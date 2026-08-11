using System.Data;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace LaPluma.CoreApi;

/// <summary>
/// The authoritative catalog, read from Azure SQL.
///
/// **This code has never executed a query.** No environment has been provisioned —
/// `enableProvisioning` is restricted to `false` — so there is no database to run it against, and
/// none of it is covered by a test that touches SQL. It compiles, it is reviewed, and that is the
/// whole of the assurance behind it. `TODO.md` carries the integration test that will be the first
/// real exercise of these queries.
///
/// Authentication is `Active Directory Default`, which resolves the workload's managed identity via
/// `AZURE_CLIENT_ID`. There is no connection string holding a credential and no password anywhere:
/// the server refuses SQL authentication outright — `azureADOnlyAuthentication` is set on it.
/// </summary>
public sealed class SqlCatalogSource(SqlCatalogOptions options) : ICatalogSource
{
    private async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    public async Task<IReadOnlyList<CatalogCategoryNode>> GetHierarchyAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT  c.Code, c.Title, c.SortOrder,
                    s.Code, s.Title, s.SortOrder
            FROM    catalog.Category  AS c
            JOIN    catalog.Subcategory AS s ON s.CategoryCode = c.Code
            ORDER BY c.SortOrder, s.SortOrder;
            """;

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var subcategoriesByCategory = new Dictionary<string, List<CatalogSubcategory>>(StringComparer.Ordinal);
        var categories = new List<CatalogCategory>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var category = new CatalogCategory(reader.GetString(0), reader.GetString(1), reader.GetInt32(2));
            if (!subcategoriesByCategory.TryGetValue(category.Code, out var subcategories))
            {
                subcategories = [];
                subcategoriesByCategory[category.Code] = subcategories;
                categories.Add(category);
            }

            subcategories.Add(new CatalogSubcategory(
                reader.GetString(3), category.Code, reader.GetString(4), reader.GetInt32(5)));
        }

        return categories
            .Select(category => new CatalogCategoryNode(category, subcategoriesByCategory[category.Code]))
            .ToArray();
    }

    public async Task<IReadOnlyList<FormPackage>> ListPackagesAsync(
        string? categoryCode,
        string? subcategoryCode,
        FormActivationState? activationState,
        CancellationToken cancellationToken)
    {
        // Filtering by activation state happens in memory, deliberately. A package's state is
        // derived from its weakest form and is not stored — storing it would let the stored value
        // and the forms disagree — so it cannot be a WHERE clause without duplicating the
        // derivation in SQL, where it would drift from the one in FormPackage.
        var packages = await LoadPackagesAsync(categoryCode, subcategoryCode, null, cancellationToken);
        return activationState is null
            ? packages
            : packages.Where(package => package.ActivationState == activationState).ToArray();
    }

    public async Task<FormPackage?> GetPackageAsync(string packageCode, CancellationToken cancellationToken)
    {
        var packages = await LoadPackagesAsync(null, null, packageCode, cancellationToken);
        return packages.Count == 0 ? null : packages[0];
    }

    private async Task<IReadOnlyList<FormPackage>> LoadPackagesAsync(
        string? categoryCode,
        string? subcategoryCode,
        string? packageCode,
        CancellationToken cancellationToken)
    {
        // Every filter is a parameter. None of these values reaches the server as text, which
        // matters more than usual here: they arrive from a route and a query string.
        const string sql = """
            SELECT  p.PackageCode, p.Title, p.Agency, p.AgencyCategoryLabel,
                    p.FeeUsdCents, p.FeeCitationUrl, p.SourceUrl, p.LastVerified,
                    c.Code, c.Title, c.SortOrder,
                    s.Code, s.Title, s.SortOrder,
                    f.FormNumber, f.Title, f.EditionDate, f.Encoding, f.PageCount,
                    f.ArtifactKind, f.FillCapability, f.ActivationState,
                    f.SourcePageUrl, f.ArtifactUrl, f.OfficialDomain, f.Sha256, f.SourceLastVerified
            FROM    catalog.Package     AS p
            JOIN    catalog.Category    AS c ON c.Code = p.CategoryCode
            JOIN    catalog.Subcategory AS s ON s.Code = p.SubcategoryCode
            LEFT JOIN catalog.Form      AS f ON f.PackageCode = p.PackageCode
            WHERE   (@categoryCode    IS NULL OR p.CategoryCode    = @categoryCode)
              AND   (@subcategoryCode IS NULL OR p.SubcategoryCode = @subcategoryCode)
              AND   (@packageCode     IS NULL OR p.PackageCode     = @packageCode)
            ORDER BY c.SortOrder, s.SortOrder, p.PackageCode, f.SortOrder;
            """;

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@categoryCode", SqlDbType.NVarChar, 64).Value =
            (object?)categoryCode ?? DBNull.Value;
        command.Parameters.Add("@subcategoryCode", SqlDbType.NVarChar, 64).Value =
            (object?)subcategoryCode ?? DBNull.Value;
        command.Parameters.Add("@packageCode", SqlDbType.NVarChar, 64).Value =
            (object?)packageCode ?? DBNull.Value;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var forms = new Dictionary<string, List<CatalogForm>>(StringComparer.Ordinal);
        var ordered = new List<FormPackage>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        while (await reader.ReadAsync(cancellationToken))
        {
            var code = reader.GetString(0);
            if (!seen.ContainsKey(code))
            {
                seen[code] = ordered.Count;
                forms[code] = [];
                ordered.Add(new FormPackage(
                    code,
                    reader.GetString(1),
                    new CatalogCategory(reader.GetString(8), reader.GetString(9), reader.GetInt32(10)),
                    new CatalogSubcategory(
                        reader.GetString(11), reader.GetString(8), reader.GetString(12), reader.GetInt32(13)),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    forms[code],
                    reader.IsDBNull(4) ? null : reader.GetInt32(4),
                    reader.IsDBNull(5) ? null : new Uri(reader.GetString(5)),
                    new Uri(reader.GetString(6)),
                    reader.GetDateTimeOffset(7)));
            }

            // LEFT JOIN: a package with no forms yields one row with null form columns. That is a
            // real state — FormPackage derives UNAVAILABLE from an empty list — not a missing row.
            if (!reader.IsDBNull(14))
            {
                forms[code].Add(new CatalogForm(
                    reader.GetString(14),
                    reader.GetString(15),
                    DateOnly.FromDateTime(reader.GetDateTime(16)),
                    ParseEnum<FormEncoding>(reader.GetString(17)),
                    reader.GetInt32(18),
                    ParseEnum<FormArtifactKind>(reader.GetString(19)),
                    ParseEnum<FormFillCapability>(reader.GetString(20)),
                    ParseEnum<FormActivationState>(reader.GetString(21)),
                    // SourcePageUrl is required by the record, so a row without one has no source
                    // metadata at all rather than a half-populated block.
                    reader.IsDBNull(22)
                        ? null
                        : new FormSourceMetadata(
                            reader.GetString(1),
                            new Uri(reader.GetString(22)),
                            reader.IsDBNull(23) ? null : new Uri(reader.GetString(23)),
                            reader.IsDBNull(24) ? string.Empty : reader.GetString(24),
                            reader.IsDBNull(25) ? null : reader.GetString(25),
                            reader.IsDBNull(26) ? default : reader.GetDateTimeOffset(26))));
            }
        }

        return ordered;
    }

    public async Task<ExtractedFormSchema?> GetSchemaAsync(
        string authority,
        string formId,
        DateOnly editionDate,
        string schemaVersion,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT  FieldsJson
            FROM    catalog.ExtractedSchema
            WHERE   Authority = @authority
              AND   FormId = @formId
              AND   EditionDate = @editionDate
              AND   SchemaVersion = @schemaVersion;
            """;

        await using var connection = await OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@authority", SqlDbType.NVarChar, 256).Value = authority;
        command.Parameters.Add("@formId", SqlDbType.NVarChar, 64).Value = formId;
        command.Parameters.Add("@editionDate", SqlDbType.Date).Value = editionDate.ToDateTime(TimeOnly.MinValue);
        command.Parameters.Add("@schemaVersion", SqlDbType.NVarChar, 32).Value = schemaVersion;

        var fieldsJson = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (fieldsJson is null)
        {
            // Absent, not unimplemented. A schema is served only after a two-person field-map
            // approval, so the fail-closed 404 the contract specifies is the row simply not being
            // there.
            return null;
        }

        var fields = JsonSerializer.Deserialize<List<ExtractedFieldManifest>>(fieldsJson)
            ?? throw new InvalidOperationException("Approved field map could not be read.");
        return new ExtractedFormSchema(
            new FormEditionId(authority, formId, editionDate), schemaVersion, fields);
    }

    private static T ParseEnum<T>(string wireValue) where T : struct, Enum =>
        // The wire names are the contract's, and the column carries a CHECK constraint holding the
        // same set. A value outside it means the database and the contract have diverged, which is
        // worth an exception rather than a silent default.
        CatalogWireNames.Parse<T>(wireValue)
        ?? throw new InvalidOperationException($"Catalog value '{wireValue}' is not in the contract.");
}

/// <summary>Where the database is, and nothing about who is allowed to read it.</summary>
public sealed class SqlCatalogOptions
{
    public required string ConnectionString { get; init; }
}
