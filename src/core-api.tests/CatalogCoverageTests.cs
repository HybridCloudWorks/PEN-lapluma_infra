using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace LaPluma.CoreApi.Tests;

[Collection("TenantAccessCollection")]
public sealed class CatalogCoverageTests(TenantTestFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task ListBlueprints_ReturnsFullUscisCatalog_AtLeast117Forms()
    {
        TenantTestAuthHandler.CurrentTenantId = "tenant_clinic_alpha";
        var response = await _client.GetAsync("/v1/library/blueprints");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var blueprints = await response.Content.ReadFromJsonAsync<List<DocumentBlueprint>>();
        Assert.NotNull(blueprints);

        // Verify we have all 117 USCIS forms plus existing seeds
        var uscisBlueprints = blueprints.Where(b => b.Namespace == "uscis").ToList();
        Assert.True(uscisBlueprints.Count >= 117, $"Expected at least 117 USCIS forms, got {uscisBlueprints.Count}");

        // Spot-check key forms across all 4 series
        // I-Series (91)
        Assert.Contains(uscisBlueprints, b => b.BlueprintId == "i-130");
        Assert.Contains(uscisBlueprints, b => b.BlueprintId == "i-485");
        Assert.Contains(uscisBlueprints, b => b.BlueprintId == "i-765");
        Assert.Contains(uscisBlueprints, b => b.BlueprintId == "i-129");
        Assert.Contains(uscisBlueprints, b => b.BlueprintId == "i-589");

        // N-Series (10)
        Assert.Contains(uscisBlueprints, b => b.BlueprintId == "n-400");
        Assert.Contains(uscisBlueprints, b => b.BlueprintId == "n-600");

        // G-Series (14)
        Assert.Contains(uscisBlueprints, b => b.BlueprintId == "g-28");
        Assert.Contains(uscisBlueprints, b => b.BlueprintId == "g-1055");

        // Other-Series (2: ar-11, eoir-29)
        Assert.Contains(uscisBlueprints, b => b.BlueprintId == "ar-11");
        Assert.Contains(uscisBlueprints, b => b.BlueprintId == "eoir-29");
    }

    [Fact]
    public async Task PreservedNonUscisDefinitions_AreDiscoverableInLibrary()
    {
        TenantTestAuthHandler.CurrentTenantId = "tenant_clinic_alpha";
        var response = await _client.GetAsync("/v1/library/blueprints");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var blueprints = await response.Content.ReadFromJsonAsync<List<DocumentBlueprint>>();
        Assert.NotNull(blueprints);

        // DS-11 (Department of State passport application)
        Assert.Contains(blueprints, b => b.BlueprintId == "ds-11" && b.Namespace == "official");

        // FAFSA (Federal Student Aid online application)
        Assert.Contains(blueprints, b => b.BlueprintId == "fafsa" && b.Namespace == "official");

        // CLINIC-INTAKE
        Assert.Contains(blueprints, b => b.BlueprintId.Contains("intake"));
    }

    [Fact]
    public async Task BlueprintSearch_FiltersByQueryTerm()
    {
        TenantTestAuthHandler.CurrentTenantId = "tenant_clinic_alpha";

        // Query by form number: "n-400"
        var n400Resp = await _client.GetAsync("/v1/library/blueprints?query=n-400");
        Assert.Equal(HttpStatusCode.OK, n400Resp.StatusCode);
        var n400List = await n400Resp.Content.ReadFromJsonAsync<List<DocumentBlueprint>>();
        Assert.NotNull(n400List);
        Assert.Contains(n400List, b => b.BlueprintId == "n-400");

        // Query by title keyword: "naturalization"
        var natResp = await _client.GetAsync("/v1/library/blueprints?query=naturalization");
        Assert.Equal(HttpStatusCode.OK, natResp.StatusCode);
        var natList = await natResp.Content.ReadFromJsonAsync<List<DocumentBlueprint>>();
        Assert.NotNull(natList);
        Assert.Contains(natList, b => b.BlueprintId == "n-400");
    }

    [Fact]
    public async Task GetDocumentGuidance_ReturnsSourceCitedGuidance_ForI130()
    {
        TenantTestAuthHandler.CurrentTenantId = "tenant_clinic_alpha";
        var response = await _client.GetAsync("/v1/library/blueprints/uscis/i-130/guidance");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var guidance = await response.Content.ReadFromJsonAsync<DocumentGuidance>();
        Assert.NotNull(guidance);
        Assert.Equal("I-130", guidance.FormId);
        Assert.Equal("USCIS", guidance.Authority);
        Assert.NotNull(guidance.OfficialInstructionsUrl);
        Assert.Contains("i-130instr.pdf", guidance.OfficialInstructionsUrl.ToString());
        Assert.Equal("https://www.uscis.gov/g-1055", guidance.FeeScheduleCitationUrl.ToString());

        // I-130 has a uniform statutory fee of $675 (67500 cents)
        Assert.Equal(67500, guidance.FeeUsdCents);
        Assert.NotEmpty(guidance.EvidenceChecklist);
        Assert.NotNull(guidance.InstitutionalGuidanceNotes);
    }

    [Fact]
    public async Task GetDocumentGuidance_EnforcesZeroFeeGuessing_ForVariableFeeForms()
    {
        TenantTestAuthHandler.CurrentTenantId = "tenant_clinic_alpha";

        // N-400 fee varies based on household income and online vs paper filing
        var response = await _client.GetAsync("/v1/library/blueprints/uscis/n-400/guidance");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var guidance = await response.Content.ReadFromJsonAsync<DocumentGuidance>();
        Assert.NotNull(guidance);
        Assert.Equal("N-400", guidance.FormId);
        // Zero fee guessing: feeUsdCents is null, citing official G-1055
        Assert.Null(guidance.FeeUsdCents);
        Assert.Contains("G-1055", guidance.FeeNotes);
        Assert.Equal("https://www.uscis.gov/g-1055", guidance.FeeScheduleCitationUrl.ToString());
    }

    [Fact]
    public async Task GetDocumentGuidance_Returns404_ForUnknownBlueprint()
    {
        TenantTestAuthHandler.CurrentTenantId = "tenant_clinic_alpha";
        var response = await _client.GetAsync("/v1/library/blueprints/uscis/nonexistent-form-xyz/guidance");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
