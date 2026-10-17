using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Xunit;

namespace LaPluma.WorkflowApi.Tests;

/// <summary>
/// Runs only against a real PostgreSQL 16 server. CI provides one (a service container) and sets
/// <see cref="ConnectionSetting"/>; elsewhere the tests are skipped unless
/// <see cref="RequireSetting"/> is <c>true</c>, in which case a missing server fails them.
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public const string ConnectionSetting = "LAPLUMA_TEST_POSTGRES";
    public const string RequireSetting = "LAPLUMA_REQUIRE_POSTGRES_TESTS";

    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionSetting))
            && !string.Equals(Environment.GetEnvironmentVariable(RequireSetting), "true", StringComparison.OrdinalIgnoreCase))
        {
            Skip = $"Set {ConnectionSetting} to a PostgreSQL 16 connection string (a role that may create databases) to run.";
        }
    }
}

/// <summary>
/// A throwaway database built from the repository's own migrations (001 and 002), with two
/// tenants that each own a folder and a case. Dropped when the test class finishes.
/// </summary>
public sealed class PostgresWorkflowDatabase : IAsyncLifetime
{
    public const string TenantA = "tenant_iso_alpha";
    public const string TenantB = "tenant_iso_beta";
    public const string FolderA = "folder-alpha-1";
    public const string FolderB = "folder-beta-1";
    public const string CaseA = "case-alpha-1";
    public const string CaseB = "case-beta-1";
    public const string KeyHeldByB = "key-held-by-beta";
    public const string KeyedFolderB = "folder-beta-keyed";
    public const string KeyedLabelB = "Beta Keyed Client";

    private const string EmptyJson = "{}";

    private string? adminConnectionString;
    private string? databaseName;

    public NpgsqlDataSource? DataSource { get; private set; }

    public NpgsqlDataSource Data => DataSource ?? throw new InvalidOperationException(
        $"{PostgresFactAttribute.ConnectionSetting} is not set, so there is no database.");

    public async Task InitializeAsync()
    {
        adminConnectionString = Environment.GetEnvironmentVariable(PostgresFactAttribute.ConnectionSetting);
        if (string.IsNullOrWhiteSpace(adminConnectionString))
        {
            return;
        }

        databaseName = $"lapluma_wf_test_{Guid.NewGuid():N}";
        await using (var admin = NpgsqlDataSource.Create(adminConnectionString))
        {
            await using var create = admin.CreateCommand($"CREATE DATABASE \"{databaseName}\";");
            await create.ExecuteNonQueryAsync();
        }

        DataSource = NpgsqlDataSource.Create(
            new NpgsqlConnectionStringBuilder(adminConnectionString) { Database = databaseName }.ConnectionString);

        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        foreach (var migration in new[] { "001_document_library_schema.sql", "002_workflow_persistence_schema.sql" })
        {
            await Execute(await File.ReadAllTextAsync(Path.Combine(root, "infra", "sql", migration)));
        }

        await Execute($"""
            INSERT INTO library.institution_tenant (tenant_id, display_name)
            VALUES ('{TenantA}', 'Isolation Alpha'), ('{TenantB}', 'Isolation Beta');

            INSERT INTO library.document_collection (namespace, collection_id, revision, title, publication_state, is_latest)
            VALUES ('official', 'family_i130', 1, 'Family I-130', 'PUBLISHED', TRUE);

            INSERT INTO workflow.client_folder (id, tenant_id, display_label)
            VALUES ('{FolderA}', '{TenantA}', 'Alpha Client'),
                   ('{FolderB}', '{TenantB}', 'Beta Client');

            INSERT INTO workflow.client_folder (id, tenant_id, display_label, idempotency_key, payload_hash)
            VALUES ('{KeyedFolderB}', '{TenantB}', '{KeyedLabelB}', '{KeyHeldByB}', '{Sha256Hex(KeyedLabelB)}');

            INSERT INTO workflow.case_workspace (
                id, folder_id, tenant_id, collection_namespace, collection_id, pinned_collection_revision,
                state, preparer_user_id, reviewer_user_id)
            VALUES ('{CaseA}', '{FolderA}', '{TenantA}', 'official', 'family_i130', 1, 'APPROVED', 'prep-a', 'rev-a'),
                   ('{CaseB}', '{FolderB}', '{TenantB}', 'official', 'family_i130', 1, 'APPROVED', 'prep-b', 'rev-b');

            INSERT INTO workflow.case_approval (case_id, approved_by_user_id, approval_kind)
            VALUES ('{CaseA}', 'appr-a', 'FINAL_SIGN_OFF'), ('{CaseB}', 'appr-b', 'FINAL_SIGN_OFF');

            INSERT INTO workflow.outbox_event (aggregate_type, aggregate_id, event_type, payload)
            VALUES ('case', '{CaseA}', 'ALPHA_EVENT', '{EmptyJson}'), ('case', '{CaseB}', 'BETA_EVENT', '{EmptyJson}');
            """);
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null)
        {
            await DataSource.DisposeAsync();
        }

        if (adminConnectionString is not null && databaseName is not null)
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = NpgsqlDataSource.Create(adminConnectionString);
            await using var drop = admin.CreateCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE);");
            await drop.ExecuteNonQueryAsync();
        }
    }

    public async Task Execute(string sql)
    {
        await using var command = Data.CreateCommand(sql);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<object?> Scalar(string sql, params object[] parameters)
    {
        await using var command = Data.CreateCommand(sql);
        foreach (var parameter in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }

        return await command.ExecuteScalarAsync();
    }

    public static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>
/// INF-26 against PostgreSQL itself: <see cref="PostgresWorkflowSource"/> returns and changes only
/// the caller's tenant's rows, and creates folders in the caller's tenant.
/// </summary>
public sealed class PostgresTenantIsolationTests(PostgresWorkflowDatabase database)
    : IClassFixture<PostgresWorkflowDatabase>
{
    private const string TenantA = PostgresWorkflowDatabase.TenantA;
    private const string TenantB = PostgresWorkflowDatabase.TenantB;
    private const string CaseA = PostgresWorkflowDatabase.CaseA;
    private const string CaseB = PostgresWorkflowDatabase.CaseB;

    private static readonly CancellationToken None = CancellationToken.None;

    private PostgresWorkflowSource Source() => new(database.Data);

    [PostgresFact]
    public async Task The_directory_lists_only_the_callers_tenant()
    {
        var alpha = (await Source().ListClientsAsync(TenantA, null, null, None)).Items.Select(e => e.Id).ToArray();
        var beta = (await Source().ListClientsAsync(TenantB, null, null, None)).Items.Select(e => e.Id).ToArray();

        Assert.Contains(PostgresWorkflowDatabase.FolderA, alpha);
        Assert.DoesNotContain(PostgresWorkflowDatabase.FolderB, alpha);
        Assert.DoesNotContain(PostgresWorkflowDatabase.KeyedFolderB, alpha);
        Assert.Contains(PostgresWorkflowDatabase.FolderB, beta);
        Assert.DoesNotContain(PostgresWorkflowDatabase.FolderA, beta);

        // A search term that matches both tenants' labels still returns only the caller's.
        var searched = await Source().ListClientsAsync(TenantA, "Client", null, None);
        Assert.All(searched.Items, entry => Assert.StartsWith("folder-alpha", entry.Id, StringComparison.Ordinal));
    }

    [PostgresFact]
    public async Task Another_tenants_case_does_not_exist_for_the_caller()
    {
        Assert.NotNull(await Source().GetCaseWorkspaceAsync(TenantA, CaseA, None));
        Assert.Null(await Source().GetCaseWorkspaceAsync(TenantA, CaseB, None));

        var history = await Source().GetCaseHistoryAsync(TenantA, CaseB, None);
        Assert.Empty(history);
        Assert.Contains(await Source().GetCaseHistoryAsync(TenantA, CaseA, None), e => e.Kind == "ALPHA_EVENT");

        Assert.Equal(DraftPreviewStatus.NotFound, (await Source().CreateDraftPreviewAsync(TenantA, CaseB, None)).Status);
        Assert.Equal(StepUpChallengeStatus.NotFound, (await Source().CreateStepUpChallengeAsync(TenantA, CaseB, "u", None)).Status);
        Assert.Equal(PackageGenerationStatus.NotFound, (await Source().RequestPackageGenerationAsync(TenantA, CaseB, "u", "k-gen", None)).Status);
        Assert.Equal(PackageDownloadStatus.NotFound, (await Source().GetPackageDownloadAsync(TenantA, CaseB, "pkg", "u", None)).Status);
        Assert.DoesNotContain(await Source().GetReviewQueueAsync(TenantA, None), item => item.CaseSummary.Id == CaseB);

        // Positive controls: the same calls on the caller's own case succeed.
        Assert.Equal(DraftPreviewStatus.Success, (await Source().CreateDraftPreviewAsync(TenantA, CaseA, None)).Status);
        Assert.Equal(PackageGenerationStatus.Success, (await Source().RequestPackageGenerationAsync(TenantA, CaseA, "u", "k-gen-a", None)).Status);
    }

    [PostgresFact]
    public async Task Writes_aimed_at_another_tenants_case_change_nothing()
    {
        var review = await Source().RecordReviewDecisionAsync(
            TenantA, CaseB, "rev-x", new ReviewDecisionRequest("CHANGES_REQUESTED", null), "k-review", None);
        var approval = await Source().ApproveCaseAsync(
            TenantA, CaseB, "appr-x", new CaseApprovalRequest(null, "challenge", true), "k-approve", None);
        var commit = await Source().CommitSectionAsync(
            TenantA, CaseB, "identity", 1, new Dictionary<string, string> { ["applicant.name.first"] = "Injected" }, "u", "k-commit", None);
        var workflowEvent = await Source().ProcessWorkflowEventAsync(
            TenantA, new PubSubWorkflowEvent("evt-cross-1", "PACKAGE_COMPILED", CaseB, DateTimeOffset.UtcNow, "corr-1"), "k-event", None);

        Assert.Equal(ReviewDecisionStatus.NotFound, review.Status);
        Assert.Equal(ApproveCaseStatus.NotFound, approval.Status);
        Assert.Equal(CommitSectionStatus.NotFound, commit.Status);
        Assert.Equal(WorkflowEventProcessingStatus.Processed, workflowEvent.Status);

        Assert.Equal("APPROVED", await database.Scalar("SELECT state FROM workflow.case_workspace WHERE id = $1", CaseB));
        Assert.Equal(0L, await database.Scalar("SELECT count(*) FROM workflow.case_field_value WHERE case_id = $1", CaseB));
        Assert.Equal(1L, await database.Scalar("SELECT count(*) FROM workflow.case_approval WHERE case_id = $1", CaseB));
        Assert.Equal(1L, await database.Scalar("SELECT count(*) FROM workflow.outbox_event WHERE aggregate_id = $1", CaseB));
    }

    [PostgresFact]
    public async Task A_new_folder_belongs_to_the_callers_tenant_and_only_that_tenant_sees_it()
    {
        var key = $"k-create-{Guid.NewGuid():N}";
        var created = await Source().CreateClientAsync(TenantA, key, new CreateClientRequest("Created By \"Alpha\""), None);

        Assert.Equal(IdempotencyOutcome.Created, created.Outcome);
        Assert.Equal(TenantA, await database.Scalar("SELECT tenant_id FROM workflow.client_folder WHERE id = $1", created.Entry.Id));
        Assert.Equal(TenantA, await database.Scalar(
            "SELECT payload->>'tenantId' FROM workflow.outbox_event WHERE aggregate_type = 'client_folder' AND aggregate_id = $1", created.Entry.Id));

        Assert.Contains((await Source().ListClientsAsync(TenantA, null, null, None)).Items, e => e.Id == created.Entry.Id);
        Assert.DoesNotContain((await Source().ListClientsAsync(TenantB, null, null, None)).Items, e => e.Id == created.Entry.Id);

        var replay = await Source().CreateClientAsync(TenantA, key, new CreateClientRequest("Created By \"Alpha\""), None);
        Assert.Equal(IdempotencyOutcome.Replayed, replay.Outcome);
        Assert.Equal(created.Entry.Id, replay.Entry.Id);
    }

    [PostgresFact]
    public async Task A_key_another_tenant_holds_conflicts_without_replaying_or_revealing_its_folder()
    {
        // The same key and the same payload as the folder tenant B created with that key: a
        // global lookup would replay B's folder to A. The tenant-scoped one must not.
        var outcome = await Source().CreateClientAsync(
            TenantA, PostgresWorkflowDatabase.KeyHeldByB, new CreateClientRequest(PostgresWorkflowDatabase.KeyedLabelB), None);

        Assert.Equal(IdempotencyOutcome.Conflict, outcome.Outcome);
        Assert.NotEqual(PostgresWorkflowDatabase.KeyedFolderB, outcome.Entry.Id);
        Assert.NotEqual(PostgresWorkflowDatabase.KeyedLabelB, outcome.Entry.DisplayLabel);
        Assert.Equal(1L, await database.Scalar(
            "SELECT count(*) FROM workflow.client_folder WHERE idempotency_key = $1", PostgresWorkflowDatabase.KeyHeldByB));
        Assert.Equal(TenantB, await database.Scalar(
            "SELECT tenant_id FROM workflow.client_folder WHERE idempotency_key = $1", PostgresWorkflowDatabase.KeyHeldByB));
    }

    [PostgresFact]
    public async Task Readiness_queries_the_schema_without_needing_rows()
    {
        await Source().CheckReadinessAsync(None);
    }
}
