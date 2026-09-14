using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LaPluma.WorkflowApi.Tests;

public sealed class PostgresWorkflowPersistenceTests
{
    [Fact]
    public void WorkflowSourceRegistration_PostgresSource_ThrowsWhenConnectionStringMissing()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { WorkflowSourceRegistration.SourceSetting, WorkflowSourceRegistration.PostgresSource }
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddWorkflowSource(config));

        Assert.Contains(WorkflowSourceRegistration.PostgresConnectionStringSetting, ex.Message);
    }

    [Fact]
    public void WorkflowSourceRegistration_PostgresSource_RegistersServiceWhenConfigured()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { WorkflowSourceRegistration.SourceSetting, WorkflowSourceRegistration.PostgresSource },
                { WorkflowSourceRegistration.PostgresConnectionStringSetting, "Host=localhost;Database=lapluma;Username=test;Password=test" }
            })
            .Build();

        services.AddWorkflowSource(config);

        var provider = services.BuildServiceProvider();
        var source = provider.GetService<IWorkflowSource>();

        Assert.NotNull(source);
        Assert.IsType<PostgresWorkflowSource>(source);
    }

    [Fact]
    public void PostgreSQLSchema_WorkflowTablesIncludeIdempotencyAndDriftGuards()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var workflowSql = File.ReadAllText(Path.Combine(root, "infra/sql/002_workflow_persistence_schema.sql"));

        // Idempotency columns and index on client_folder
        Assert.Contains("idempotency_key VARCHAR(128) NULL UNIQUE", workflowSql);
        Assert.Contains("payload_hash    CHAR(64)     NULL", workflowSql);
        Assert.Contains("CREATE INDEX idx_folder_idempotency", workflowSql);

        // Per-person trust boundary
        Assert.Contains("CREATE TABLE workflow.folder_person", workflowSql);
        Assert.Contains("ck_minor_no_credential CHECK (NOT (is_minor AND holds_own_credential))", workflowSql);

        // Edition drift protection
        Assert.Contains("CREATE TABLE workflow.case_pinned_blueprint", workflowSql);
        Assert.Contains("drift_detected             BOOLEAN", workflowSql);

        // Transactional outbox
        Assert.Contains("CREATE TABLE workflow.outbox_event", workflowSql);
        Assert.Contains("CREATE INDEX idx_outbox_unpublished", workflowSql);
    }
}
