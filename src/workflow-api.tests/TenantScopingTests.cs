using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Xunit;

namespace LaPluma.WorkflowApi.Tests;

/// <summary>
/// INF-26 at the HTTP surface: the tenant comes from the token's <c>tenant_id</c> claim only, a
/// caller without one is refused before any handler runs, and what one tenant creates another
/// never sees. The same guarantees against PostgreSQL are in <see cref="PostgresTenantIsolationTests"/>.
/// </summary>
public sealed class TenantScopingTests : IClassFixture<AuthenticatedFactory>
{
    private const string TenantA = "tenant_iso_alpha";
    private const string TenantB = "tenant_iso_beta";

    private readonly AuthenticatedFactory factory;

    public TenantScopingTests(AuthenticatedFactory factory) => this.factory = factory;

    private HttpClient ClientFor(string tenant)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.TenantHeader, tenant);
        return client;
    }

    private static HttpRequestMessage CreateClient(string label, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/clients")
        {
            Content = JsonContent.Create(new { displayLabel = label }),
        };
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private static async Task<string[]> Labels(HttpClient client)
    {
        var page = await client.GetFromJsonAsync<JsonElement>("/v1/clients");
        return page.GetProperty("items").EnumerateArray()
            .Select(entry => entry.GetProperty("displayLabel").GetString()!)
            .ToArray();
    }

    [Theory]
    [InlineData("/v1/session")]
    [InlineData("/v1/clients")]
    [InlineData("/v1/review-queue")]
    [InlineData("/v1/cases/case-fixture-0001/workspace")]
    public async Task A_caller_whose_token_has_no_tenant_is_refused(string path)
    {
        var response = await ClientFor(TestAuthHandler.NoTenant).GetAsync(path);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("Tenant_Upper")]
    [InlineData("x")]
    [InlineData("tenant with spaces")]
    [InlineData("tenant;drop")]
    public async Task A_malformed_tenant_claim_is_refused(string tenant)
    {
        var response = await ClientFor(tenant).GetAsync("/v1/clients");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_client_created_in_one_tenant_is_invisible_to_another()
    {
        var label = $"Isolation Probe {Guid.NewGuid():N}";
        var created = await ClientFor(TenantA).SendAsync(CreateClient(label, Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);

        Assert.Contains(label, await Labels(ClientFor(TenantA)));
        Assert.DoesNotContain(label, await Labels(ClientFor(TenantB)));
    }

    [Fact]
    public async Task The_same_idempotency_key_in_another_tenant_does_not_replay_the_first_tenants_client()
    {
        var key = Guid.NewGuid().ToString();
        var first = await ClientFor(TenantA).SendAsync(CreateClient("Alpha Private", key));
        var second = await ClientFor(TenantB).SendAsync(CreateClient("Beta Private", key));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        var alpha = await first.Content.ReadFromJsonAsync<JsonElement>();
        var beta = await second.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(alpha.GetProperty("id").GetString(), beta.GetProperty("id").GetString());
        Assert.Equal("Beta Private", beta.GetProperty("displayLabel").GetString());
    }

    [Theory]
    [InlineData("tenant_hybridcloudworks", true)]
    [InlineData("tenant-clinic-01", true)]
    [InlineData("", false)]
    [InlineData("A", false)]
    [InlineData("tenant/../other", false)]
    public void Only_a_single_well_formed_tenant_id_claim_resolves(string value, bool resolves)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(WorkflowTenant.ClaimType, value)], "Test"));

        Assert.Equal(resolves, WorkflowTenant.TryResolve(principal, out var tenant));
        Assert.Equal(resolves ? value : null, tenant);
    }

    [Fact]
    public void Two_tenant_claims_are_ambiguous_and_resolve_to_nothing()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(WorkflowTenant.ClaimType, TenantA), new Claim(WorkflowTenant.ClaimType, TenantB)], "Test"));

        Assert.False(WorkflowTenant.TryResolve(principal, out _));
    }

    [Fact]
    public void Neither_tid_nor_the_subject_stands_in_for_tenant_id()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("tid", TenantA), new Claim(ClaimTypes.NameIdentifier, TenantB)], "Test"));

        Assert.False(WorkflowTenant.TryResolve(principal, out _));
    }

    [Fact]
    public void An_unauthenticated_principal_has_no_tenant()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(WorkflowTenant.ClaimType, TenantA)]));

        Assert.False(WorkflowTenant.TryResolve(principal, out _));
    }
}
