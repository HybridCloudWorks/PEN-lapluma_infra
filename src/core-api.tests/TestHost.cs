using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LaPluma.CoreApi.Tests;

/// <summary>
/// Authenticates every request, so tests about catalog behaviour stay about catalog behaviour.
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

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "test-caller")], SchemeName);
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
    /// Select the in-memory fixture. Production defaults to SQL, so every test that exercises the
    /// catalog has to opt into the fixture explicitly — which is the point of the default.
    /// </summary>
    public static IWebHostBuilder WithFixtureCatalog(this IWebHostBuilder builder)
    {
        builder.UseSetting(CatalogSourceRegistration.SourceSetting, CatalogSourceRegistration.FixtureSource);
        return builder;
    }

    /// <summary>Supply an audience and issuer, so the catalog policy is configured rather than failing closed.</summary>
    public static IWebHostBuilder WithAuthenticationConfigured(this IWebHostBuilder builder)
    {
        builder.UseSetting(CatalogAuthentication.AudienceSetting, Audience);
        builder.UseSetting(CatalogAuthentication.IssuerSetting, Issuer);
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
        builder.WithFixtureCatalog().WithAuthenticationConfigured().WithTestAuthentication();
}
