using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace LaPluma.WorkflowApi.Tests;

/// <summary>
/// The workflow API exactly as a deployment configures it, with no substituted scheme. The only
/// thing replaced is the network: the key-set address is answered in process with a JWK Set for
/// a key generated per factory, the same document the core API publishes.
/// </summary>
public sealed class JwksBackedFactory : WebApplicationFactory<global::Program>
{
    public RSA SigningKey { get; } = RSA.Create(2048);

    public string KeyId { get; } = Guid.NewGuid().ToString("N");

    public StaticResponseHandler KeySet { get; }

    public JwksBackedFactory() => KeySet = new StaticResponseHandler(() => JwksFor(SigningKey, KeyId));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.WithFixtureWorkflow().WithAuthenticationConfigured();
        builder.ConfigureServices(services =>
            services.AddHttpClient(WorkflowAuthentication.JwksHttpClientName)
                .ConfigurePrimaryHttpMessageHandler(() => KeySet));
    }

    public string Mint(
        string? issuer = null,
        string? audience = null,
        DateTime? notBefore = null,
        DateTime? expires = null,
        RSA? key = null,
        string subject = "spatino@hybridcloudworks.com")
    {
        var start = notBefore ?? DateTime.UtcNow;
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer ?? TestHost.Issuer,
            Audience = audience ?? TestHost.Audience,
            IssuedAt = start,
            NotBefore = start,
            Expires = expires ?? start.AddHours(1),
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(key ?? SigningKey) { KeyId = key is null ? KeyId : "unknown-key" },
                SecurityAlgorithms.RsaSha256),
            Claims = new Dictionary<string, object>
            {
                ["sub"] = subject,
                ["email"] = subject,
                ["tenant_id"] = "tenant_hybridcloudworks",
                ["role"] = new[] { "caseworker", "preparer" },
            },
        });
    }

    public static string JwksFor(RSA key, string keyId)
    {
        var parameters = key.ExportParameters(includePrivateParameters: false);
        return JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA",
                    use = "sig",
                    alg = "RS256",
                    kid = keyId,
                    n = Base64UrlEncoder.Encode(parameters.Modulus),
                    e = Base64UrlEncoder.Encode(parameters.Exponent),
                },
            },
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            SigningKey.Dispose();
        }
    }
}

/// <summary>Answers every request with one body, or fails every request when the body is null.</summary>
public sealed class StaticResponseHandler(Func<string?> body) : HttpMessageHandler
{
    private int requests;

    public int Requests => requests;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref requests);
        var content = body() ?? throw new HttpRequestException("Key set unreachable.");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(content, Encoding.UTF8, "application/json"),
        });
    }
}

public sealed class SessionTokenAuthenticationTests : IClassFixture<JwksBackedFactory>
{
    private const string ProtectedRoute = "/v1/session";

    private readonly JwksBackedFactory _factory;

    public SessionTokenAuthenticationTests(JwksBackedFactory factory) => _factory = factory;

    [Fact]
    public async Task A_session_token_signed_by_the_published_key_is_accepted()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _factory.Mint());

        var response = await client.GetAsync(ProtectedRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("spatino@hybridcloudworks.com", body.GetProperty("userID").GetString());
        Assert.True(_factory.KeySet.Requests > 0);
    }

    [Fact]
    public async Task The_token_is_accepted_from_the_header_API_Gateway_forwards_it_in()
    {
        var request = new HttpRequestMessage(HttpMethod.Get, ProtectedRoute);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "gateway-identity-token");
        request.Headers.Add(SessionTokenValidation.ForwardedAuthorizationHeader, $"Bearer {_factory.Mint()}");

        var response = await _factory.CreateClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public static TheoryData<string> ForgedKinds() => new(
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
        "algorithm-none");

    [Theory]
    [MemberData(nameof(ForgedKinds))]
    public async Task A_forged_or_invalid_token_is_rejected(string kind)
    {
        using var otherKey = RSA.Create(2048);
        var now = DateTime.UtcNow;
        var token = kind switch
        {
            "fixed-test-token" => "test-caller",
            "test-prefix-token" => "lp_test_x",
            "legacy-saml-pipe" => "lp_saml_" + Convert.ToBase64String(Encoding.UTF8.GetBytes("tenant_hybridcloudworks|attacker@hybridcloudworks.com")),
            "legacy-saml-colon" => "lp_saml_" + Convert.ToBase64String(Encoding.UTF8.GetBytes("tenant_hybridcloudworks:admin@hybridcloudworks.com:admin:0")),
            "unsigned-google-issuer" => Unsigned("https://accounts.google.com"),
            "google-issuer-wrong-key" => _factory.Mint(issuer: "https://accounts.google.com", key: otherKey),
            "wrong-key" => _factory.Mint(key: otherKey),
            "expired" => _factory.Mint(notBefore: now.AddHours(-2), expires: now.AddHours(-1)),
            "wrong-audience" => _factory.Mint(audience: "api://some-other-service"),
            "wrong-issuer" => _factory.Mint(issuer: "https://issuer.example.invalid"),
            "algorithm-none" => Unsigned(TestHost.Issuer),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var client = _factory.CreateClient();

        var direct = new HttpRequestMessage(HttpMethod.Get, ProtectedRoute);
        direct.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        var forwarded = new HttpRequestMessage(HttpMethod.Get, ProtectedRoute);
        forwarded.Headers.TryAddWithoutValidation(SessionTokenValidation.ForwardedAuthorizationHeader, $"Bearer {token}");

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(direct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(forwarded)).StatusCode);
    }

    [Fact]
    public async Task An_unreachable_key_set_fails_closed()
    {
        using var unreachable = new UnreachableKeySetFactory();
        var client = unreachable.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", unreachable.Mint());

        var response = await client.GetAsync(ProtectedRoute);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void A_key_set_address_that_is_not_https_stops_the_host()
    {
        using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
            builder.WithFixtureWorkflow().WithAuthenticationConfigured()
                .UseSetting(WorkflowAuthentication.JwksUriSetting, "http://login.example.invalid/.well-known/jwks.json"));

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains(WorkflowAuthentication.JwksUriSetting, error.ToString());
    }

    private static string Unsigned(string issuer) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = TestHost.Audience,
            Expires = DateTime.UtcNow.AddHours(1),
            Claims = new Dictionary<string, object> { ["sub"] = "attacker@hybridcloudworks.com" },
        });

    private sealed class UnreachableKeySetFactory : WebApplicationFactory<global::Program>
    {
        private readonly RSA key = RSA.Create(2048);

        public string Mint() =>
            new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = TestHost.Issuer,
                Audience = TestHost.Audience,
                Expires = DateTime.UtcNow.AddHours(1),
                SigningCredentials = new SigningCredentials(new RsaSecurityKey(key) { KeyId = "k" }, SecurityAlgorithms.RsaSha256),
            });

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.WithFixtureWorkflow().WithAuthenticationConfigured();
            builder.ConfigureServices(services =>
                services.AddHttpClient(WorkflowAuthentication.JwksHttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new StaticResponseHandler(() => null)));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (disposing)
            {
                key.Dispose();
            }
        }
    }
}
