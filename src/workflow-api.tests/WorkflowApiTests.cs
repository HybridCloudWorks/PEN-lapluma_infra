using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LaPluma.WorkflowApi.Tests;

/// <summary>
/// End-to-end tests over the real request pipeline, so serialization, routing, parameter binding,
/// and status codes are all exercised rather than the handler bodies alone.
/// </summary>
public sealed class WorkflowApiTests : IClassFixture<AuthenticatedFactory>
{
    private readonly AuthenticatedFactory factory;

    public WorkflowApiTests(AuthenticatedFactory factory) => this.factory = factory;

    private HttpClient Client() => factory.CreateClient();

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, object? body, string key)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    [Theory]
    [InlineData("/health", "ok")]
    [InlineData("/ready", "ready")]
    public async Task Probes_report_their_status(string path, string expectedStatus)
    {
        var response = await Client().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJson(response);
        Assert.Equal(expectedStatus, body.GetProperty("status").GetString());
        Assert.Equal("workflow-api", body.GetProperty("service").GetString());
    }

    [Fact]
    public async Task The_session_reports_the_authenticated_caller_with_the_contract_wire_names()
    {
        var response = await Client().GetAsync("/v1/session");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJson(response);
        // The Swift Codable decoder matches property names exactly: userID, not userId. This test
        // pins the serialization policy output to the app's wire names.
        Assert.Equal("test-caller", body.GetProperty("userID").GetString());
        Assert.Equal("FIXTURE-DEMO", body.GetProperty("workspaceCode").GetString());
        Assert.True(body.GetProperty("isDemo").GetBoolean());
        Assert.Contains(
            "WORKFORCE",
            body.GetProperty("personas").EnumerateArray().Select(element => element.GetString()));
        Assert.Contains(
            "viewClientDirectory",
            body.GetProperty("capabilities").EnumerateArray().Select(element => element.GetString()));
    }

    [Fact]
    public async Task The_client_directory_returns_a_page_with_the_seed_client()
    {
        var response = await Client().GetAsync("/v1/clients");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJson(response);
        var items = body.GetProperty("items").EnumerateArray().ToArray();
        Assert.NotEmpty(items);
        var seed = items.Single(item =>
            item.GetProperty("id").GetString() == "folder-fixture-0001");
        Assert.Equal("Fixture Client One", seed.GetProperty("displayLabel").GetString());
        // The primary case carries the app's CaseSummary wire names, folderID included.
        var primaryCase = seed.GetProperty("primaryCase");
        Assert.Equal("folder-fixture-0001", primaryCase.GetProperty("folderID").GetString());
        Assert.Equal("COLLECTING", primaryCase.GetProperty("state").GetString());
        // Mechanical counters only — a percentage-like field anywhere here is a contract breach.
        var counters = primaryCase.GetProperty("counters");
        Assert.True(counters.TryGetProperty("fieldsFilled", out _));
        Assert.DoesNotContain(
            "percent",
            (await response.Content.ReadAsStringAsync()).ToLowerInvariant());
    }

    [Fact]
    public async Task An_overlong_directory_query_is_rejected_not_truncated()
    {
        var response = await Client().GetAsync($"/v1/clients?query={new string('a', 300)}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "urn:lapluma:problem:clients-query-invalid",
            (await ReadJson(response)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Creating_a_client_is_idempotent_by_key()
    {
        var client = Client();
        var key = Guid.NewGuid().ToString();

        var first = await client.SendAsync(Request(
            HttpMethod.Post, "/v1/clients", new { displayLabel = "Fixture Client Repeat" }, key));
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        var createdId = (await ReadJson(first)).GetProperty("id").GetString();

        // The retry contract: the same key with the same payload returns the original result
        // rather than creating a sibling. The client's offline mutation queue depends on this.
        var replay = await client.SendAsync(Request(
            HttpMethod.Post, "/v1/clients", new { displayLabel = "Fixture Client Repeat" }, key));
        Assert.Equal(HttpStatusCode.Created, replay.StatusCode);
        Assert.Equal(createdId, (await ReadJson(replay)).GetProperty("id").GetString());

        var conflict = await client.SendAsync(Request(
            HttpMethod.Post, "/v1/clients", new { displayLabel = "A Different Label" }, key));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(
            "urn:lapluma:problem:idempotency-key-conflict",
            (await ReadJson(conflict)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_missing_idempotency_key_is_a_diagnosable_400()
    {
        var response = await Client().PostAsJsonAsync(
            "/v1/clients", new { displayLabel = "No Key" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "urn:lapluma:problem:idempotency-key-required",
            (await ReadJson(response)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task An_oversize_idempotency_key_is_rejected()
    {
        var response = await Client().SendAsync(Request(
            HttpMethod.Post, "/v1/clients", new { displayLabel = "Long Key" }, new string('k', 129)));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(
            "urn:lapluma:problem:idempotency-key-required",
            (await ReadJson(response)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task The_case_workspace_returns_the_fixture_case()
    {
        var response = await Client().GetAsync("/v1/cases/case-fixture-0001/workspace");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJson(response);
        Assert.Equal(
            "case-fixture-0001", body.GetProperty("summary").GetProperty("id").GetString());
        Assert.Equal(
            "user-fixture-preparer",
            body.GetProperty("assignments").GetProperty("preparerID").GetString());
        Assert.Empty(body.GetProperty("sections").EnumerateArray());
    }

    [Fact]
    public async Task A_missing_and_an_unauthorized_case_are_the_same_404()
    {
        var response = await Client().GetAsync("/v1/cases/case-not-ours/workspace");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await ReadJson(response);
        Assert.Equal("urn:lapluma:problem:not-found", body.GetProperty("type").GetString());
        Assert.NotEqual(Guid.Empty, body.GetProperty("correlationId").GetGuid());
    }

    private sealed class EmptyDirectorySource : IWorkflowSource
    {
        public Task<AuthenticatedContext> GetSessionContextAsync(
            string userId, CancellationToken cancellationToken) =>
            Task.FromResult(new AuthenticatedContext(userId, "EMPTY", [], [], [], true));

        public Task<ClientDirectoryPage> ListClientsAsync(
            string? query, string? cursor, CancellationToken cancellationToken) =>
            Task.FromResult(new ClientDirectoryPage([], null));

        public Task<CreateClientOutcome> CreateClientAsync(
            string idempotencyKey, CreateClientRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CaseWorkspace?> GetCaseWorkspaceAsync(
            string caseId, CancellationToken cancellationToken) =>
            Task.FromResult<CaseWorkspace?>(null);
    }

    private sealed class EmptyDirectoryFactory : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
        {
            builder.WithFixtureWorkflow().WithAuthenticationConfigured().WithTestAuthentication();
            builder.ConfigureServices(services =>
                services.AddSingleton<IWorkflowSource>(new EmptyDirectorySource()));
        }
    }

    [Fact]
    public async Task An_empty_client_directory_is_still_ready()
    {
        // A durable store with zero clients is a freshly provisioned environment, not a failed
        // replica. Readiness proves the source resolves and answers — never that data exists —
        // so an empty page must report ready rather than pull the replica out of rotation.
        using var factory = new EmptyDirectoryFactory();

        var response = await factory.CreateClient().GetAsync("/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Unbuilt_contract_operations_answer_501_not_404()
    {
        // Mapped explicitly so "not built yet" is distinguishable from "wrong URL".
        var response = await Client().GetAsync("/v1/review-queue");

        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        Assert.Equal(
            "urn:lapluma:problem:not-implemented",
            (await ReadJson(response)).GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("/relay/" + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    [InlineData("/v1/relay/0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef")]
    public async Task The_anonymous_relay_surface_does_not_exist_yet(string path)
    {
        // The public relay endpoints are deliberately unmapped — not even a 501 — until their own
        // security review. An unauthenticated GET must find nothing.
        using var anonymousClient = factory.CreateClient();

        var response = await anonymousClient.GetAsync(path);

        Assert.True(
            response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized,
            $"expected an absent surface, got {(int)response.StatusCode}");
    }
}
