using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace LaPluma.WorkflowApi.Tests;

public sealed class CaseReviewAndApprovalTests : IClassFixture<AuthenticatedFactory>
{
    private readonly AuthenticatedFactory factory;

    public CaseReviewAndApprovalTests(AuthenticatedFactory factory) => this.factory = factory;

    private HttpClient Client() => factory.CreateClient();

    private static HttpRequestMessage PostWithKey(string url, object body, string key = "test-key")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(body)
        };
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task ReviewQueue_ReturnsAssignedReviewableCases()
    {
        var client = Client();
        var response = await client.GetAsync("/v1/review-queue");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJson(response);
        Assert.Equal(JsonValueKind.Array, body.ValueKind);

        var cases = body.EnumerateArray().ToArray();
        Assert.NotEmpty(cases);

        var reviewItem = cases.FirstOrDefault(c =>
            c.GetProperty("caseSummary").GetProperty("id").GetString() == WorkflowFixtureSource.FixtureQueueCaseId);
        Assert.NotNull(reviewItem.GetRawText());
        Assert.Equal("Fixture Queue Client", reviewItem.GetProperty("clientLabel").GetString());
        Assert.Equal("IN_REVIEW", reviewItem.GetProperty("caseSummary").GetProperty("state").GetString());
    }

    [Fact]
    public async Task ReviewDecision_RequiresIdempotencyKey()
    {
        var client = Client();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/cases/{WorkflowFixtureSource.FixtureReviewCaseId}/review-decisions")
        {
            Content = JsonContent.Create(new ReviewDecisionRequest("READY_FOR_APPROVAL", "Looks good"))
        };

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadJson(response);
        Assert.Equal("urn:lapluma:problem:idempotency-key-required", body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task ReviewDecision_RejectsInvalidOutcome()
    {
        var client = Client();
        var response = await client.SendAsync(PostWithKey(
            $"/v1/cases/{WorkflowFixtureSource.FixtureReviewCaseId}/review-decisions",
            new ReviewDecisionRequest("INVALID_OUTCOME", "Note"),
            "key-decide-invalid"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var body = await ReadJson(response);
        Assert.Equal("urn:lapluma:problem:review-outcome-invalid", body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task CompleteReview_Approval_Output_And_Invalidation_RoundTrip()
    {
        var client = Client();
        var caseId = WorkflowFixtureSource.FixtureReviewCaseId;

        // 1. Record Review Decision -> READY_FOR_APPROVAL
        var decideResp = await client.SendAsync(PostWithKey(
            $"/v1/cases/{caseId}/review-decisions",
            new ReviewDecisionRequest("READY_FOR_APPROVAL", "All evidence checked and verified"),
            "key-decide-1"));

        Assert.Equal(HttpStatusCode.Created, decideResp.StatusCode);
        var decideBody = await ReadJson(decideResp);
        Assert.Equal("READY_FOR_APPROVAL", decideBody.GetProperty("outcome").GetString());

        // 2. Mint Draft Preview with watermark and hashes
        var previewResp = await client.SendAsync(PostWithKey(
            $"/v1/cases/{caseId}/draft-preview",
            new { },
            "key-preview-1"));

        Assert.Equal(HttpStatusCode.Created, previewResp.StatusCode);
        var previewBody = await ReadJson(previewResp);
        Assert.Equal("DRAFT â€” NOT FOR FILING", previewBody.GetProperty("watermark").GetString());
        var valueSetHash = previewBody.GetProperty("valueSetHash").GetString();
        var editionSetHash = previewBody.GetProperty("editionSetHash").GetString();
        Assert.False(string.IsNullOrWhiteSpace(valueSetHash));
        Assert.False(string.IsNullOrWhiteSpace(editionSetHash));

        var preview = new DraftFormPreview(
            caseId,
            previewBody.GetProperty("watermark").GetString()!,
            previewBody.GetProperty("pageCount").GetInt32(),
            valueSetHash!,
            editionSetHash!,
            previewBody.GetProperty("expiresAt").GetDateTimeOffset());

        // 3. Create Step-Up Challenge
        var challengeResp = await client.SendAsync(PostWithKey(
            $"/v1/cases/{caseId}/step-up-challenge",
            new { },
            "key-challenge-1"));

        Assert.Equal(HttpStatusCode.Created, challengeResp.StatusCode);
        var challengeBody = await ReadJson(challengeResp);
        var challengeToken = challengeBody.GetProperty("challengeToken").GetString();
        Assert.NotNull(challengeToken);
        Assert.Equal(64, challengeToken.Length);

        // 4. Submit Approval with Stale Hash -> Rejected (409 stale-preview)
        var stalePreview = preview with { ValueSetHash = "stale-hash-value" };
        var staleApproveResp = await client.SendAsync(PostWithKey(
            $"/v1/cases/{caseId}/approval",
            new CaseApprovalRequest(stalePreview, challengeToken, Attested: true),
            "key-approve-stale"));

        Assert.Equal(HttpStatusCode.Conflict, staleApproveResp.StatusCode);
        var staleBody = await ReadJson(staleApproveResp);
        Assert.Equal("urn:lapluma:problem:stale-preview", staleBody.GetProperty("type").GetString());

        // 5. Submit Valid Approval -> 201 Created
        var approveResp = await client.SendAsync(PostWithKey(
            $"/v1/cases/{caseId}/approval",
            new CaseApprovalRequest(preview, challengeToken, Attested: true),
            "key-approve-valid"));

        Assert.Equal(HttpStatusCode.Created, approveResp.StatusCode);
        var approveBody = await ReadJson(approveResp);
        Assert.True(approveBody.GetProperty("valid").GetBoolean());
        Assert.Equal(caseId, approveBody.GetProperty("caseId").GetString());

        // 6. Request Package Generation on Approved Case -> 201 Created
        var packageResp = await client.SendAsync(PostWithKey(
            $"/v1/cases/{caseId}/package-generation",
            new { },
            "key-pkg-1"));

        Assert.Equal(HttpStatusCode.Created, packageResp.StatusCode);
        var packageBody = await ReadJson(packageResp);
        Assert.True(packageBody.GetProperty("verification").GetProperty("passed").GetBoolean());
        Assert.Equal(0, packageBody.GetProperty("verification").GetProperty("mismatches").GetInt32());
        Assert.Equal(12, packageBody.GetProperty("verification").GetProperty("fieldsVerified").GetInt32());

        // 7. Canonical Section Edit -> Invalidates Approval
        var commitResp = await client.SendAsync(PostWithKey(
            $"/v1/cases/{caseId}/sections/identity/commit",
            new CommitSectionRequest(1, new Dictionary<string, string> { ["applicant.name.first"] = "UpdatedName" }),
            "key-commit-1"));

        Assert.Equal(HttpStatusCode.OK, commitResp.StatusCode);
        var commitBody = await ReadJson(commitResp);
        Assert.True(commitBody.GetProperty("invalidatedApproval").GetBoolean());

        // 8. Package Generation after Invalidation -> Rejected (409 approval-invalidated)
        var invalidPkgResp = await client.SendAsync(PostWithKey(
            $"/v1/cases/{caseId}/package-generation",
            new { },
            "key-pkg-2"));

        Assert.Equal(HttpStatusCode.Conflict, invalidPkgResp.StatusCode);
        var invalidPkgBody = await ReadJson(invalidPkgResp);
        Assert.Equal("urn:lapluma:problem:approval-invalidated", invalidPkgBody.GetProperty("type").GetString());

        // 9. Verify Full Audit History
        var historyResp = await client.GetAsync($"/v1/cases/{caseId}/history");
        Assert.Equal(HttpStatusCode.OK, historyResp.StatusCode);
        var historyBody = await ReadJson(historyResp);
        var events = historyBody.EnumerateArray().Select(e => e.GetProperty("kind").GetString()).ToArray();

        Assert.Contains("REVIEW_DECIDED", events);
        Assert.Contains("APPROVED", events);
        Assert.Contains("PACKAGE_GENERATED", events);
        Assert.Contains("SECTION_COMMITTED", events);
    }
}