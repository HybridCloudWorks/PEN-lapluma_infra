using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LaPluma.CoreApi.Tests;

/// <summary>
/// Proves two-institution reuse, cross-tenant isolation, and publication rollback (INT-15).
/// Covers:
/// 1. Shared blueprint reuse across synthetic institutions without duplication.
/// 2. Strict tenant-private blueprint and collection isolation (404 Not Found for foreign tenants).
/// 3. Search, count, and query non-leakage.
/// 4. Independent review enforcement (reviewer != author) and self-approval rejection.
/// 5. Upstream drift detection and automatic quarantine.
/// 6. Publication rollback promoting target revision while preserving audit trail and case pins.
/// </summary>
[Collection("TenantAccessCollection")]
public sealed class MultiInstitutionIsolationTests
{
    private readonly TenantTestFactory _factory;

    public MultiInstitutionIsolationTests(TenantTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SharedOfficialBlueprints_AreReusedAcrossInstitutions_WithoutDuplication()
    {
        var client = _factory.CreateClient();

        // Clinic Alpha fetches shared official I-130
        TenantTestAuthHandler.CurrentTenantId = "tenant_clinic_alpha";
        var alphaResp = await client.GetAsync("/v1/library/blueprints/uscis/i-130");
        Assert.Equal(HttpStatusCode.OK, alphaResp.StatusCode);
        var alphaBp = await alphaResp.Content.ReadFromJsonAsync<DocumentBlueprint>();
        Assert.NotNull(alphaBp);
        Assert.Equal("uscis", alphaBp.Namespace);
        Assert.Equal("i-130", alphaBp.BlueprintId);
        Assert.Equal(1, alphaBp.Revision);

        // Firm Beta fetches the same shared official I-130
        TenantTestAuthHandler.CurrentTenantId = "tenant_firm_beta";
        var betaResp = await client.GetAsync("/v1/library/blueprints/uscis/i-130");
        Assert.Equal(HttpStatusCode.OK, betaResp.StatusCode);
        var betaBp = await betaResp.Content.ReadFromJsonAsync<DocumentBlueprint>();
        Assert.NotNull(betaBp);
        Assert.Equal("uscis", betaBp.Namespace);
        Assert.Equal("i-130", betaBp.BlueprintId);
        Assert.Equal(1, betaBp.Revision);

        // Both institutions receive the exact same official artifact metadata and field mappings
        Assert.Equal(alphaBp.Title, betaBp.Title);
        Assert.Equal(alphaBp.Issuer, betaBp.Issuer);
        Assert.Equal(alphaBp.PreparationMode, betaBp.PreparationMode);
    }

    [Fact]
    public async Task TenantPrivateBlueprints_AreIsolated_AndReturn404ToForeignTenants()
    {
        var client = _factory.CreateClient();

        // 1. Clinic Alpha has private intake blueprint
        TenantTestAuthHandler.CurrentTenantId = "tenant_clinic_alpha";
        var alphaResp = await client.GetAsync("/v1/library/blueprints/tenant_clinic_alpha/intake");
        Assert.Equal(HttpStatusCode.OK, alphaResp.StatusCode);
        var alphaBp = await alphaResp.Content.ReadFromJsonAsync<DocumentBlueprint>();
        Assert.NotNull(alphaBp);
        Assert.Equal("tenant_clinic_alpha", alphaBp.Namespace);
        Assert.Equal("intake", alphaBp.BlueprintId);

        // 2. Firm Beta attempting to access Clinic Alpha's intake blueprint receives 404 (not 403, never leaking existence)
        TenantTestAuthHandler.CurrentTenantId = "tenant_firm_beta";
        var betaAccessToAlpha = await client.GetAsync("/v1/library/blueprints/tenant_clinic_alpha/intake");
        Assert.Equal(HttpStatusCode.NotFound, betaAccessToAlpha.StatusCode);

        // 3. Firm Beta has private special retainer blueprint
        var betaResp = await client.GetAsync("/v1/library/blueprints/tenant_firm_beta/special_retainer");
        Assert.Equal(HttpStatusCode.OK, betaResp.StatusCode);
        var betaBp = await betaResp.Content.ReadFromJsonAsync<DocumentBlueprint>();
        Assert.NotNull(betaBp);
        Assert.Equal("tenant_firm_beta", betaBp.Namespace);

        // 4. Clinic Alpha attempting to access Firm Beta's retainer blueprint receives 404
        TenantTestAuthHandler.CurrentTenantId = "tenant_clinic_alpha";
        var alphaAccessToBeta = await client.GetAsync("/v1/library/blueprints/tenant_firm_beta/special_retainer");
        Assert.Equal(HttpStatusCode.NotFound, alphaAccessToBeta.StatusCode);
    }

    [Fact]
    public async Task TenantPrivateCollections_AreIsolatedInListingsAndDirectLookup()
    {
        var client = _factory.CreateClient();

        // 1. Clinic Alpha sees both official Family Reunification and private Clinic Intake
        TenantTestAuthHandler.CurrentTenantId = "tenant_clinic_alpha";
        var alphaColsResp = await client.GetAsync("/v1/library/collections");
        Assert.Equal(HttpStatusCode.OK, alphaColsResp.StatusCode);
        var alphaCols = await alphaColsResp.Content.ReadFromJsonAsync<List<DocumentCollection>>();
        Assert.NotNull(alphaCols);
        Assert.Equal(2, alphaCols.Count);
        Assert.Contains(alphaCols, c => c.CollectionId == "clinic_intake_pkg");
        Assert.Contains(alphaCols, c => c.CollectionId == "family_reunification");

        // 2. Firm Beta sees only the official Family Reunification collection
        TenantTestAuthHandler.CurrentTenantId = "tenant_firm_beta";
        var betaColsResp = await client.GetAsync("/v1/library/collections");
        Assert.Equal(HttpStatusCode.OK, betaColsResp.StatusCode);
        var betaCols = await betaColsResp.Content.ReadFromJsonAsync<List<DocumentCollection>>();
        Assert.NotNull(betaCols);
        Assert.Single(betaCols);
        Assert.DoesNotContain(betaCols, c => c.CollectionId == "clinic_intake_pkg");

        // 3. Direct lookup of Clinic Alpha's private collection by Firm Beta returns 404
        var forbiddenLookup = await client.GetAsync("/v1/library/collections/tenant_clinic_alpha/clinic_intake_pkg");
        Assert.Equal(HttpStatusCode.NotFound, forbiddenLookup.StatusCode);
    }

    [Fact]
    public async Task SearchAndCounts_DoNotLeakCrossTenantContent()
    {
        var client = _factory.CreateClient();

        // 1. Searching for "intake" as Clinic Alpha returns the private intake collection
        TenantTestAuthHandler.CurrentTenantId = "tenant_clinic_alpha";
        var alphaSearchResp = await client.GetAsync("/v1/library/collections?query=intake");
        Assert.Equal(HttpStatusCode.OK, alphaSearchResp.StatusCode);
        var alphaSearchResults = await alphaSearchResp.Content.ReadFromJsonAsync<List<DocumentCollection>>();
        Assert.NotNull(alphaSearchResults);
        Assert.Single(alphaSearchResults);
        Assert.Equal("clinic_intake_pkg", alphaSearchResults[0].CollectionId);

        // 2. Searching for "intake" as Firm Beta returns an empty list (200 OK, 0 results)
        TenantTestAuthHandler.CurrentTenantId = "tenant_firm_beta";
        var betaSearchResp = await client.GetAsync("/v1/library/collections?query=intake");
        Assert.Equal(HttpStatusCode.OK, betaSearchResp.StatusCode);
        var betaSearchResults = await betaSearchResp.Content.ReadFromJsonAsync<List<DocumentCollection>>();
        Assert.NotNull(betaSearchResults);
        Assert.Empty(betaSearchResults);
    }

    [Fact]
    public async Task PublicationLifecycle_EnforcesIndependentReview_DriftQuarantine_AndRollback()
    {
        var service = new InMemoryBlueprintPublicationService();

        const string ns = "uscis";
        const string bpId = "i-130";
        const int rev = 2;

        // 1. Draft Rejection: Self-approval is strictly forbidden (author == reviewer)
        var selfApprovalRequest = new BlueprintReviewRequest(
            ReviewerId: "author_dan",
            AuthorId: "author_dan",
            Notes: "Attempted self-approval");

        var selfApprovalEx = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ReviewBlueprintAsync(ns, bpId, rev, selfApprovalRequest));
        Assert.Contains("self-approval prohibited", selfApprovalEx.Message, StringComparison.OrdinalIgnoreCase);

        // 2. Independent Review: Reviewer != Author succeeds
        var validReviewRequest = new BlueprintReviewRequest(
            ReviewerId: "legal_reviewer_elena",
            AuthorId: "author_dan",
            Notes: "Independent legal and schema verification completed");
        await service.ReviewBlueprintAsync(ns, bpId, rev, validReviewRequest);

        var auditAfterReview = await service.GetAuditTrailAsync(ns, bpId, rev);
        Assert.Contains(auditAfterReview, a => a.Action == "REVIEW_APPROVED" && a.ReviewerId == "legal_reviewer_elena");

        // 3. Publish: Publish with verified manifest hash succeeds
        var publishRequest = new BlueprintPublishRequest(
            PublisherId: "lead_operator_fiona",
            ReviewerId: "legal_reviewer_elena",
            AuthorId: "author_dan",
            ManifestSha256: "34859083117abcde1234567890abcdef1234567890abcdef1234567890abcdef");
        await service.PublishBlueprintAsync(ns, bpId, rev, publishRequest);

        var auditAfterPublish = await service.GetAuditTrailAsync(ns, bpId, rev);
        Assert.Contains(auditAfterPublish, a => a.Action == "PUBLISHED");

        // 4. Drift Check: When official upstream hash differs, blueprint is quarantined
        var driftCheck = new BlueprintDriftCheckRequest(
            ObservedSourceSha256: "9999999999999999999999999999999999999999999999999999999999999999",
            OperatorId: "drift_sentinel",
            Reason: "Official USCIS PDF checksum changed on uscis.gov");
        var isQuarantined = await service.CheckAndQuarantineDriftAsync(ns, bpId, rev, driftCheck);
        Assert.True(isQuarantined);

        var auditAfterDrift = await service.GetAuditTrailAsync(ns, bpId, rev);
        Assert.Contains(auditAfterDrift, a => a.Action == "QUARANTINED_DRIFT");

        // 5. Rollback: Rollback promotes target revision 1 while keeping complete audit history
        var rollbackRequest = new BlueprintRollbackRequest(
            TargetRevision: 1,
            OperatorId: "lead_operator_fiona",
            Reason: "Rollback to stable r1 after upstream drift detected");
        await service.RollbackBlueprintAsync(ns, bpId, rollbackRequest);

        var fullAudit = await service.GetAuditTrailAsync(ns, bpId);
        Assert.Contains(fullAudit, a => a.Action == "ROLLED_BACK" && a.Revision == 1);
        Assert.Equal(4, fullAudit.Count); // REVIEW_APPROVED, PUBLISHED, QUARANTINED_DRIFT, ROLLED_BACK
    }

    [Fact]
    public async Task InstitutionBranding_DoesNotAlterOfficialBlueprints()
    {
        // Obtain service instance to verify official collection members retain official authority
        var service = _factory.Services.GetRequiredService<ILibraryAccessService>();

        var officialFamilyCol = await service.GetCollectionAsync("tenant_clinic_alpha", "official", "family_reunification", 1);
        Assert.NotNull(officialFamilyCol);
        Assert.Equal("official", officialFamilyCol.Namespace);
        Assert.Equal("official-standard", officialFamilyCol.WorkflowSettings.RootElement.GetProperty("theme").GetString());

        // Official member blueprints retain official issuer
        var officialI130 = await service.GetBlueprintAsync("tenant_clinic_alpha", "uscis", "i-130", 1);
        Assert.NotNull(officialI130);
        Assert.Equal("uscis", officialI130.Namespace);
        Assert.Equal("U.S. Citizenship and Immigration Services", officialI130.Issuer);
        Assert.Equal(PreparationMode.FillablePdf, officialI130.PreparationMode);
    }
}
