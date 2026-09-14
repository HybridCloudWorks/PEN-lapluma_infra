using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace LaPluma.CoreApi;

/// <summary>
/// PostgreSQL 16 implementation of ICatalogSource (INF-10).
/// Replaces Azure SQL with Cloud SQL PostgreSQL.
/// Executes parameterized queries with strict isolation and connection pooling.
/// </summary>
public sealed class PostgresCatalogSource(NpgsqlDataSource dataSource) : ICatalogSource
{
    public async Task<IReadOnlyList<CatalogCategoryNode>> GetHierarchyAsync(
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT  c.code, c.title, c.sort_order,
                    s.code, s.title, s.sort_order
            FROM    catalog.category    AS c
            JOIN    catalog.subcategory AS s ON s.category_code = c.code
            ORDER BY c.sort_order, s.sort_order;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var subcategoriesByCategory = new Dictionary<string, List<CatalogSubcategory>>(StringComparer.Ordinal);
        var categories = new List<CatalogCategory>();

        while (await reader.ReadAsync(cancellationToken))
        {
            var categoryCode = reader.GetString(0);
            var categoryTitle = reader.GetString(1);
            var categorySort = reader.GetInt32(2);

            var category = new CatalogCategory(categoryCode, categoryTitle, categorySort);
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
        const string sql = """
            SELECT  p.package_code, p.title, p.agency, p.agency_category_label,
                    p.fee_usd_cents, p.fee_citation_url, p.source_url, p.last_verified,
                    c.code, c.title, c.sort_order,
                    s.code, s.title, s.sort_order,
                    f.form_number, f.title, f.edition_date, f.encoding, f.page_count,
                    f.artifact_kind, f.fill_capability, f.activation_state,
                    f.source_page_url, f.artifact_url, f.official_domain, f.sha256, f.source_last_verified
            FROM    catalog.package     AS p
            JOIN    catalog.category    AS c ON c.code = p.category_code
            JOIN    catalog.subcategory AS s ON s.code = p.subcategory_code
            LEFT JOIN catalog.form      AS f ON f.package_code = p.package_code
            WHERE   ($1::text IS NULL OR p.category_code    = $1)
              AND   ($2::text IS NULL OR p.subcategory_code = $2)
              AND   ($3::text IS NULL OR p.package_code     = $3)
            ORDER BY c.sort_order, s.sort_order, p.package_code, f.sort_order;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)categoryCode ?? DBNull.Value);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)subcategoryCode ?? DBNull.Value);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)packageCode ?? DBNull.Value);

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
                    reader.GetFieldValue<DateTimeOffset>(7)));
            }

            if (!reader.IsDBNull(13))
            {
                forms[code].Add(new CatalogForm(
                    reader.GetString(13),
                    reader.GetString(14),
                    DateOnly.FromDateTime(reader.GetDateTime(15)),
                    ParseEnum<FormEncoding>(reader.GetString(16)),
                    reader.GetInt32(17),
                    ParseEnum<FormArtifactKind>(reader.GetString(18)),
                    ParseEnum<FormFillCapability>(reader.GetString(19)),
                    ParseEnum<FormActivationState>(reader.GetString(20)),
                    reader.IsDBNull(21)
                        ? null
                        : new FormSourceMetadata(
                            reader.GetString(1),
                            new Uri(reader.GetString(21)),
                            reader.IsDBNull(22) ? null : new Uri(reader.GetString(22)),
                            reader.IsDBNull(23) ? string.Empty : reader.GetString(23),
                            reader.IsDBNull(24) ? null : reader.GetString(24),
                            reader.IsDBNull(25) ? default : reader.GetFieldValue<DateTimeOffset>(25))));
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
            SELECT  fields_json
            FROM    catalog.extracted_schema
            WHERE   authority = $1
              AND   form_id = $2
              AND   edition_date = $3
              AND   schema_version = $4;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, authority);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, formId);
        command.Parameters.AddWithValue(NpgsqlDbType.Date, editionDate.ToDateTime(TimeOnly.MinValue));
        command.Parameters.AddWithValue(NpgsqlDbType.Text, schemaVersion);

        var fieldsJson = await command.ExecuteScalarAsync(cancellationToken) as string;
        if (fieldsJson is null)
        {
            return null;
        }

        var fields = JsonSerializer.Deserialize<List<ExtractedFieldManifest>>(fieldsJson)
            ?? throw new InvalidOperationException("Approved field map could not be read.");
        return new ExtractedFormSchema(
            new FormEditionId(authority, formId, editionDate), schemaVersion, fields);
    }

    private static T ParseEnum<T>(string wireValue) where T : struct, Enum =>
        CatalogWireNames.Parse<T>(wireValue)
        ?? throw new InvalidOperationException($"Catalog value '{wireValue}' is not in the contract.");
}
