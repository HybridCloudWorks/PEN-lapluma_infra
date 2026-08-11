namespace LaPluma.CoreApi;

/// <summary>
/// Where the catalog comes from.
///
/// Two implementations: the in-memory fixture that has always been here, and the SQL-backed source
/// that reads the authoritative store. The seam exists so the contract tests can keep running
/// offline against the fixture without that fixture being what production serves — which is what it
/// was, registered as a singleton with nothing else behind it.
///
/// Asynchronous throughout, because one of the two implementations talks to a database over a
/// private endpoint. A synchronous interface would have forced that call to block a request thread.
/// </summary>
public interface ICatalogSource
{
    Task<IReadOnlyList<CatalogCategoryNode>> GetHierarchyAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<FormPackage>> ListPackagesAsync(
        string? categoryCode,
        string? subcategoryCode,
        FormActivationState? activationState,
        CancellationToken cancellationToken);

    Task<FormPackage?> GetPackageAsync(string packageCode, CancellationToken cancellationToken);

    Task<ExtractedFormSchema?> GetSchemaAsync(
        string authority,
        string formId,
        DateOnly editionDate,
        string schemaVersion,
        CancellationToken cancellationToken);
}
