using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace LaPluma.WorkflowApi.Tests;

/// <summary>
/// The workflow surface is not anonymous, and the probes are. Unlike the catalog, everything under
/// /v1 here carries case content, so the fail-closed posture is the whole product: a caller that
/// bypasses the API Management edge must find a second lock, not an open door.
/// </summary>
public sealed class WorkflowAuthorizationTests
{
    // Configured, but with no authentication scheme substituted: this is a real request arriving
    // with no credentials, which is what a caller bypassing the edge looks like.
    private sealed class ConfiguredFactory : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.WithFixtureWorkflow().WithAuthenticationConfigured();
    }

    // Authenticated, but with no audience or issuer configured. Nothing can validate a token, so
    // the deployment must deny rather than accept.
    private sealed class UnconfiguredButAuthenticatedFactory : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.WithFixtureWorkflow().WithTestAuthentication();
    }

    // No authentication configured and no scheme substituted, but the fixture selected so the
    // host starts: this isolates token validation from workflow wiring.
    private sealed class FixtureOnlyFactory : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.WithFixtureWorkflow();
    }

    public static TheoryData<string> WorkflowRoutes() =>
    [
        "/v1/session",
        "/v1/clients",
        "/v1/cases/case-fixture-0001/workspace",
        "/v1/review-queue",
        "/v1/admin/members",
    ];

    [Theory]
    [MemberData(nameof(WorkflowRoutes))]
    public async Task Every_workflow_route_rejects_an_unauthenticated_request(string route)
    {
        using var factory = new ConfiguredFactory();

        var response = await factory.CreateClient().GetAsync(route);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Even_a_stub_endpoint_sits_behind_the_lock()
    {
        // A 501 must not be reachable anonymously: the stub map is part of the authorized group,
        // and an unauthenticated caller learns nothing about which operations exist.
        using var factory = new ConfiguredFactory();

        var response = await factory.CreateClient().PostAsync("/v1/admin/demo/enter", null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task A_garbage_bearer_token_is_rejected()
    {
        // Asserts the token is actually validated rather than merely present. The factory here is
        // unconfigured on purpose, so the handler has no authority to fetch signing keys from and
        // the test makes no network call.
        using var factory = new FixtureOnlyFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "not.a.real.token");

        var response = await client.GetAsync("/v1/clients");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_unconfigured_deployment_denies_even_an_authenticated_caller()
    {
        // The fail-closed case, and the reason the policy carries an explicit deny rather than
        // relying on token validation alone. With no audience and issuer there is nothing to
        // validate against; a deployment in that state must serve nothing rather than everything.
        using var factory = new UnconfiguredButAuthenticatedFactory();

        var response = await factory.CreateClient().GetAsync("/v1/clients");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/ready")]
    public async Task The_probes_stay_anonymous(string route)
    {
        // An orchestrator holds no token. A probe that required one would report the identity
        // provider's health rather than this service's, and would take the replica down with it.
        using var factory = new ConfiguredFactory();

        var response = await factory.CreateClient().GetAsync(route);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_rejected_request_returns_the_same_problem_document_as_every_other_failure()
    {
        using var factory = new ConfiguredFactory();

        var response = await factory.CreateClient().GetAsync("/v1/clients");

        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.NotEqual(Guid.Empty, document.RootElement.GetProperty("correlationId").GetGuid());
    }

    [Fact]
    public async Task The_rejection_body_carries_no_route_or_query_content()
    {
        // The content-free constraint applies to the failure path too, and an authorization
        // rejection is the one most likely to be logged and forwarded somewhere.
        using var factory = new ConfiguredFactory();

        var response = await factory.CreateClient()
            .GetAsync("/v1/cases/DISTINCTIVE_MARKER/workspace?probe=ANOTHER_MARKER");

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("DISTINCTIVE_MARKER", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ANOTHER_MARKER", body, StringComparison.Ordinal);
    }
}
