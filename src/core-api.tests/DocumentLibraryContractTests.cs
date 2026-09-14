using System.Text.Json;
using Xunit;

namespace LaPluma.CoreApi.Tests;

public sealed class DocumentLibraryContractTests
{
    [Fact]
    public void DocumentBlueprint_SerializesWithExpectedEnumsAndProperties()
    {
        var jsonDoc = JsonDocument.Parse("{\"fields\": [{\"name\": \"applicant.name\", \"type\": \"string\"}]}");
        var emptyArray = JsonDocument.Parse("[]");

        var blueprint = new DocumentBlueprint(
            Namespace: "uscis",
            BlueprintId: "i-130",
            Revision: 1,
            Title: "Petition for Alien Relative",
            Issuer: "USCIS",
            OfficialEditionDate: new DateOnly(2024, 4, 1),
            PreparationMode: PreparationMode.FillablePdf,
            ArtifactType: BlueprintArtifactType.OfficialPdf,
            SourceUrl: new Uri("https://www.uscis.gov/i-130"),
            SourceSha256: "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            FieldsSchema: jsonDoc,
            ValidationRules: emptyArray,
            EvidenceRequirements: emptyArray,
            PublicationState: PublicationState.Published,
            ReviewedBy: "architect@lapluma.internal",
            ReviewedAt: DateTimeOffset.UtcNow,
            PublishedAt: DateTimeOffset.UtcNow,
            IsLatest: true,
            CreatedAt: DateTimeOffset.UtcNow);

        var serialized = JsonSerializer.Serialize(blueprint);
        Assert.Contains("\"FILLABLE_PDF\"", serialized);
        Assert.Contains("\"OFFICIAL_PDF\"", serialized);
        Assert.Contains("\"PUBLISHED\"", serialized);
        Assert.Contains("\"uscis\"", serialized);
        Assert.Contains("\"i-130\"", serialized);

        var deserialized = JsonSerializer.Deserialize<DocumentBlueprint>(serialized);
        Assert.NotNull(deserialized);
        Assert.Equal("uscis", deserialized.Namespace);
        Assert.Equal("i-130", deserialized.BlueprintId);
        Assert.Equal(1, deserialized.Revision);
        Assert.Equal(PreparationMode.FillablePdf, deserialized.PreparationMode);
        Assert.Equal(PublicationState.Published, deserialized.PublicationState);
    }

    [Fact]
    public void DocumentCollection_SerializesWithMembersAndSettings()
    {
        var settings = JsonDocument.Parse("{\"theme\": \"pastel\", \"displayOrder\": 1}");
        var member = new CollectionMember("uscis", "i-130", 1, 0, true);

        var collection = new DocumentCollection(
            Namespace: "official",
            CollectionId: "family_reunification",
            Revision: 1,
            Title: "Family Reunification",
            Description: "Official USCIS Family Reunification Collection",
            WorkflowSettings: settings,
            PublicationState: PublicationState.Published,
            IsLatest: true,
            CreatedAt: DateTimeOffset.UtcNow,
            Members: new[] { member });

        var serialized = JsonSerializer.Serialize(collection);
        Assert.Contains("\"family_reunification\"", serialized);
        Assert.Contains("\"official\"", serialized);
        Assert.Contains("\"PUBLISHED\"", serialized);
        Assert.Single(collection.Members);
    }

    [Fact]
    public void PostgreSQLSchemaFiles_ExistAndContainRequiredTables()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var librarySqlPath = Path.Combine(root, "infra/sql/001_document_library_schema.sql");
        var workflowSqlPath = Path.Combine(root, "infra/sql/002_workflow_persistence_schema.sql");

        Assert.True(File.Exists(librarySqlPath), $"Expected {librarySqlPath} to exist.");
        Assert.True(File.Exists(workflowSqlPath), $"Expected {workflowSqlPath} to exist.");

        var librarySql = File.ReadAllText(librarySqlPath);
        Assert.Contains("CREATE TABLE library.document_blueprint", librarySql);
        Assert.Contains("CREATE TABLE library.document_collection", librarySql);
        Assert.Contains("CREATE TABLE library.collection_blueprint_member", librarySql);
        Assert.Contains("CREATE TABLE library.tenant_collection_assignment", librarySql);
        Assert.Contains("CREATE TABLE library.tenant_blueprint_grant", librarySql);
        Assert.Contains("GIN (fields_schema)", librarySql);

        var workflowSql = File.ReadAllText(workflowSqlPath);
        Assert.Contains("CREATE TABLE workflow.case_workspace", workflowSql);
        Assert.Contains("CREATE TABLE workflow.case_pinned_blueprint", workflowSql);
        Assert.Contains("CREATE TABLE workflow.case_field_value", workflowSql);
        Assert.Contains("CREATE TABLE workflow.case_approval", workflowSql);
        Assert.Contains("CREATE TABLE workflow.upload_session", workflowSql);
        Assert.Contains("CREATE TABLE workflow.outbox_event", workflowSql);
    }
}
