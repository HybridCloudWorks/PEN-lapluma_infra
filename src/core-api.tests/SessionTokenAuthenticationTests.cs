using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using LaPluma.CoreApi.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace LaPluma.CoreApi.Tests;

/// <summary>
/// The core API's one credential is the session token it mints after a verified SAML sign-in.
/// These tests run the real request pipeline with no substituted scheme, so what passes here is
/// what passes in a deployment.
/// </summary>
public sealed class SessionTokenAuthenticationTests : IClassFixture<SessionTokenFactory>
{
    private const string ProtectedRoute = "/v1/catalog/packages";

    private readonly SessionTokenFactory _factory;

    public SessionTokenAuthenticationTests(SessionTokenFactory factory) => _factory = factory;

    public static TheoryData<string> ForgedKinds() => new(ForgedTokens.Kinds);

    [Fact]
    public async Task A_verified_SAML_sign_in_yields_a_signed_session_token_the_API_accepts()
    {
        var client = _factory.CreateClient();

        var token = await _factory.SignInAsync(client);

        Assert.DoesNotContain("lp_saml_", token, StringComparison.Ordinal);
        var jwt = new JsonWebToken(token);
        Assert.Equal(SecurityAlgorithms.RsaSha256, jwt.Alg);
        Assert.False(string.IsNullOrEmpty(jwt.Kid));
        Assert.Equal(TestHost.Issuer, jwt.Issuer);
        Assert.Equal(new[] { TestHost.Audience }, jwt.Audiences);
        Assert.Equal("tenant_hybridcloudworks", jwt.GetClaim("tenant_id").Value);
        Assert.Equal("spatino@hybridcloudworks.com", jwt.GetClaim("email").Value);
        Assert.True(jwt.ValidTo - jwt.IssuedAt <= TimeSpan.FromHours(1));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await client.GetAsync(ProtectedRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task The_token_is_accepted_from_the_header_API_Gateway_forwards_it_in()
    {
        // Through the gateway, Authorization carries the gateway's own Google-signed ID token for
        // Cloud Run IAM and the caller's header arrives as X-Forwarded-Authorization.
        var client = _factory.CreateClient();
        var token = await _factory.SignInAsync(client);
        var request = new HttpRequestMessage(HttpMethod.Get, ProtectedRoute);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "gateway-identity-token");
        request.Headers.Add(SessionTokenValidation.ForwardedAuthorizationHeader, $"Bearer {token}");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [MemberData(nameof(ForgedKinds))]
    public async Task A_forged_or_invalid_token_is_rejected(string kind)
    {
        var token = ForgedTokens.Create(kind, _factory.SigningKey, TestHost.Issuer, TestHost.Audience);
        var client = _factory.CreateClient();

        var direct = new HttpRequestMessage(HttpMethod.Get, ProtectedRoute);
        direct.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        var forwarded = new HttpRequestMessage(HttpMethod.Get, ProtectedRoute);
        forwarded.Headers.TryAddWithoutValidation(SessionTokenValidation.ForwardedAuthorizationHeader, $"Bearer {token}");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(direct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(forwarded)).StatusCode);
    }

    [Fact]
    public async Task A_rejected_assertion_yields_no_token()
    {
        var response = await _factory.CreateClient().PostAsync(
            $"/auth/saml/acs/{TestIdentityProvider.TenantId}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["SAMLResponse"] = TestIdentityProvider.Base64(TestIdentityProvider.UnsignedResponse(new AssertionSpec())),
            }));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.DoesNotContain("accessToken", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_signing_keys_are_published_anonymously_and_public_only()
    {
        var response = await _factory.CreateClient().GetAsync("/.well-known/jwks.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var keySet = new JsonWebKeySet(body);
        var key = Assert.Single(keySet.Keys);
        Assert.Equal("RSA", key.Kty);
        Assert.Equal("sig", key.Use);
        Assert.Equal(SecurityAlgorithms.RsaSha256, key.Alg);
        Assert.False(key.HasPrivateKey);
        using var document = JsonDocument.Parse(body);
        var published = document.RootElement.GetProperty("keys")[0];
        foreach (var privateParameter in new[] { "d", "p", "q", "dp", "dq", "qi" })
        {
            Assert.False(published.TryGetProperty(privateParameter, out _), privateParameter);
        }
    }

    [Fact]
    public async Task A_token_validates_against_the_published_key_set()
    {
        // What API Gateway and the workflow API do: fetch the JWK Set, then validate offline.
        var client = _factory.CreateClient();
        var token = await _factory.SignInAsync(client);
        var keySet = new JsonWebKeySet(await client.GetStringAsync("/.well-known/jwks.json"));

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidIssuer = TestHost.Issuer,
            ValidAudience = TestHost.Audience,
            IssuerSigningKeys = keySet.GetSigningKeys(),
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        });

        Assert.True(result.IsValid, result.Exception?.Message);
    }

    [Fact]
    public async Task Without_a_signing_key_the_consumer_service_issues_nothing()
    {
        using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
            builder.WithFixtureCatalog().WithAuthenticationConfigured()
                .UseSetting(SamlServiceProvider.IdentityProvidersSetting, _factory.IdentityProvider.IdentityProvidersJson()));

        var response = await factory.CreateClient().PostAsync(
            $"/auth/saml/acs/{TestIdentityProvider.TenantId}",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["SAMLResponse"] = TestIdentityProvider.Base64(_factory.IdentityProvider.SignedResponse(new AssertionSpec())),
            }));
        var keys = await factory.CreateClient().GetFromJsonAsync<JsonElement>("/.well-known/jwks.json");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(0, keys.GetProperty("keys").GetArrayLength());
    }
}

/// <summary>The signing key either loads cleanly or stops the host; it never half-works.</summary>
public sealed class SessionTokenKeyTests
{
    [Fact]
    public void An_absent_key_is_unconfigured_and_publishes_nothing()
    {
        var keys = SessionTokenKeys.FromConfiguration(new ConfigurationBuilder().Build());

        Assert.Null(keys.SigningCredentials);
        Assert.Empty(keys.ValidationKeys);
        Assert.Empty(keys.PublicKeySet.Keys);
    }

    [Fact]
    public void A_key_shorter_than_2048_bits_is_refused()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(1024);

        var error = Assert.Throws<InvalidOperationException>(() => SessionTokenKeys.FromPem(rsa.ExportPkcs8PrivateKeyPem()));
        Assert.Contains("2048", error.Message);
    }

    [Fact]
    public void A_public_key_alone_is_refused()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);

        Assert.Throws<InvalidOperationException>(() => SessionTokenKeys.FromPem(rsa.ExportSubjectPublicKeyInfoPem()));
    }

    [Fact]
    public void Garbage_is_refused()
    {
        Assert.Throws<InvalidOperationException>(() => SessionTokenKeys.FromPem("not a key"));
    }

    [Fact]
    public async Task An_issued_token_carries_the_assertion_identity_and_expires_within_an_hour()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var keys = SessionTokenKeys.FromPem(rsa.ExportPkcs8PrivateKeyPem());
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [CatalogAuthentication.IssuerSetting] = TestHost.Issuer,
                [CatalogAuthentication.AudienceSetting] = TestHost.Audience,
            })
            .Build();
        var issuer = new SessionTokenIssuer(keys, configuration, TimeProvider.System);
        var assertion = new SamlAssertion(
            Issuer: TestIdentityProvider.GoogleEntityId,
            SubjectNameId: "spatino@hybridcloudworks.com",
            TenantId: "tenant_hybridcloudworks",
            Email: "spatino@hybridcloudworks.com",
            GivenName: null,
            Surname: null,
            Roles: ["admin", "reviewer"],
            IssueInstant: DateTimeOffset.UtcNow,
            NotBefore: DateTimeOffset.UtcNow.AddMinutes(-5),
            NotOnOrAfter: DateTimeOffset.UtcNow.AddMinutes(5),
            Audience: TestIdentityProvider.SpEntityId);

        var exchange = issuer.Issue(assertion);

        Assert.Equal("Bearer", exchange.TokenType);
        Assert.Equal(3600, exchange.ExpiresIn);
        Assert.Equal("tenant_hybridcloudworks", exchange.TenantId);
        Assert.Equal("spatino@hybridcloudworks.com", exchange.UserId);
        var principal = await new JsonWebTokenHandler().ValidateTokenAsync(exchange.AccessToken, new TokenValidationParameters
        {
            ValidIssuer = TestHost.Issuer,
            ValidAudience = TestHost.Audience,
            IssuerSigningKeys = keys.ValidationKeys,
        });
        Assert.True(principal.IsValid, principal.Exception?.Message);
        var identity = principal.ClaimsIdentity;
        Assert.Equal("tenant_hybridcloudworks", identity.FindFirst("tenant_id")?.Value);
        Assert.Equal(new[] { "admin", "reviewer" }, identity.FindAll(c => c.Type is "role" or ClaimTypes.Role).Select(c => c.Value));
    }
}
