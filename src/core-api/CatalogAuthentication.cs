using LaPluma.CoreApi.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace LaPluma.CoreApi;

/// <summary>
/// Authentication and authorization for the catalog surface.
///
/// The only credential this service accepts is a LaPluma session token: an RS256 JWT it minted
/// itself after a verified SAML sign-in (see <see cref="SessionTokenIssuer"/>). It is validated by
/// the standard JWT bearer handler — issuer, audience, lifetime, and signature against this
/// service's own public key — with no metadata fetch and no alternative path. API Gateway checks
/// the same token at the edge; a service whose only protection is an upstream gateway fails open
/// the moment anything reaches it directly, so this is the second lock, not the first.
/// </summary>
public static class CatalogAuthentication
{
    /// <summary>Applied to the catalog group. Health and readiness stay anonymous.</summary>
    public const string PolicyName = "catalog-reader";

    public const string AudienceSetting = "Authentication:Audience";
    public const string IssuerSetting = "Authentication:Issuer";

    public static IServiceCollection AddCatalogAuthentication(
        this IServiceCollection services, IConfiguration configuration)
    {
        var audience = configuration[AudienceSetting];
        var issuer = configuration[IssuerSetting];
        var configured = !string.IsNullOrWhiteSpace(audience) && !string.IsNullOrWhiteSpace(issuer);

        // Loaded here rather than on first use so an unusable key stops the host at startup.
        var keys = SessionTokenKeys.FromConfiguration(configuration);
        services.AddSingleton(keys);
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<SessionTokenIssuer>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // No Authority and no metadata address: the handler validates against the keys
                // supplied below and never reaches out for configuration.
                options.MapInboundClaims = true;
                options.TokenValidationParameters =
                    SessionTokenValidation.CreateParameters(issuer, audience, keys.ValidationKeys);
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = SessionTokenValidation.SelectBearerToken,
                };
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(PolicyName, policy =>
            {
                policy.RequireAuthenticatedUser();

                if (!configured)
                {
                    // Fail closed, and loudly. With no audience and issuer there is nothing to
                    // validate a token against, so the safe reading of an unconfigured deployment
                    // is "deny everything", not "accept anything". Without this the service would
                    // still reject unsigned tokens, but any scheme registered later — a test
                    // handler, a developer's convenience shim — would sail straight through.
                    policy.RequireAssertion(_ => false);
                }
            });
        });

        return services;
    }
}
