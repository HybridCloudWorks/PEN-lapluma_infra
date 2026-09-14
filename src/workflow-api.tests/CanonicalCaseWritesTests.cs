using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace LaPluma.WorkflowApi.Tests;

public sealed class CanonicalCaseWritesTests : IClassFixture<AuthenticatedFactory>
{
    private readonly AuthenticatedFactory factory;

    public CanonicalCaseWritesTests(AuthenticatedFactory factory) => this.factory = factory;

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
    public async Task CommitSection_ValidRevision_SucceedsAndIncrementsRevision()
    {
        var client = Client();
        var caseId = WorkflowFixtureSource.FixtureReviewCaseId;

        var request = new CommitSectionRequest(1, new Dictionary<string, string>
        {
            ["applicant.name.first"] = "Maria",
            ["applicant.name.last"] = "Santos"
        });

        var response = await client.SendAsync(PostWithKey(
            $"/v1/cases/{caseId}/sections/identity/commit",
            request,
            $"key-commit-valid-{Guid.NewGuid():N}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJson(response);

        Assert.True(body.TryGetProperty("section", out var section));
        Assert.Equal("identity", section.GetProperty("id").GetString());
        Assert.Equal(2, section.GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task CommitSection_StaleRevision_Returns412VersionConflict()
    {
        var client = Client();
        var caseId = WorkflowFixtureSource.FixtureReviewCaseId;

        // Base revision 99 is stale (current is 1 or 2)
        var request = new CommitSectionRequest(99, new Dictionary<string, string>
        {
            ["applicant.name.first"] = "StaleName"
        });

        var response = await client.SendAsync(PostWithKey(
            $"/v1/cases/{caseId}/sections/identity/commit",
            request,
            $"key-commit-stale-{Guid.NewGuid():N}"));

        Assert.Equal(HttpStatusCode.PreconditionFailed, response.StatusCode);
        var body = await ReadJson(response);
        Assert.Equal("urn:lapluma:problem:version-conflict", body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task CommitSection_IdempotencyReplay_ReturnsSameResult()
    {
        var client = Client();
        var caseId = WorkflowFixtureSource.FixtureReviewCaseId;
        var key = $"key-idempotent-{Guid.NewGuid():N}";

        // Get current revision first
        var workspaceResp = await client.GetAsync($"/v1/cases/{caseId}/workspace");
        var wsBody = await ReadJson(workspaceResp);
        var currentRev = wsBody.GetProperty("sections").EnumerateArray()
            .FirstOrDefault(s => s.GetProperty("id").GetString() == "identity")
            .GetProperty("revision").GetInt32();

        var request = new CommitSectionRequest(currentRev, new Dictionary<string, string>
        {
            ["applicant.address.city"] = "Austin"
        });

        // First attempt -> Created
        var resp1 = await client.SendAsync(PostWithKey($"/v1/cases/{caseId}/sections/identity/commit", request, key));
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        var body1 = await ReadJson(resp1);
        var resultingRev = body1.GetProperty("section").GetProperty("revision").GetInt32();

        // Second attempt with exact same key and body -> Replayed successfully
        var resp2 = await client.SendAsync(PostWithKey($"/v1/cases/{caseId}/sections/identity/commit", request, key));
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var body2 = await ReadJson(resp2);
        Assert.Equal(resultingRev, body2.GetProperty("section").GetProperty("revision").GetInt32());
    }

    [Fact]
    public async Task CommitSection_IdempotencyConflict_Returns409()
    {
        var client = Client();
        var caseId = WorkflowFixtureSource.FixtureReviewCaseId;
        var key = $"key-conflict-{Guid.NewGuid():N}";

        // Get current revision
        var workspaceResp = await client.GetAsync($"/v1/cases/{caseId}/workspace");
        var wsBody = await ReadJson(workspaceResp);
        var currentRev = wsBody.GetProperty("sections").EnumerateArray()
            .FirstOrDefault(s => s.GetProperty("id").GetString() == "identity")
            .GetProperty("revision").GetInt32();

        var request1 = new CommitSectionRequest(currentRev, new Dictionary<string, string>
        {
            ["applicant.phone"] = "555-1111"
        });
        var resp1 = await client.SendAsync(PostWithKey($"/v1/cases/{caseId}/sections/identity/commit", request1, key));
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);

        // Same key, mutated values -> Conflict 409
        var request2 = new CommitSectionRequest(currentRev, new Dictionary<string, string>
        {
            ["applicant.phone"] = "555-9999"
        });
        var resp2 = await client.SendAsync(PostWithKey($"/v1/cases/{caseId}/sections/identity/commit", request2, key));
        Assert.Equal(HttpStatusCode.Conflict, resp2.StatusCode);
        var body2 = await ReadJson(resp2);
        Assert.Equal("urn:lapluma:problem:idempotency-key-conflict", body2.GetProperty("type").GetString());
    }

    [Fact]
    public async Task CommitSection_IfMatchHeader_OverridesBaseRevision()
    {
        var client = Client();
        var caseId = WorkflowFixtureSource.FixtureReviewCaseId;

        // Get current revision
        var workspaceResp = await client.GetAsync($"/v1/cases/{caseId}/workspace");
        var wsBody = await ReadJson(workspaceResp);
        var currentRev = wsBody.GetProperty("sections").EnumerateArray()
            .FirstOrDefault(s => s.GetProperty("id").GetString() == "identity")
            .GetProperty("revision").GetInt32();

        // Pass stale 0 in body, but correct currentRev in If-Match header
        var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/cases/{caseId}/sections/identity/commit")
        {
            Content = JsonContent.Create(new CommitSectionRequest(0, new Dictionary<string, string>
            {
                ["applicant.name.middle"] = "Elena"
            }))
        };
        request.Headers.Add("Idempotency-Key", $"key-ifmatch-{Guid.NewGuid():N}");
        request.Headers.Add("If-Match", $"\"{currentRev}\"");

        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJson(response);
        Assert.Equal(currentRev + 1, body.GetProperty("section").GetProperty("revision").GetInt32());
    }
}
