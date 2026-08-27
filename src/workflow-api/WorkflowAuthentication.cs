using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace LaPluma.WorkflowApi;

/// <summary>
/// Authentication and authorization for the workflow surface.
///
/// The contract declares an opaque tenant-session bearer; the session service that would mint one
/// does not exist yet (REVIEW R-20), so this service validates Entra JWTs exactly the way core-api
/// does — the repository's second-lock standard behind the API Management edge. The divergence is
/// deliberate and recorded, not silent: the contract validator pins the declared scheme so any edit
/// to it is visible, and swapping this class for the session-token validator is R-20's follow-up.
/// </summary>
public static class WorkflowAuthentication
{
    /// <summary>Applied to the /v1 group. Health and readiness stay anonymous.</summary>
    public const string PolicyName = "workflow-caller";

    public const string AudienceSetting = "Authentication:Audience";
    public const string IssuerSetting = "Authentication:Issuer";

    public static IServiceCollection AddWorkflowAuthentication(
        this IServiceCollection services, IConfiguration configuration)
    {
        var audience = configuration[AudienceSetting];
        var issuer = configuration[IssuerSetting];
        var configured = !string.IsNullOrWhiteSpace(audience) && !string.IsNullOrWhiteSpace(issuer);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                // Authority is left unset when unconfigured so the handler never reaches out for
                // OIDC metadata it has no address for. Nothing can validate, so nothing is trusted.
                options.Authority = configured ? issuer : null;
                options.Audience = audience;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = issuer,
                    ValidAudience = audience,
                    // The default five minutes is generous for a token that never leaves a VNet.
                    ClockSkew = TimeSpan.FromSeconds(30),
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
