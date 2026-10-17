using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
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
        Assert.True(workflowSql.Contains("CREATE INDEX idx_folder_idempotency") || workflowSql.Contains("CREATE INDEX IF NOT EXISTS idx_folder_idempotency"));

        // Per-person trust boundary
        Assert.True(workflowSql.Contains("CREATE TABLE workflow.folder_person") || workflowSql.Contains("CREATE TABLE IF NOT EXISTS workflow.folder_person"));
        Assert.Contains("ck_minor_no_credential CHECK (NOT (is_minor AND holds_own_credential))", workflowSql);

        // Edition drift protection
        Assert.True(workflowSql.Contains("CREATE TABLE workflow.case_pinned_blueprint") || workflowSql.Contains("CREATE TABLE IF NOT EXISTS workflow.case_pinned_blueprint"));
        Assert.Contains("drift_detected             BOOLEAN", workflowSql);

        // Transactional outbox
        Assert.True(workflowSql.Contains("CREATE TABLE workflow.outbox_event") || workflowSql.Contains("CREATE TABLE IF NOT EXISTS workflow.outbox_event"));
        Assert.True(workflowSql.Contains("CREATE INDEX idx_outbox_unpublished") || workflowSql.Contains("CREATE INDEX IF NOT EXISTS idx_outbox_unpublished"));
    }
}

/// <summary>
/// INF-27, client half: the service never opens an unencrypted PostgreSQL connection, whatever
/// the connection string says. Cloud SQL refuses plaintext from its side too (ENCRYPTED_ONLY).
/// </summary>
public sealed class PostgresConnectionSecurityTests
{
    private const string PrivateIpConnection = "Host=10.247.80.3;Port=5432;Database=lapluma;Username=lapluma_app;Password=not-a-real-password";

    [Theory]
    [InlineData("")]
    [InlineData(";SslMode=Prefer")]
    [InlineData(";SslMode=Require")]
    public void An_unset_or_opportunistic_mode_becomes_require(string suffix)
    {
        var secured = new NpgsqlConnectionStringBuilder(
            PostgresConnectionSecurity.RequireEncryption(PrivateIpConnection + suffix));

        Assert.Equal(SslMode.Require, secured.SslMode);
        Assert.Equal("10.247.80.3", secured.Host);
    }

    [Theory]
    [InlineData("VerifyCA", SslMode.VerifyCA)]
    [InlineData("VerifyFull", SslMode.VerifyFull)]
    public void A_stricter_mode_is_kept(string mode, SslMode expected)
    {
        var secured = new NpgsqlConnectionStringBuilder(
            PostgresConnectionSecurity.RequireEncryption($"{PrivateIpConnection};SslMode={mode}"));

        Assert.Equal(expected, secured.SslMode);
    }

    [Theory]
    [InlineData("Disable")]
    [InlineData("Allow")]
    public void A_plaintext_mode_is_refused_without_echoing_the_password(string mode)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            PostgresConnectionSecurity.RequireEncryption($"{PrivateIpConnection};SslMode={mode}"));

        Assert.Contains("SslMode", error.Message);
        Assert.DoesNotContain("not-a-real-password", error.ToString());
    }

    [Fact]
    public void Registration_refuses_a_plaintext_connection_string()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { WorkflowSourceRegistration.SourceSetting, WorkflowSourceRegistration.PostgresSource },
                { WorkflowSourceRegistration.PostgresConnectionStringSetting, PrivateIpConnection + ";SslMode=Disable" },
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddWorkflowSource(config));
    }
}
