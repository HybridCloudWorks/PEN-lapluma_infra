using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LaPluma.CoreApi.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace LaPluma.CoreApi.Tests;

/// <summary>
/// The core API exactly as a deployment configures it — issuer, audience, signing key, and an
/// identity provider — with no substituted authentication scheme. Every key and certificate is
/// generated per factory; none is checked in.
/// </summary>
public sealed class SessionTokenFactory : WebApplicationFactory<global::Program>
{
    internal TestIdentityProvider IdentityProvider { get; } = new();

    internal RSA SigningKey { get; } = RSA.Create(2048);

    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.WithFixtureCatalog()
            .WithAuthenticationConfigured()
            .UseSetting(SessionTokenKeys.SigningKeySetting, SigningKey.ExportPkcs8PrivateKeyPem())
            .UseSetting(SamlServiceProvider.IdentityProvidersSetting, IdentityProvider.IdentityProvidersJson());

    /// <summary>Signs in through the assertion consumer service and returns the session token.</summary>
    internal async Task<string> SignInAsync(HttpClient client, AssertionSpec? spec = null)
    {
        var response = await client.PostAsync(
            $"/auth/saml/acs/{TestIdentityProvider.TenantId}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["SAMLResponse"] = TestIdentityProvider.Base64(IdentityProvider.SignedResponse(spec ?? new AssertionSpec())),
            }));
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return body.GetProperty("accessToken").GetString()!;
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
