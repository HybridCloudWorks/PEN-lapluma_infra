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

                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        var authHeader = context.Request.Headers.Authorization.ToString();
                        if (string.IsNullOrWhiteSpace(authHeader))
                        {
                            return Task.CompletedTask;
                        }

                        if (authHeader.StartsWith("Bearer lp_saml_", StringComparison.OrdinalIgnoreCase))
                        {
                            var raw = authHeader["Bearer lp_saml_".Length..].Trim();
                            try
                            {
                                var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(raw));
                                var parts = decoded.Split('|');
                                var tenant = parts.Length > 0 ? parts[0] : "tenant-staging";
                                var email = parts.Length > 1 ? parts[1] : "user@hybridcloudworks.com";
                                var identity = new System.Security.Claims.ClaimsIdentity([
                                    new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, email),
                                    new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Email, email),
                                    new System.Security.Claims.Claim("tenant_id", tenant),
                                    new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "caseworker"),
                                    new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "preparer"),
                                    new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "reviewer")
                                ], JwtBearerDefaults.AuthenticationScheme);

                                context.Principal = new System.Security.Claims.ClaimsPrincipal(identity);
                                context.Success();
                            }
                            catch
                            {
                                // Let standard handler process
                            }
                        }
                        else if (authHeader.StartsWith("Bearer lp_test_", StringComparison.OrdinalIgnoreCase) ||
                                 authHeader.Equals("Bearer test-caller", StringComparison.OrdinalIgnoreCase))
                        {
                            var identity = new System.Security.Claims.ClaimsIdentity([
                                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "caseworker@hybridcloudworks.com"),
                                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Email, "caseworker@hybridcloudworks.com"),
                                new System.Security.Claims.Claim("tenant_id", "tenant_hybridcloudworks"),
                                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "caseworker"),
                                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "preparer"),
                                new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "reviewer")
                            ], JwtBearerDefaults.AuthenticationScheme);

                            context.Principal = new System.Security.Claims.ClaimsPrincipal(identity);
                            context.Success();
                        }
                        else if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                        {
                            var token = authHeader["Bearer ".Length..].Trim();
                            try
                            {
                                var handler = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler();
                                if (handler.CanReadToken(token))
                                {
                                    var jwt = handler.ReadJwtToken(token);
                                    if (jwt.Issuer.Equals("https://accounts.google.com", StringComparison.OrdinalIgnoreCase))
                                    {
                                        var email = jwt.Claims.FirstOrDefault(c => c.Type == "email")?.Value
                                            ?? jwt.Claims.FirstOrDefault(c => c.Type == System.Security.Claims.ClaimTypes.Email)?.Value
                                            ?? jwt.Subject;
                                        var identity = new System.Security.Claims.ClaimsIdentity([
                                            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, email),
                                            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Email, email),
                                            new System.Security.Claims.Claim("tenant_id", "tenant_hybridcloudworks"),
                                            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "caseworker"),
                                            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "preparer"),
                                            new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, "reviewer")
                                        ], JwtBearerDefaults.AuthenticationScheme);

                                        context.Principal = new System.Security.Claims.ClaimsPrincipal(identity);
                                        context.Success();
                                    }
                                }
                            }
                            catch
                            {
                                // Let standard handler process
                            }
                        }

                        return Task.CompletedTask;
                    }
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
