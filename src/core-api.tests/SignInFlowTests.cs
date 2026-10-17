using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LaPluma.CoreApi.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace LaPluma.CoreApi.Tests;

/// <summary>
/// The native-app sign-in contract (INT-22) through the real pipeline: login with PKCE and a
/// registered redirect URI, the IdP's answer turned into a one-time code on a 302, and the code
/// redeemed with its verifier for the session token. The token itself never appears in a URL.
/// </summary>
public sealed class SignInFlowTests : IClassFixture<SessionTokenFactory>
{
    private readonly SessionTokenFactory _factory;

    public SignInFlowTests(SessionTokenFactory factory) => _factory = factory;

    [Fact]
    public async Task A_sign_in_redirects_with_a_code_and_the_code_buys_a_session_token()
    {
        using var client = _factory.CreateSignInClient();
        var started = await SessionTokenFactory.StartSignInAsync(client);

        var callback = await _factory.CompleteSignInAsync(client, started);

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("no-store", callback.Headers.CacheControl?.ToString());
        var location = callback.Headers.Location!;
        Assert.StartsWith(SessionTokenFactory.RedirectUri + "?", location.OriginalString, StringComparison.Ordinal);
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal(started.State, query["state"].ToString());
        var code = query["code"].ToString();
        Assert.StartsWith("lpc1.", code, StringComparison.Ordinal);
        // Neither the session token nor the email address travels in the redirect.
        Assert.DoesNotContain("eyJ", location.OriginalString, StringComparison.Ordinal);
        Assert.DoesNotContain("hybridcloudworks.com", location.OriginalString, StringComparison.OrdinalIgnoreCase);

        var exchange = await SessionTokenFactory.RedeemAsync(client, code, started.Pkce.Verifier);

        Assert.Equal(HttpStatusCode.OK, exchange.StatusCode);
        Assert.Equal("no-store", exchange.Headers.CacheControl?.ToString());
        var body = await exchange.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Bearer", body.GetProperty("tokenType").GetString());
        Assert.Equal(3600, body.GetProperty("expiresIn").GetInt32());
        Assert.Equal(TestIdentityProvider.TenantId, body.GetProperty("tenantId").GetString());
        Assert.Equal("spatino@hybridcloudworks.com", body.GetProperty("email").GetString());
        var token = body.GetProperty("accessToken").GetString()!;
        Assert.Equal(SecurityAlgorithms.RsaSha256, new JsonWebToken(token).Alg);

        using var api = _factory.CreateClient();
        api.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.OK, (await api.GetAsync("/v1/catalog/packages")).StatusCode);
    }

    [Fact]
    public async Task A_code_is_single_use()
    {
        using var client = _factory.CreateSignInClient();
        var (code, verifier) = await CodeAsync(client);

        var first = await SessionTokenFactory.RedeemAsync(client, code, verifier);
        var second = await SessionTokenFactory.RedeemAsync(client, code, verifier);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        await AssertProblemAsync(second, "auth-code-replayed");
    }

    [Fact]
    public async Task A_code_needs_the_verifier_its_sign_in_started_with_and_a_wrong_one_does_not_use_it_up()
    {
        using var client = _factory.CreateSignInClient();
        var (code, verifier) = await CodeAsync(client);

        var wrong = await SessionTokenFactory.RedeemAsync(client, code, PkcePair.Create().Verifier);
        var right = await SessionTokenFactory.RedeemAsync(client, code, verifier);

        await AssertProblemAsync(wrong, "code-verifier-mismatch");
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
    }

    [Fact]
    public async Task A_code_is_bound_to_the_redirect_uri_its_sign_in_used()
    {
        using var client = _factory.CreateSignInClient();
        var (code, verifier) = await CodeAsync(client);

        var response = await SessionTokenFactory.RedeemAsync(client, code, verifier, redirectUri: "lapluma://auth/other");

        await AssertProblemAsync(response, "redirect-uri-mismatch");
    }

    [Theory]
    [InlineData("tampered")]
    [InlineData("truncated")]
    [InlineData("no-prefix")]
    [InlineData("garbage")]
    [InlineData("empty")]
    public async Task A_tampered_or_malformed_code_is_refused(string kind)
    {
        using var client = _factory.CreateSignInClient();
        var (code, verifier) = await CodeAsync(client);
        var middle = code.Length / 2;
        var forged = kind switch
        {
            "tampered" => code[..middle] + (code[middle] == 'A' ? 'B' : 'A') + code[(middle + 1)..],
            "truncated" => code[..(code.Length - 4)],
            "no-prefix" => code["lpc1.".Length..],
            "garbage" => "lpc1.%%%not-base64%%%",
            _ => "",
        };

        var response = await SessionTokenFactory.RedeemAsync(client, forged, verifier);

        await AssertProblemAsync(response, "auth-code-invalid");
    }

    [Fact]
    public async Task A_code_from_another_deployment_is_refused()
    {
        using var other = new SessionTokenFactory();
        using var otherClient = other.CreateSignInClient();
        var started = await SessionTokenFactory.StartSignInAsync(otherClient);
        var callback = await other.CompleteSignInAsync(otherClient, started);
        var code = QueryHelpers.ParseQuery(callback.Headers.Location!.Query)["code"].ToString();

        using var client = _factory.CreateSignInClient();
        var response = await SessionTokenFactory.RedeemAsync(client, code, started.Pkce.Verifier);

        await AssertProblemAsync(response, "auth-code-invalid");
    }

    [Fact]
    public async Task A_code_expires_after_sixty_seconds()
    {
        using var factory = new SessionTokenFactory();
        using var client = factory.CreateSignInClient();
        var started = await SessionTokenFactory.StartSignInAsync(client);
        var callback = await factory.CompleteSignInAsync(client, started);
        var code = QueryHelpers.ParseQuery(callback.Headers.Location!.Query)["code"].ToString();

        factory.Clock.Advance(SignInFlow.CodeLifetime + TimeSpan.FromSeconds(1));
        var response = await SessionTokenFactory.RedeemAsync(client, code, started.Pkce.Verifier);

        await AssertProblemAsync(response, "auth-code-expired");
    }

    [Theory]
    [InlineData(null, "auth-code-invalid")]
    [InlineData("password", "unsupported-grant-type")]
    public async Task A_token_request_without_a_code_or_with_another_grant_is_refused(string? grantType, string expected)
    {
        using var client = _factory.CreateSignInClient();
        var verifier = PkcePair.Create().Verifier;

        var response = grantType is null
            ? await SessionTokenFactory.RedeemAsync(client, code: null, verifier)
            : await SessionTokenFactory.RedeemAsync(client, "lpc1.x", verifier, grantType: grantType);

        await AssertProblemAsync(response, expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("short")]
    [InlineData("has spaces in it but is long enough to pass the length rule.....")]
    public async Task A_malformed_code_verifier_is_refused(string? verifier)
    {
        using var client = _factory.CreateSignInClient();
        var (code, _) = await CodeAsync(client);

        var response = await SessionTokenFactory.RedeemAsync(client, code, verifier);

        await AssertProblemAsync(response, "code-verifier-invalid");
    }

    [Fact]
    public async Task The_token_endpoint_also_accepts_JSON()
    {
        using var client = _factory.CreateSignInClient();
        var (code, verifier) = await CodeAsync(client);

        var response = await client.PostAsJsonAsync("/auth/token", new
        {
            grantType = "authorization_code",
            code,
            codeVerifier = verifier,
            redirectUri = SessionTokenFactory.RedirectUri,
        });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public static TheoryData<string?, string?, string?, string?, string> InvalidLogins()
    {
        var challenge = PkcePair.Create().Challenge;
        const string state = "0123456789abcdefABCDEF";
        const string redirect = SessionTokenFactory.RedirectUri;
        return new TheoryData<string?, string?, string?, string?, string>
        {
            { null, challenge, "S256", state, "redirect-uri-not-allowed" },
            { "https://evil.example.invalid/callback", challenge, "S256", state, "redirect-uri-not-allowed" },
            { redirect + "x", challenge, "S256", state, "redirect-uri-not-allowed" },
            { redirect + "/../steal", challenge, "S256", state, "redirect-uri-not-allowed" },
            { redirect + "?next=evil", challenge, "S256", state, "redirect-uri-not-allowed" },
            { "LAPLUMA://auth/callback", challenge, "S256", state, "redirect-uri-not-allowed" },
            { redirect, challenge, null, state, "code-challenge-method-unsupported" },
            { redirect, challenge, "plain", state, "code-challenge-method-unsupported" },
            { redirect, null, "S256", state, "code-challenge-invalid" },
            { redirect, challenge[..42], "S256", state, "code-challenge-invalid" },
            { redirect, challenge[..42] + "=", "S256", state, "code-challenge-invalid" },
            { redirect, challenge, "S256", null, "state-invalid" },
            { redirect, challenge, "S256", "tooshort", "state-invalid" },
            { redirect, challenge, "S256", "has spaces in it, sixteen+", "state-invalid" },
            { redirect, challenge, "S256", new string('a', 65), "state-invalid" },
        };
    }

    [Theory]
    [MemberData(nameof(InvalidLogins))]
    public async Task A_login_without_a_registered_redirect_uri_and_valid_PKCE_and_state_is_refused_without_redirecting(
        string? redirectUri, string? challenge, string? method, string? state, string expected)
    {
        using var client = _factory.CreateSignInClient();

        var response = await client.GetAsync(SessionTokenFactory.LoginUrl(redirectUri, challenge, method, state));

        await AssertProblemAsync(response, expected);
        Assert.Null(response.Headers.Location);
    }

    [Fact]
    public async Task Each_login_sends_its_own_AuthnRequest_ID_and_RelayState()
    {
        using var client = _factory.CreateSignInClient();

        var first = await SessionTokenFactory.StartSignInAsync(client);
        var second = await SessionTokenFactory.StartSignInAsync(client);

        Assert.StartsWith("_", first.RequestId, StringComparison.Ordinal);
        Assert.NotEqual(first.RequestId, second.RequestId);
        Assert.NotEqual(first.RelayState, second.RelayState);
        // Entra ID limits the redirect query to 4,096 characters; RelayState must stay small.
        Assert.InRange(first.RelayState.Length, 1, 400);
    }

    [Fact]
    public async Task An_assertion_without_RelayState_is_refused_and_not_consumed()
    {
        using var client = _factory.CreateSignInClient();
        var started = await SessionTokenFactory.StartSignInAsync(client);
        var assertion = _factory.IdentityProvider.SignedResponse(new AssertionSpec { InResponseTo = started.RequestId });

        var withoutState = await SessionTokenFactory.PostAssertionAsync(client, assertion, relayState: null);
        var withState = await SessionTokenFactory.PostAssertionAsync(client, assertion, started.RelayState);

        await AssertProblemAsync(withoutState, "relay-state-missing");
        Assert.Equal(HttpStatusCode.Redirect, withState.StatusCode);
        Assert.Contains("code=", withState.Headers.Location!.OriginalString, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("tag")]
    [InlineData("no-separator")]
    public async Task Tampered_RelayState_is_refused(string part)
    {
        using var client = _factory.CreateSignInClient();
        var started = await SessionTokenFactory.StartSignInAsync(client);
        var dot = started.RelayState.IndexOf('.');
        var index = part == "payload" ? dot / 2 : dot + 3;
        var tampered = part == "no-separator"
            ? started.RelayState.Replace(".", "", StringComparison.Ordinal)
            : started.RelayState[..index] + (started.RelayState[index] == 'A' ? 'B' : 'A') + started.RelayState[(index + 1)..];

        var response = await _factory.CompleteSignInAsync(client, started, relayState: tampered);

        await AssertProblemAsync(response, "relay-state-invalid");
    }

    [Fact]
    public async Task RelayState_is_bound_to_the_tenant_its_sign_in_resolved()
    {
        using var client = _factory.CreateSignInClient();
        var started = await SessionTokenFactory.StartSignInAsync(client);

        var response = await _factory.CompleteSignInAsync(client, started, tenantId: "tenant_other_institution");

        await AssertProblemAsync(response, "relay-state-invalid");
    }

    [Fact]
    public async Task RelayState_from_another_deployment_is_refused()
    {
        using var other = new SessionTokenFactory();
        using var otherClient = other.CreateSignInClient();
        var foreign = await SessionTokenFactory.StartSignInAsync(otherClient);
        using var client = _factory.CreateSignInClient();

        var response = await _factory.CompleteSignInAsync(client, foreign);

        await AssertProblemAsync(response, "relay-state-invalid");
    }

    [Fact]
    public async Task RelayState_expires_after_ten_minutes()
    {
        using var factory = new SessionTokenFactory();
        using var client = factory.CreateSignInClient();
        var started = await SessionTokenFactory.StartSignInAsync(client);

        factory.Clock.Advance(SignInFlow.TransactionLifetime + TimeSpan.FromSeconds(1));
        var response = await factory.CompleteSignInAsync(client, started);

        await AssertProblemAsync(response, "relay-state-expired");
    }

    [Theory]
    [InlineData("_a-different-request")]
    [InlineData("")]
    public async Task An_assertion_that_does_not_answer_this_sign_in_goes_back_as_an_error(string inResponseTo)
    {
        using var client = _factory.CreateSignInClient();
        var started = await SessionTokenFactory.StartSignInAsync(client);

        var response = await _factory.CompleteSignInAsync(client, started, new AssertionSpec { InResponseTo = inResponseTo });

        AssertErrorRedirect(response, started, "unsolicited-response");
    }

    [Fact]
    public async Task A_rejected_assertion_goes_back_to_the_app_with_its_state_and_no_code()
    {
        using var client = _factory.CreateSignInClient();
        var started = await SessionTokenFactory.StartSignInAsync(client);

        var response = await _factory.CompleteSignInAsync(client, started, signed: false);

        AssertErrorRedirect(response, started, "unsigned-assertion");
    }

    [Fact]
    public async Task Without_a_signing_key_sign_in_does_not_start()
    {
        using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
            builder.WithFixtureCatalog().WithAuthenticationConfigured()
                .UseSetting(SamlServiceProvider.IdentityProvidersSetting, _factory.IdentityProvider.IdentityProvidersJson()));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var pkce = PkcePair.Create();

        var login = await client.GetAsync(
            SessionTokenFactory.LoginUrl(SessionTokenFactory.RedirectUri, pkce.Challenge, "S256", "0123456789abcdefABCDEF"));
        var token = await SessionTokenFactory.RedeemAsync(client, "lpc1.x", pkce.Verifier);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, login.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, token.StatusCode);
    }

    [Fact]
    public async Task A_configured_redirect_uri_list_replaces_the_default()
    {
        using var baseFactory = new SessionTokenFactory();
        using var factory = baseFactory.WithWebHostBuilder(builder =>
            builder.UseSetting($"{SignInFlow.RedirectUrisSetting}:0", "https://app.lapluma.example/auth/callback"));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var pkce = PkcePair.Create();

        var configured = await client.GetAsync(SessionTokenFactory.LoginUrl(
            "https://app.lapluma.example/auth/callback", pkce.Challenge, "S256", "0123456789abcdefABCDEF"));
        var defaultUri = await client.GetAsync(SessionTokenFactory.LoginUrl(
            SessionTokenFactory.RedirectUri, pkce.Challenge, "S256", "0123456789abcdefABCDEF"));

        Assert.Equal(HttpStatusCode.Redirect, configured.StatusCode);
        await AssertProblemAsync(defaultUri, "redirect-uri-not-allowed");
    }

    [Theory]
    [InlineData("http://app.lapluma.example/callback")]
    [InlineData("javascript://auth/alert")]
    [InlineData("lapluma://auth/callback?x=1")]
    [InlineData("lapluma://auth/callback#fragment")]
    [InlineData("lapluma:callback")]
    [InlineData("not a uri")]
    public void An_unusable_redirect_uri_setting_stops_the_host(string value)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [$"{SignInFlow.RedirectUrisSetting}:0"] = value })
            .Build();
        using var rsa = System.Security.Cryptography.RSA.Create(2048);

        Assert.Throws<InvalidOperationException>(() =>
            new SignInFlow(SessionTokenKeys.FromPem(rsa.ExportPkcs8PrivateKeyPem()), configuration, TimeProvider.System));
    }

    [Fact]
    public void Two_instances_with_the_same_signing_key_derive_the_same_keys()
    {
        // What makes the flow stateless across Cloud Run instances.
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var pem = rsa.ExportPkcs8PrivateKeyPem();
        var pkcs1 = rsa.ExportRSAPrivateKeyPem();

        var first = SessionTokenKeys.FromPem(pem).DeriveKey("purpose");
        var second = SessionTokenKeys.FromPem(pkcs1).DeriveKey("purpose");
        var otherPurpose = SessionTokenKeys.FromPem(pem).DeriveKey("another purpose");

        Assert.NotNull(first);
        Assert.Equal(32, first.Length);
        Assert.Equal(first, second);
        Assert.NotEqual(first, otherPurpose);
        Assert.Null(SessionTokenKeys.FromPem(null).DeriveKey("purpose"));
    }

    private async Task<(string Code, string Verifier)> CodeAsync(HttpClient client)
    {
        var started = await SessionTokenFactory.StartSignInAsync(client);
        var callback = await _factory.CompleteSignInAsync(client, started);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        return (QueryHelpers.ParseQuery(callback.Headers.Location!.Query)["code"].ToString(), started.Pkce.Verifier);
    }

    private static void AssertErrorRedirect(HttpResponseMessage response, StartedSignIn started, string errorCode)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith(started.RedirectUri + "?", location.OriginalString, StringComparison.Ordinal);
        var query = QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("access_denied", query["error"].ToString());
        Assert.Equal(errorCode, query["error_description"].ToString());
        Assert.Equal(started.State, query["state"].ToString());
        Assert.False(query.ContainsKey("code"));
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, string type)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal($"urn:lapluma:problem:{type}", problem.GetProperty("type").GetString());
    }
}
