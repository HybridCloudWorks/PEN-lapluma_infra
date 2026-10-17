using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.RegularExpressions;

namespace LaPluma.WorkflowApi;

/// <summary>
/// The caller's tenant, read from the validated session token and from nowhere else.
///
/// The core API mints the token after a verified SAML sign-in and puts the institution in its
/// <c>tenant_id</c> claim. That claim is the only input: no query parameter, header, or request body
/// can name a tenant, and there is no fallback to <c>tid</c> or the subject. A principal without
/// exactly one well-formed <c>tenant_id</c> is refused by the authorization policy before any
/// handler runs, so every store call is made with a tenant the caller actually holds.
/// </summary>
public static partial class WorkflowTenant
{
    public const string ClaimType = "tenant_id";

    public static bool TryResolve(ClaimsPrincipal? principal, [NotNullWhen(true)] out string? tenantId)
    {
        tenantId = null;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        var claims = principal.FindAll(ClaimType).ToArray();
        if (claims.Length != 1 || !TenantIdPattern().IsMatch(claims[0].Value))
        {
            return false;
        }

        tenantId = claims[0].Value;
        return true;
    }

    /// <summary>For handlers behind the policy, which has already required a valid tenant.</summary>
    public static string Resolve(ClaimsPrincipal principal) =>
        TryResolve(principal, out var tenantId)
            ? tenantId
            : throw new InvalidOperationException("The caller has no tenant; the authorization policy should have refused it.");

    [GeneratedRegex("^[a-z0-9_-]{2,64}$")]
    private static partial Regex TenantIdPattern();
}
