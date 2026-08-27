using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LaPluma.WorkflowApi.Tests;

/// <summary>
/// Authenticates every request, so tests about workflow behaviour stay about workflow behaviour.
///
/// Deliberately not a way to skip authorization: the policy still has to pass, which is why
/// <see cref="AuthenticatedFactory"/> also supplies an audience and issuer. A factory that
/// authenticates but leaves those unset is denied, and a test asserts exactly that.
/// </summary>
internal sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Test";
    public const string CallerId = "test-caller";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, CallerId)], SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

internal static class TestHost
{
    // Values that are obviously not real. login.example.invalid cannot resolve, which is the point:
    // if a test ever causes the JWT handler to fetch OIDC metadata, it fails loudly here rather
    // than reaching something.
    public const string Audience = "api://lapluma-tests";
    public const string Issuer = "https://login.example.invalid/tenant/v2.0";

    /// <summary>
    /// Select the fixture store explicitly. There is no default source at all — the registration
    /// throws — so every host, test or real, has to state which store it serves.
    /// </summary>
    public static IWebHostBuilder WithFixtureWorkflow(this IWebHostBuilder builder)
    {
        builder.UseSetting(
            WorkflowSourceRegistration.SourceSetting, WorkflowSourceRegistration.FixtureSource);
        return builder;
    }

    /// <summary>Supply an audience and issuer, so the policy is configured rather than failing closed.</summary>
    public static IWebHostBuilder WithAuthenticationConfigured(this IWebHostBuilder builder)
    {
        builder.UseSetting(WorkflowAuthentication.AudienceSetting, Audience);
        builder.UseSetting(WorkflowAuthentication.IssuerSetting, Issuer);
        return builder;
    }

    /// <summary>Make the always-succeeds scheme the default, standing in for a token from the edge.</summary>
    public static IWebHostBuilder WithTestAuthentication(this IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
            services.AddAuthentication(TestAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                    TestAuthHandler.SchemeName, _ => { }));
        return builder;
    }
}

/// <summary>Configured and authenticated: the state a request arriving from APIM would be in.</summary>
public sealed class AuthenticatedFactory : WebApplicationFactory<global::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.WithFixtureWorkflow().WithAuthenticationConfigured().WithTestAuthentication();
}

/// <summary>
/// Adds a controllable clock and a mintable upload URL, so upload sessions can be created and
/// expired without storage. The fake issuer returns a URL on the reserved example.invalid host —
/// exactly the kind of value the client's stub treats as "do not actually PUT".
/// </summary>
/// <summary>A clock the tests can move, without a package dependency for four lines of code.</summary>
public sealed class TestClock : TimeProvider
{
    private DateTimeOffset now = new(2026, 8, 26, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => now;

    public void Advance(TimeSpan by) => now = now.Add(by);
}

public sealed class UploadReadyFactory : WebApplicationFactory<global::Program>
{
    public TestClock Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.WithFixtureWorkflow().WithAuthenticationConfigured().WithTestAuthentication();
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<IUploadUrlIssuer>(new FakeUploadUrlIssuer());
        });
    }

    private sealed class FakeUploadUrlIssuer : IUploadUrlIssuer
    {
        public Task<Uri> MintWriteOnlyUrlAsync(
            string blobName, DateTimeOffset expiresOn, CancellationToken cancellationToken) =>
            Task.FromResult(new Uri($"https://upload.example.invalid/{blobName}"));
    }
}
