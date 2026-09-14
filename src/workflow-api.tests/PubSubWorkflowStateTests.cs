using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace LaPluma.WorkflowApi.Tests;

public sealed class PubSubWorkflowStateTests : IClassFixture<AuthenticatedFactory>
{
    private readonly AuthenticatedFactory factory;

    public PubSubWorkflowStateTests(AuthenticatedFactory factory)
    {
        this.factory = factory;
    }

    private HttpClient CreateClient() => factory.CreateClient();

    private static HttpRequestMessage Request(HttpMethod method, string url, object? body = null, string? idempotencyKey = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (body != null)
        {
            req.Content = JsonContent.Create(body);
        }
        if (idempotencyKey != null)
        {
            req.Headers.Add("Idempotency-Key", idempotencyKey);
        }
        return req;
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Scoped_package_download_returns_short_lived_grant_and_allows_renewal()
    {
        var client = CreateClient();
        var caseId = "case-fixture-0002";

        // 1. Advance case to ready for approval
        var reviewResp = await client.SendAsync(Request(
            HttpMethod.Post, $"/v1/cases/{caseId}/review-decisions",
            new { outcome = "READY_FOR_APPROVAL", note = "Approved for download test" },
            Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.Created, reviewResp.StatusCode);

        // 2. Draft preview
        var previewResp = await client.SendAsync(Request(
            HttpMethod.Post, $"/v1/cases/{caseId}/draft-preview", null, Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.Created, previewResp.StatusCode);
        var previewJson = await ReadJson(previewResp);

        // 3. Step-up challenge and approval
        var challengeResp = await client.SendAsync(Request(
            HttpMethod.Post, $"/v1/cases/{caseId}/step-up-challenge", null, Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.Created, challengeResp.StatusCode);
        var challengeToken = (await ReadJson(challengeResp)).GetProperty("challengeToken").GetString();

        var approvalResp = await client.SendAsync(Request(
            HttpMethod.Post, $"/v1/cases/{caseId}/approval",
            new { preview = previewJson, stepUpChallenge = challengeToken, attested = true },
            Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.Created, approvalResp.StatusCode);

        // 4. Generate package
        var genResp = await client.SendAsync(Request(
            HttpMethod.Post, $"/v1/cases/{caseId}/package-generation", null, Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.Created, genResp.StatusCode);
        var packageJson = await ReadJson(genResp);
        var packageId = packageJson.GetProperty("id").GetString();
        Assert.NotNull(packageId);

        // Verify package carries initial downloadGrant
        Assert.True(packageJson.TryGetProperty("downloadGrant", out var initGrant));
        Assert.Contains("storage.googleapis.com", initGrant.GetProperty("downloadUrl").GetString());

        // 5. Query scoped download grant
        var downloadResp = await client.GetAsync($"/v1/cases/{caseId}/packages/{packageId}/download");
        Assert.Equal(HttpStatusCode.OK, downloadResp.StatusCode);
        var downloadJson = await ReadJson(downloadResp);

        Assert.Equal(packageId, downloadJson.GetProperty("packageId").GetString());
        Assert.Equal(caseId, downloadJson.GetProperty("caseId").GetString());
        var downloadUrl = downloadJson.GetProperty("downloadUrl").GetString();
        Assert.NotNull(downloadUrl);
        Assert.Contains("storage.googleapis.com", downloadUrl);
        Assert.Contains("X-Goog-Algorithm=GOOG4-RSA-SHA256", downloadUrl);
        Assert.True(downloadJson.GetProperty("sizeBytes").GetInt64() > 0);

        // 6. Requesting download grant again succeeds (renewing grant without regenerating)
        var renewResp = await client.GetAsync($"/v1/cases/{caseId}/packages/{packageId}/download");
        Assert.Equal(HttpStatusCode.OK, renewResp.StatusCode);
        var renewJson = await ReadJson(renewResp);
        Assert.Equal(packageId, renewJson.GetProperty("packageId").GetString());
    }

    [Fact]
    public async Task Scoped_package_download_refuses_when_approval_is_invalidated()
    {
        var client = CreateClient();
        var caseId = "case-fixture-0002";

        // Commit section to invalidate approval
        var commitResp = await client.SendAsync(Request(
            HttpMethod.Post, $"/v1/cases/{caseId}/sections/identity/commit",
            new { baseRevision = 1, values = new Dictionary<string, string> { ["field"] = "updated" } },
            Guid.NewGuid().ToString()));

        // Download must be refused with 409
        var downloadResp = await client.GetAsync($"/v1/cases/{caseId}/packages/pkg-{caseId}/download");
        Assert.Equal(HttpStatusCode.Conflict, downloadResp.StatusCode);
        var body = await ReadJson(downloadResp);
        Assert.Equal("urn:lapluma:problem:approval-invalidated", body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task PubSub_workflow_event_deduplication_ignores_duplicates_idempotently()
    {
        var client = CreateClient();
        var eventId = $"evt-{Guid.NewGuid():N}";
        var caseId = "case-fixture-queue-0001";

        var payload = new
        {
            eventId = eventId,
            eventType = "DOCUMENT_EXTRACTED",
            caseId = caseId,
            occurredAt = DateTimeOffset.UtcNow,
            correlationId = "corr-12345"
        };

        // First delivery: PROCESSED
        var resp1 = await client.SendAsync(Request(
            HttpMethod.Post, "/v1/events/workflow", payload, Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        var json1 = await ReadJson(resp1);
        Assert.Equal(eventId, json1.GetProperty("eventId").GetString());
        Assert.Equal("PROCESSED", json1.GetProperty("status").GetString());

        // Duplicate delivery with same eventId: DUPLICATE_IGNORED
        var resp2 = await client.SendAsync(Request(
            HttpMethod.Post, "/v1/events/workflow", payload, Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var json2 = await ReadJson(resp2);
        Assert.Equal(eventId, json2.GetProperty("eventId").GetString());
        Assert.Equal("DUPLICATE_IGNORED", json2.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Out_of_order_pubsub_events_cannot_regress_approved_or_generated_state()
    {
        var client = CreateClient();
        var caseId = $"case-regression-{Guid.NewGuid():N}";

        // Move to GENERATED first
        var compileEvt = new
        {
            eventId = $"evt-compile-{Guid.NewGuid():N}",
            eventType = "PACKAGE_COMPILED",
            caseId = caseId,
            occurredAt = DateTimeOffset.UtcNow,
            correlationId = "corr-compile"
        };
        var compileResp = await client.SendAsync(Request(
            HttpMethod.Post, "/v1/events/workflow", compileEvt, Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.OK, compileResp.StatusCode);

        // Now an out-of-order event arriving later must be prevented from regressing state
        var eventId = $"evt-regression-{Guid.NewGuid():N}";
        var payload = new
        {
            eventId = eventId,
            eventType = "DOCUMENT_EXTRACTED",
            caseId = caseId,
            occurredAt = DateTimeOffset.UtcNow,
            correlationId = "corr-regression-test"
        };

        var resp = await client.SendAsync(Request(
            HttpMethod.Post, "/v1/events/workflow", payload, Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await ReadJson(resp);
        Assert.Equal("REGRESSION_PREVENTED", json.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Malformed_pubsub_event_payload_returns_400()
    {
        var client = CreateClient();
        var payload = new
        {
            eventId = "",
            eventType = "INVALID",
            caseId = ""
        };

        var resp = await client.SendAsync(Request(
            HttpMethod.Post, "/v1/events/workflow", payload, Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
