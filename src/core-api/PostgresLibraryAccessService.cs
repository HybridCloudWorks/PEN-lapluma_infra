using System.Data;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace LaPluma.CoreApi;

/// <summary>
/// PostgreSQL 16 implementation of ILibraryAccessService (INF-10 / INF-18).
/// Executes queries against PostgreSQL views:
/// - library.v_effective_tenant_collections
/// - library.v_effective_tenant_blueprints
/// Guarantees strict server-side tenant isolation.
/// </summary>
public sealed class PostgresLibraryAccessService(NpgsqlDataSource dataSource) : ILibraryAccessService
{
    public async Task<IReadOnlyList<DocumentCollection>> GetAssignedCollectionsAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT  namespace, collection_id, revision, title, description,
                    workflow_settings, publication_state, is_latest, created_at
            FROM    library.v_effective_tenant_collections
            WHERE   tenant_id = $1
            ORDER BY namespace, collection_id, revision DESC;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, tenantId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var collections = new List<DocumentCollection>();

        while (await reader.ReadAsync(cancellationToken))
        {
            var ns = reader.GetString(0);
            var colId = reader.GetString(1);
            var rev = reader.GetInt32(2);
            var title = reader.GetString(3);
            var desc = reader.IsDBNull(4) ? null : reader.GetString(4);
            var settingsJson = reader.GetString(5);
            var pubState = Enum.Parse<PublicationState>(reader.GetString(6), ignoreCase: true);
            var isLatest = reader.GetBoolean(7);
            var createdAt = reader.GetFieldValue<DateTimeOffset>(8);

            // Members can be populated by reading members for this collection
            var members = await LoadCollectionMembersAsync(connection, ns, colId, rev, cancellationToken);

            collections.Add(new DocumentCollection(
                ns, colId, rev, title, desc,
                JsonDocument.Parse(settingsJson),
                pubState, isLatest, createdAt, members));
        }

        return collections.AsReadOnly();
    }

    public async Task<DocumentCollection?> GetCollectionAsync(
        string tenantId,
        string collectionNamespace,
        string collectionId,
        int? revision = null,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT  namespace, collection_id, revision, title, description,
                    workflow_settings, publication_state, is_latest, created_at
            FROM    library.v_effective_tenant_collections
            WHERE   tenant_id = $1
              AND   namespace = $2
              AND   collection_id = $3
              AND   ($4::int IS NULL OR revision = $4)
            ORDER BY revision DESC
            LIMIT 1;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, tenantId);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, collectionNamespace);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, collectionId);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, (object?)revision ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var ns = reader.GetString(0);
        var colId = reader.GetString(1);
        var rev = reader.GetInt32(2);
        var title = reader.GetString(3);
        var desc = reader.IsDBNull(4) ? null : reader.GetString(4);
        var settingsJson = reader.GetString(5);
        var pubState = Enum.Parse<PublicationState>(reader.GetString(6), ignoreCase: true);
        var isLatest = reader.GetBoolean(7);
        var createdAt = reader.GetFieldValue<DateTimeOffset>(8);

        await reader.CloseAsync();

        var members = await LoadCollectionMembersAsync(connection, ns, colId, rev, cancellationToken);

        return new DocumentCollection(
            ns, colId, rev, title, desc,
            JsonDocument.Parse(settingsJson),
            pubState, isLatest, createdAt, members);
    }

    public async Task<IReadOnlyList<DocumentBlueprint>> ListEffectiveBlueprintsAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT  namespace, blueprint_id, revision, title, issuer,
                    official_edition_date, preparation_mode, artifact_type,
                    source_url, source_sha256, fields_schema, validation_rules,
                    evidence_requirements, publication_state, is_latest, created_at
            FROM    library.v_effective_tenant_blueprints
            WHERE   tenant_id = $1
            ORDER BY namespace, blueprint_id, revision DESC;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, tenantId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var blueprints = new List<DocumentBlueprint>();

        while (await reader.ReadAsync(cancellationToken))
        {
            blueprints.Add(ReadBlueprintRow(reader));
        }

        return blueprints.AsReadOnly();
    }

    public async Task<DocumentBlueprint?> GetBlueprintAsync(
        string tenantId,
        string blueprintNamespace,
        string blueprintId,
        int? revision = null,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT  namespace, blueprint_id, revision, title, issuer,
                    official_edition_date, preparation_mode, artifact_type,
                    source_url, source_sha256, fields_schema, validation_rules,
                    evidence_requirements, publication_state, is_latest, created_at
            FROM    library.v_effective_tenant_blueprints
            WHERE   tenant_id = $1
              AND   namespace = $2
              AND   blueprint_id = $3
              AND   ($4::int IS NULL OR revision = $4)
            ORDER BY revision DESC
            LIMIT 1;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, tenantId);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, blueprintNamespace);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, blueprintId);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, (object?)revision ?? DBNull.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return ReadBlueprintRow(reader);
    }

    public async Task<bool> CanAccessBlueprintAsync(
        string tenantId,
        string blueprintNamespace,
        string blueprintId,
        CancellationToken cancellationToken = default)
    {
        const string sql = """
            SELECT  1
            FROM    library.v_effective_tenant_blueprints
            WHERE   tenant_id = $1
              AND   namespace = $2
              AND   blueprint_id = $3
            LIMIT 1;
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, tenantId);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, blueprintNamespace);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, blueprintId);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is not null;
    }

    private static async Task<IReadOnlyList<CollectionMember>> LoadCollectionMembersAsync(
        NpgsqlConnection connection,
        string colNamespace,
        string colId,
        int colRev,
        CancellationToken ct)
    {
        const string sql = """
            SELECT  blueprint_namespace, blueprint_id, blueprint_revision, sort_order, is_required
            FROM    library.collection_blueprint_member
            WHERE   collection_namespace = $1
              AND   collection_id = $2
              AND   collection_revision = $3
            ORDER BY sort_order;
            """;

        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, colNamespace);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, colId);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, colRev);

        await using var reader = await command.ExecuteReaderAsync(ct);
        var members = new List<CollectionMember>();
        while (await reader.ReadAsync(ct))
        {
            members.Add(new CollectionMember(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetBoolean(4)));
        }

        return members.AsReadOnly();
    }

    private static DocumentBlueprint ReadBlueprintRow(NpgsqlDataReader reader)
    {
        return new DocumentBlueprint(
            Namespace: reader.GetString(0),
            BlueprintId: reader.GetString(1),
            Revision: reader.GetInt32(2),
            Title: reader.GetString(3),
            Issuer: reader.GetString(4),
            OfficialEditionDate: reader.IsDBNull(5) ? null : DateOnly.FromDateTime(reader.GetDateTime(5)),
            PreparationMode: Enum.Parse<PreparationMode>(reader.GetString(6), ignoreCase: true),
            ArtifactType: Enum.Parse<BlueprintArtifactType>(reader.GetString(7), ignoreCase: true),
            SourceUrl: reader.IsDBNull(8) ? null : new Uri(reader.GetString(8)),
            SourceSha256: reader.IsDBNull(9) ? null : reader.GetString(9),
            FieldsSchema: JsonDocument.Parse(reader.GetString(10)),
            ValidationRules: JsonDocument.Parse(reader.GetString(11)),
            EvidenceRequirements: JsonDocument.Parse(reader.GetString(12)),
            PublicationState: Enum.Parse<PublicationState>(reader.GetString(13), ignoreCase: true),
            ReviewedBy: null,
            ReviewedAt: null,
            PublishedAt: null,
            IsLatest: reader.GetBoolean(14),
            CreatedAt: reader.GetFieldValue<DateTimeOffset>(15));
    }
}
