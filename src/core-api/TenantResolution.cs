using System.Security.Claims;
using System.Text.RegularExpressions;

namespace LaPluma.CoreApi;

/// <summary>
/// Server-side tenant identity resolution.
/// Strictly extracts tenant identity from validated claims (e.g. tid, tenant_id, sub).
/// Explicitly rejects client tenant overrides from request parameters or headers.
/// </summary>
public static partial class TenantResolution
{
    public const string TenantIdClaimType = "tenant_id";
    public const string AltTenantIdClaimType = "tid";

    [GeneratedRegex("^[a-z0-9_-]{2,64}$", RegexOptions.Compiled)]
    public static partial Regex TenantIdRegex();

    /// <summary>
    /// Resolves the authenticated caller's tenant ID from principal claims.
    /// Never inspects or trusts untrusted query string or header arguments.
    /// </summary>
    public static string? ResolveTenantId(ClaimsPrincipal? principal)
    {
        if (principal == null || !principal.Identity?.IsAuthenticated == true)
        {
            return null;
        }

        // 1. Direct tenant_id claim
        var tenantClaim = principal.FindFirst(TenantIdClaimType)?.Value;
        if (!string.IsNullOrWhiteSpace(tenantClaim) && TenantIdRegex().IsMatch(tenantClaim))
        {
            return tenantClaim;
        }

        // 2. Azure/Entra tid claim
        var tidClaim = principal.FindFirst(AltTenantIdClaimType)?.Value;
        if (!string.IsNullOrWhiteSpace(tidClaim) && TenantIdRegex().IsMatch(tidClaim))
        {
            return tidClaim;
        }

        // 3. Fallback to NameIdentifier if formatted as valid tenant ID
        var subClaim = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrWhiteSpace(subClaim) && TenantIdRegex().IsMatch(subClaim))
        {
            return subClaim;
        }

        return null;
    }
}
