using System.Security.Claims;

namespace LaPluma.CoreApi;

/// <summary>
/// Service contract for resolving tenant-effective Document Blueprints and Collections.
/// Enforces server-side tenant isolation:
/// - Client tenant selection is NEVER accepted as authorization.
/// - Shared Blueprints are immutable and reusable across tenants.
/// - Private Blueprints are strictly isolated to the owning or explicitly granted tenant.
/// </summary>
public interface ILibraryAccessService
{
    /// <summary>
    /// Returns all published Collections actively assigned to the specified tenant.
    /// </summary>
    Task<IReadOnlyList<DocumentCollection>> GetAssignedCollectionsAsync(
        string tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific Collection by namespace and collection ID, ensuring the caller's tenant has an active assignment.
    /// Returns null if the collection does not exist or the caller is not authorized.
    /// </summary>
    Task<DocumentCollection?> GetCollectionAsync(
        string tenantId,
        string collectionNamespace,
        string collectionId,
        int? revision = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns all Document Blueprints accessible to the specified tenant.
    /// Includes:
    /// - Pinned members of actively assigned collections
    /// - Shared official published blueprints
    /// - Private blueprints owned by the tenant
    /// - Private blueprints explicitly granted to the tenant
    /// Excludes:
    /// - Private blueprints owned by other tenants without an explicit grant.
    /// </summary>
    Task<IReadOnlyList<DocumentBlueprint>> ListEffectiveBlueprintsAsync(
        string tenantId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets a specific Document Blueprint if the caller's tenant is authorized to access it.
    /// Returns null (never leaks existence) if unauthorized or not found.
    /// </summary>
    Task<DocumentBlueprint?> GetBlueprintAsync(
        string tenantId,
        string blueprintNamespace,
        string blueprintId,
        int? revision = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asserts whether the given tenant has effective access to the specified blueprint.
    /// </summary>
    Task<bool> CanAccessBlueprintAsync(
        string tenantId,
        string blueprintNamespace,
        string blueprintId,
        CancellationToken cancellationToken = default);
}
