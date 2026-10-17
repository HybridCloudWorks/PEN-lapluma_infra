using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace LaPluma.WorkflowApi;

/// <summary>
/// Authentication and authorization for the workflow surface.
///
/// The contract declares an opaque tenant-session bearer. The session is minted by the core API
/// after a verified SAML sign-in: an RS256 JWT carrying the tenant, email, and roles. This service
/// validates it with the standard JWT bearer handler — issuer, audience, lifetime, and signature —
/// against the public keys the core API publishes at <c>/.well-known/jwks.json</c>, fetched from
/// <see cref="JwksUriSetting"/> and cached. There is no other way in: no fixed test token, no
/// unsigned fallback. API Gateway checks the same token at the edge; this is the second lock.
/// </summary>
public static class WorkflowAuthentication
{
    /// <summary>Applied to the /v1 group. Health and readiness stay anonymous.</summary>
    public const string PolicyName = "workflow-caller";

    public const string AudienceSetting = "Authentication:Audience";
    public const string IssuerSetting = "Authentication:Issuer";
    public const string JwksUriSetting = "Authentication:JwksUri";

    /// <summary>The named HTTP client the signing keys are fetched with.</summary>
    public const string JwksHttpClientName = "LaPluma.SessionTokenKeys";

    public static IServiceCollection AddWorkflowAuthentication(
        this IServiceCollection services, IConfiguration configuration)
    {
        var audience = configuration[AudienceSetting];
        var issuer = configuration[IssuerSetting];
        var jwksUri = ParseJwksUri(configuration[JwksUriSetting]);
        var configured = !string.IsNullOrWhiteSpace(audience) && !string.IsNullOrWhiteSpace(issuer) && jwksUri is not null;

        services.AddHttpClient(JwksHttpClientName, client => client.Timeout = TimeSpan.FromSeconds(10));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IHttpClientFactory>((options, httpClientFactory) =>
            {
                options.MapInboundClaims = true;
                options.TokenValidationParameters = SessionTokenValidation.CreateParameters(issuer, audience);
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = SessionTokenValidation.SelectBearerToken,
                };

                // Unconfigured, the handler has no keys at all and every token fails validation.
                if (configured)
                {
                    options.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(
                        jwksUri!.AbsoluteUri,
                        new JwksConfigurationRetriever(issuer!),
                        new HttpDocumentRetriever(httpClientFactory.CreateClient(JwksHttpClientName)) { RequireHttps = true });
                }
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(PolicyName, policy =>
            {
                policy.RequireAuthenticatedUser();

                // Every /v1 operation is tenant data. A token without exactly one well-formed
                // tenant_id claim is refused here (403), so no handler ever runs without a
                // tenant to scope its queries by (INF-26).
                policy.RequireAssertion(context => WorkflowTenant.TryResolve(context.User, out _));

                if (!configured)
                {
                    // Fail closed, and loudly. With no audience, issuer, and key source there is
                    // nothing to validate a token against, so the safe reading of an unconfigured
                    // deployment is "deny everything", not "accept anything". Without this the
                    // service would still reject unsigned tokens, but any scheme registered later —
                    // a test handler, a developer's convenience shim — would sail straight through.
                    policy.RequireAssertion(_ => false);
                }
            });
        });

        return services;
    }

    private static Uri? ParseJwksUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException($"{JwksUriSetting} must be an absolute https URI.");
        }

        return uri;
    }

    /// <summary>
    /// Reads a bare JWK Set rather than an OpenID discovery document: the core API publishes keys,
    /// not a full OpenID provider. The issuer is pinned from configuration, never from the fetch.
    /// </summary>
    private sealed class JwksConfigurationRetriever(string issuer) : IConfigurationRetriever<OpenIdConnectConfiguration>
    {
        public async Task<OpenIdConnectConfiguration> GetConfigurationAsync(
            string address, IDocumentRetriever retriever, CancellationToken cancel)
        {
            var document = await retriever.GetDocumentAsync(address, cancel).ConfigureAwait(false);
            var keySet = new JsonWebKeySet(document);
            var configuration = new OpenIdConnectConfiguration
            {
                Issuer = issuer,
                JwksUri = address,
                JsonWebKeySet = keySet,
            };

            foreach (var key in keySet.GetSigningKeys())
            {
                configuration.SigningKeys.Add(key);
            }

            return configuration;
        }
    }
}

/// <summary>Validation shared by every route that accepts a LaPluma session token.</summary>
public static class SessionTokenValidation
{
    /// <summary>
    /// API Gateway authenticates to Cloud Run with its own Google-signed ID token in
    /// <c>Authorization</c> and moves the caller's header here. That ID token only proves the
    /// gateway may invoke the service — Cloud Run IAM has already checked it — so it is never
    /// treated as a user identity.
    /// </summary>
    public const string ForwardedAuthorizationHeader = "X-Forwarded-Authorization";

    public static TokenValidationParameters CreateParameters(string? issuer, string? audience) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = issuer,
        ValidateAudience = true,
        ValidAudience = audience,
        ValidateLifetime = true,
        RequireExpirationTime = true,
        RequireSignedTokens = true,
        ValidateIssuerSigningKey = true,
        ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
        ClockSkew = TimeSpan.FromSeconds(30),
    };

    /// <summary>
    /// Chooses which header carries the caller's token. It only selects; the JWT bearer handler
    /// still validates whatever is selected in full, so a forwarded header confers nothing a
    /// direct one would not.
    /// </summary>
    public static Task SelectBearerToken(MessageReceivedContext context)
    {
        var forwarded = context.Request.Headers[ForwardedAuthorizationHeader].ToString();
        if (forwarded.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            context.Token = forwarded["Bearer ".Length..].Trim();
        }

        return Task.CompletedTask;
    }
}
