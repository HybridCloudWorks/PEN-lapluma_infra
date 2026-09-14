using System.Data;
using Npgsql;
using NpgsqlTypes;

namespace LaPluma.CoreApi;

/// <summary>
/// PostgreSQL 16 implementation of IBlueprintPublicationService (INF-04).
/// Executes publication, review, drift detection, and rollback workflows with database-enforced invariants.
/// </summary>
public sealed class PostgresBlueprintPublicationService(NpgsqlDataSource dataSource) : IBlueprintPublicationService
{
    public async Task ReviewBlueprintAsync(
        string ns,
        string blueprintId,
        int revision,
        BlueprintReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(request.ReviewerId.Trim(), request.AuthorId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Independent review invariant violated: reviewer cannot be the author (self-approval prohibited).");
        }

        const string sql = "SELECT library.fn_review_blueprint($1, $2, $3, $4, $5, $6);";

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, ns);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, blueprintId);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, revision);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, request.ReviewerId.Trim());
        command.Parameters.AddWithValue(NpgsqlDbType.Text, request.AuthorId.Trim());
        command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)request.Notes ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task PublishBlueprintAsync(
        string ns,
        string blueprintId,
        int revision,
        BlueprintPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.AuthorId != null && string.Equals(request.ReviewerId.Trim(), request.AuthorId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Self-approval invariant violated: reviewer cannot be the author.");
        }

        const string sql = "SELECT library.fn_publish_blueprint($1, $2, $3, $4, $5, $6);";

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, ns);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, blueprintId);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, revision);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, request.PublisherId.Trim());
        command.Parameters.AddWithValue(NpgsqlDbType.Char, request.ManifestSha256.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)request.AuthorId?.Trim() ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> CheckAndQuarantineDriftAsync(
        string ns,
        string blueprintId,
        int revision,
        BlueprintDriftCheckRequest request,
        CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT library.fn_quarantine_if_source_drift($1, $2, $3, $4, $5, $6);";

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, ns);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, blueprintId);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, revision);
        command.Parameters.AddWithValue(NpgsqlDbType.Char, request.ObservedSourceSha256.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue(NpgsqlDbType.Text, request.OperatorId.Trim());
        command.Parameters.AddWithValue(NpgsqlDbType.Text, (object?)request.Reason ?? DBNull.Value);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is bool isQuarantined && isQuarantined;
    }

    public async Task RollbackBlueprintAsync(
        string ns,
        string blueprintId,
        BlueprintRollbackRequest request,
        CancellationToken cancellationToken = default)
    {
        const string sql = "SELECT library.fn_rollback_blueprint($1, $2, $3, $4, $5);";

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, ns);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, blueprintId);
        command.Parameters.AddWithValue(NpgsqlDbType.Integer, request.TargetRevision);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, request.OperatorId.Trim());
        command.Parameters.AddWithValue(NpgsqlDbType.Text, request.Reason.Trim());

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<BlueprintPublicationAuditEntry>> GetAuditTrailAsync(
        string ns,
        string blueprintId,
        int? revision = null,
        CancellationToken cancellationToken = default)
    {
        var sql = """
            SELECT  audit_id, namespace, blueprint_id, revision, action,
                    author_id, reviewer_id, operator_id, source_sha256,
                    manifest_sha256, reason, created_at
            FROM    library.blueprint_publication_audit
            WHERE   namespace = $1 AND blueprint_id = $2
            """;

        if (revision.HasValue)
        {
            sql += " AND revision = $3";
        }
        sql += " ORDER BY created_at DESC;";

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, ns);
        command.Parameters.AddWithValue(NpgsqlDbType.Text, blueprintId);
        if (revision.HasValue)
        {
            command.Parameters.AddWithValue(NpgsqlDbType.Integer, revision.Value);
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<BlueprintPublicationAuditEntry>();

        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new BlueprintPublicationAuditEntry(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.IsDBNull(10) ? null : reader.GetString(10),
                reader.GetFieldValue<DateTimeOffset>(11)));
        }

        return entries.AsReadOnly();
    }
}
