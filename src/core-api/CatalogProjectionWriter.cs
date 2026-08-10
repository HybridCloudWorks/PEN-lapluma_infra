using Azure.Core;
using Azure.Identity;
using Microsoft.Azure.Cosmos;

namespace LaPluma.CoreApi;

/// <summary>
/// Writes rebuildable derived views of the catalog into Cosmos.
///
/// **Rebuildable is the whole contract.** Nothing here is authoritative: every document this writes
/// can be reconstructed from SQL, which is why losing the container is an inconvenience rather than
/// a data loss, and why the erasure sweep can delete from it without a two-phase dance. If a value
/// ever appears here that SQL cannot regenerate, this class has become a second source of truth and
/// the design has drifted.
///
/// **This code has never written a document.** No environment has been provisioned, so it compiles
/// and is reviewed and that is all. `TODO.md` carries the integration test.
/// </summary>
public sealed class CatalogProjectionWriter(Container container, TimeProvider clock)
{
    /// <summary>Partition key path is /tenantId then /caseId; catalog projections are tenant-wide.</summary>
    public const string CatalogTenant = "catalog";

    public async Task WritePackageProjectionsAsync(
        IReadOnlyList<FormPackage> packages, CancellationToken cancellationToken)
    {
        foreach (var package in packages)
        {
            var document = new CatalogPackageProjection(
                Id: package.PackageCode,
                TenantId: CatalogTenant,
                CaseId: package.PackageCode,
                PackageCode: package.PackageCode,
                Title: package.Title,
                CategoryCode: package.Category.Code,
                SubcategoryCode: package.Subcategory.Code,
                // Derived, not copied: the projection carries the same weakest-form rule the API
                // serves, so a reader of the projection and a reader of the API cannot disagree.
                ActivationState: package.ActivationState,
                FormNumbers: package.Forms.Select(form => form.FormNumber).ToArray(),
                ProjectedAt: clock.GetUtcNow());

            await container.UpsertItemAsync(
                document,
                new PartitionKeyBuilder().Add(document.TenantId).Add(document.CaseId).Build(),
                cancellationToken: cancellationToken);
        }
    }

    /// <summary>
    /// Managed identity only. `disableLocalAuth` is set on the account, so a key would be refused;
    /// this exists so nobody reaches for one when the first connection fails.
    /// </summary>
    public static CosmosClient CreateClient(string accountEndpoint, TokenCredential credential) =>
        new(accountEndpoint, credential, new CosmosClientOptions
        {
            // The account is reachable only through its private endpoint, so gateway mode is what
            // works: direct mode opens additional ports that the endpoint does not publish.
            ConnectionMode = ConnectionMode.Gateway,
        });

    public static TokenCredential DefaultCredential(string? managedIdentityClientId) =>
        new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ManagedIdentityClientId = managedIdentityClientId,
        });
}

/// <summary>A derived view of a package. Every field is reconstructable from SQL.</summary>
public sealed record CatalogPackageProjection(
    string Id,
    string TenantId,
    string CaseId,
    string PackageCode,
    string Title,
    string CategoryCode,
    string SubcategoryCode,
    FormActivationState ActivationState,
    IReadOnlyList<string> FormNumbers,
    DateTimeOffset ProjectedAt);
