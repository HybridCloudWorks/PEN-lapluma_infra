using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace LaPluma.CoreApi.Tests;

public sealed class PostgresCatalogContractTests
{
    [Fact]
    public void CatalogSourceRegistration_PostgresSource_ThrowsWhenConnectionStringMissing()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { CatalogSourceRegistration.SourceSetting, CatalogSourceRegistration.PostgresSource }
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            services.AddCatalogSource(config));

        Assert.Contains(CatalogSourceRegistration.PostgresConnectionStringSetting, ex.Message);
    }

    [Fact]
    public void CatalogSourceRegistration_PostgresSource_RegistersServicesWhenConfigured()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                { CatalogSourceRegistration.SourceSetting, CatalogSourceRegistration.PostgresSource },
                { CatalogSourceRegistration.PostgresConnectionStringSetting, "Host=localhost;Database=lapluma;Username=test;Password=test" }
            })
            .Build();

        services.AddCatalogSource(config);

        var provider = services.BuildServiceProvider();
        var catalogSource = provider.GetService<ICatalogSource>();
        var libraryService = provider.GetService<ILibraryAccessService>();

        Assert.NotNull(catalogSource);
        Assert.IsType<PostgresCatalogSource>(catalogSource);
        Assert.NotNull(libraryService);
        Assert.IsType<PostgresLibraryAccessService>(libraryService);
    }

    [Fact]
    public void PostgreSQLSchema_MatchesCoreAndWorkflowPersistenceContracts()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var librarySql = File.ReadAllText(Path.Combine(root, "infra/sql/001_document_library_schema.sql"));
        var workflowSql = File.ReadAllText(Path.Combine(root, "infra/sql/002_workflow_persistence_schema.sql"));
        var accessSql = File.ReadAllText(Path.Combine(root, "infra/sql/003_library_access_views_and_seeds.sql"));

        // Catalog & library persistence
        Assert.True(librarySql.Contains("CREATE TABLE library.document_blueprint") || librarySql.Contains("CREATE TABLE IF NOT EXISTS library.document_blueprint"));
        Assert.True(librarySql.Contains("CREATE TABLE library.document_collection") || librarySql.Contains("CREATE TABLE IF NOT EXISTS library.document_collection"));
        Assert.True(librarySql.Contains("CREATE TABLE library.collection_blueprint_member") || librarySql.Contains("CREATE TABLE IF NOT EXISTS library.collection_blueprint_member"));
        Assert.True(librarySql.Contains("CREATE TABLE library.tenant_collection_assignment") || librarySql.Contains("CREATE TABLE IF NOT EXISTS library.tenant_collection_assignment"));
        Assert.True(librarySql.Contains("CREATE TABLE library.tenant_blueprint_grant") || librarySql.Contains("CREATE TABLE IF NOT EXISTS library.tenant_blueprint_grant"));

        // Access views
        Assert.Contains("CREATE OR REPLACE VIEW library.v_effective_tenant_collections", accessSql);
        Assert.Contains("CREATE OR REPLACE VIEW library.v_effective_tenant_blueprints", accessSql);

        // Workflow persistence
        Assert.True(workflowSql.Contains("CREATE TABLE workflow.client_folder") || workflowSql.Contains("CREATE TABLE IF NOT EXISTS workflow.client_folder"));
        Assert.Contains("idempotency_key", workflowSql);
        Assert.Contains("payload_hash", workflowSql);
        Assert.True(workflowSql.Contains("CREATE TABLE workflow.folder_person") || workflowSql.Contains("CREATE TABLE IF NOT EXISTS workflow.folder_person"));
        Assert.True(workflowSql.Contains("CREATE TABLE workflow.case_workspace") || workflowSql.Contains("CREATE TABLE IF NOT EXISTS workflow.case_workspace"));
        Assert.True(workflowSql.Contains("CREATE TABLE workflow.case_pinned_blueprint") || workflowSql.Contains("CREATE TABLE IF NOT EXISTS workflow.case_pinned_blueprint"));
        Assert.True(workflowSql.Contains("CREATE TABLE workflow.case_field_value") || workflowSql.Contains("CREATE TABLE IF NOT EXISTS workflow.case_field_value"));
        Assert.True(workflowSql.Contains("CREATE TABLE workflow.case_approval") || workflowSql.Contains("CREATE TABLE IF NOT EXISTS workflow.case_approval"));
        Assert.True(workflowSql.Contains("CREATE TABLE workflow.upload_session") || workflowSql.Contains("CREATE TABLE IF NOT EXISTS workflow.upload_session"));
        Assert.True(workflowSql.Contains("CREATE TABLE workflow.outbox_event") || workflowSql.Contains("CREATE TABLE IF NOT EXISTS workflow.outbox_event"));
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
                { CatalogSourceRegistration.SourceSetting, CatalogSourceRegistration.PostgresSource },
                { CatalogSourceRegistration.PostgresConnectionStringSetting, PrivateIpConnection + ";SslMode=Disable" },
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddCatalogSource(config));
    }
}
