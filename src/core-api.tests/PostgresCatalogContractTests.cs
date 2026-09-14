using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        Assert.Contains("CREATE TABLE library.document_blueprint", librarySql);
        Assert.Contains("CREATE TABLE library.document_collection", librarySql);
        Assert.Contains("CREATE TABLE library.collection_blueprint_member", librarySql);
        Assert.Contains("CREATE TABLE library.tenant_collection_assignment", librarySql);
        Assert.Contains("CREATE TABLE library.tenant_blueprint_grant", librarySql);

        // Access views
        Assert.Contains("CREATE OR REPLACE VIEW library.v_effective_tenant_collections", accessSql);
        Assert.Contains("CREATE OR REPLACE VIEW library.v_effective_tenant_blueprints", accessSql);

        // Workflow persistence
        Assert.Contains("CREATE TABLE workflow.client_folder", workflowSql);
        Assert.Contains("idempotency_key", workflowSql);
        Assert.Contains("payload_hash", workflowSql);
        Assert.Contains("CREATE TABLE workflow.folder_person", workflowSql);
        Assert.Contains("CREATE TABLE workflow.case_workspace", workflowSql);
        Assert.Contains("CREATE TABLE workflow.case_pinned_blueprint", workflowSql);
        Assert.Contains("CREATE TABLE workflow.case_field_value", workflowSql);
        Assert.Contains("CREATE TABLE workflow.case_approval", workflowSql);
        Assert.Contains("CREATE TABLE workflow.upload_session", workflowSql);
        Assert.Contains("CREATE TABLE workflow.outbox_event", workflowSql);
    }
}
