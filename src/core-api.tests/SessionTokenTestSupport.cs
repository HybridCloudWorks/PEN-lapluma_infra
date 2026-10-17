using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using LaPluma.CoreApi.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace LaPluma.CoreApi.Tests;

/// <summary>
/// The core API exactly as a deployment configures it — issuer, audience, signing key, and an
/// identity provider — with no substituted authentication scheme. Every key and certificate is
/// generated per factory; none is checked in.
/// </summary>
public sealed class SessionTokenFactory : WebApplicationFactory<global::Program>
{
    public const string RedirectUri = SignInFlow.DefaultRedirectUri;

    internal TestIdentityProvider IdentityProvider { get; } = new();

    internal RSA SigningKey { get; } = RSA.Create(2048);

    /// <summary>The service's clock. Real time unless a test moves it forward.</summary>
    internal MovableClock Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.WithFixtureCatalog()
            .WithAuthenticationConfigured()
            .UseSetting(SessionTokenKeys.SigningKeySetting, SigningKey.ExportPkcs8PrivateKeyPem())
            .UseSetting(SamlServiceProvider.IdentityProvidersSetting, IdentityProvider.IdentityProvidersJson())
            .ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Clock));

    /// <summary>A client that reports redirects instead of following them, as the sign-in steps need.</summary>
    internal HttpClient CreateSignInClient() =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    /// <summary>
    /// The whole native-app sign-in: start at /auth/saml/login, answer the AuthnRequest with a
    /// signed assertion, follow the 302 back with the code, and redeem it with the verifier.
    /// </summary>
    internal async Task<string> SignInAsync(AssertionSpec? spec = null)
    {
        using var client = CreateSignInClient();
        var started = await StartSignInAsync(client);
        var callback = await CompleteSignInAsync(client, started, spec);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        var code = QueryHelpers.ParseQuery(callback.Headers.Location!.Query)["code"].ToString();

        var response = await RedeemAsync(client, code, started.Pkce.Verifier);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("accessToken").GetString()!;
    }

    /// <summary>Starts a sign-in and reads back what the IdP would receive.</summary>
    internal static async Task<StartedSignIn> StartSignInAsync(
        HttpClient client, PkcePair? pkce = null, string? state = null, string redirectUri = RedirectUri)
    {
        pkce ??= PkcePair.Create();
        state ??= WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(24));
        var response = await client.GetAsync(LoginUrl(redirectUri, pkce.Challenge, "S256", state));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var query = QueryHelpers.ParseQuery(response.Headers.Location!.Query);
        var request = new XmlDocument { XmlResolver = null };
        request.LoadXml(RawInflate(Convert.FromBase64String(query["SAMLRequest"].ToString())));
        return new StartedSignIn(
            RequestId: request.DocumentElement!.GetAttribute("ID"),
            RelayState: query["RelayState"].ToString(),
            State: state,
            Pkce: pkce,
            RedirectUri: redirectUri);
    }

    internal static string LoginUrl(string? redirectUri, string? challenge, string? method, string? state)
    {
        var query = new Dictionary<string, string?> { ["domain"] = "hybridcloudworks.com" };
        if (redirectUri is not null) query["redirect_uri"] = redirectUri;
        if (challenge is not null) query["code_challenge"] = challenge;
        if (method is not null) query["code_challenge_method"] = method;
        if (state is not null) query["state"] = state;
        return QueryHelpers.AddQueryString("/auth/saml/login", query);
    }

    /// <summary>
    /// Posts a signed assertion that answers <paramref name="started"/>'s AuthnRequest unless
    /// <paramref name="spec"/> says otherwise, with its RelayState unless one is given.
    /// </summary>
    internal Task<HttpResponseMessage> CompleteSignInAsync(
        HttpClient client,
        StartedSignIn started,
        AssertionSpec? spec = null,
        string? relayState = null,
        string tenantId = TestIdentityProvider.TenantId,
        bool signed = true)
    {
        spec ??= new AssertionSpec();
        if (spec.InResponseTo is null)
        {
            spec = spec with { InResponseTo = started.RequestId };
        }

        var response = signed ? IdentityProvider.SignedResponse(spec) : TestIdentityProvider.UnsignedResponse(spec);
        return PostAssertionAsync(client, response, relayState ?? started.RelayState, tenantId);
    }

    internal static Task<HttpResponseMessage> PostAssertionAsync(
        HttpClient client, string responseXml, string? relayState, string tenantId = TestIdentityProvider.TenantId)
    {
        var fields = new Dictionary<string, string> { ["SAMLResponse"] = TestIdentityProvider.Base64(responseXml) };
        if (relayState is not null)
        {
            fields["RelayState"] = relayState;
        }

        return client.PostAsync($"/auth/saml/acs/{tenantId}", new FormUrlEncodedContent(fields));
    }

    internal static Task<HttpResponseMessage> RedeemAsync(
        HttpClient client, string? code, string? verifier, string? redirectUri = RedirectUri, string? grantType = "authorization_code")
    {
        var fields = new Dictionary<string, string>();
        if (grantType is not null) fields["grant_type"] = grantType;
        if (code is not null) fields["code"] = code;
        if (verifier is not null) fields["code_verifier"] = verifier;
        if (redirectUri is not null) fields["redirect_uri"] = redirectUri;
        return client.PostAsync("/auth/token", new FormUrlEncodedContent(fields));
    }

    private static string RawInflate(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var deflate = new DeflateStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(deflate, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            IdentityProvider.Dispose();
            SigningKey.Dispose();
        }
    }
}

/// <summary>An RFC 7636 verifier and its S256 challenge.</summary>
internal sealed record PkcePair(string Verifier, string Challenge)
{
    public static PkcePair Create()
    {
        var verifier = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        return new PkcePair(verifier, WebEncoders.Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))));
    }
}

/// <summary>What the app holds, and what the IdP was sent, after starting a sign-in.</summary>
internal sealed record StartedSignIn(string RequestId, string RelayState, string State, PkcePair Pkce, string RedirectUri);

/// <summary>Real time plus whatever a test has moved it forward by.</summary>
internal sealed class MovableClock : TimeProvider
{
    private long _offsetTicks;

    public void Advance(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);

    public override DateTimeOffset GetUtcNow() => base.GetUtcNow().AddTicks(Interlocked.Read(ref _offsetTicks));
}

/// <summary>Tokens an attacker could build, each of which must be refused.</summary>
internal static class ForgedTokens
{
    public static readonly string[] Kinds =
    [
        "fixed-test-token",
        "test-prefix-token",
        "legacy-saml-pipe",
        "legacy-saml-colon",
        "unsigned-google-issuer",
        "google-issuer-wrong-key",
        "wrong-key",
        "expired",
        "wrong-audience",
        "wrong-issuer",
        "algorithm-none",
        "hmac-with-public-key",
    ];

    /// <summary>Builds the forged credential named by <paramref name="kind"/> against a host signing with <paramref name="genuineKey"/>.</summary>
    public static string Create(string kind, RSA genuineKey, string issuer, string audience)
    {
        var now = DateTime.UtcNow;
        using var otherKey = RSA.Create(2048);
        return kind switch
        {
            "fixed-test-token" => "test-caller",
            "test-prefix-token" => "lp_test_x",
            "legacy-saml-pipe" => "lp_saml_" + Convert.ToBase64String(Encoding.UTF8.GetBytes("tenant_hybridcloudworks|attacker@hybridcloudworks.com")),
            "legacy-saml-colon" => "lp_saml_" + Convert.ToBase64String(Encoding.UTF8.GetBytes(
                $"tenant_hybridcloudworks:admin@hybridcloudworks.com:admin,reviewer:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}")),
            "unsigned-google-issuer" => Unsigned("https://accounts.google.com", audience, now),
            "google-issuer-wrong-key" => Sign(otherKey, "https://accounts.google.com", audience, now, now.AddHours(1)),
            "wrong-key" => Sign(otherKey, issuer, audience, now, now.AddHours(1)),
            "expired" => Sign(genuineKey, issuer, audience, now.AddHours(-2), now.AddHours(-1)),
            "wrong-audience" => Sign(genuineKey, issuer, "api://some-other-service", now, now.AddHours(1)),
            "wrong-issuer" => Sign(genuineKey, "https://issuer.example.invalid", audience, now, now.AddHours(1)),
            "algorithm-none" => Unsigned(issuer, audience, now),
            "hmac-with-public-key" => HmacWithPublicKey(genuineKey, issuer, audience, now),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
    }

    public static string Sign(RSA key, string issuer, string audience, DateTime notBefore, DateTime expires) =>
        new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = notBefore,
            NotBefore = notBefore,
            Expires = expires,
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(key), SecurityAlgorithms.RsaSha256),
            Claims = Claims(),
        });

    private static string Unsigned(string issuer, string audience, DateTime now) =>
        new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddHours(1),
            Claims = Claims(),
        });

    private static string HmacWithPublicKey(RSA genuineKey, string issuer, string audience, DateTime now)
    {
        // Algorithm confusion: the public key, which anyone can fetch, used as an HMAC secret.
        var secret = genuineKey.ExportSubjectPublicKeyInfo();
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = now,
            NotBefore = now,
            Expires = now.AddHours(1),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(secret), SecurityAlgorithms.HmacSha256),
            Claims = Claims(),
        });
    }

    private static Dictionary<string, object> Claims() => new()
    {
        ["sub"] = "attacker@hybridcloudworks.com",
        ["email"] = "attacker@hybridcloudworks.com",
        ["tenant_id"] = "tenant_hybridcloudworks",
        ["role"] = new[] { "caseworker", "preparer", "reviewer" },
    };
}
