using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace LaPluma.CoreApi.Tests;

/// <summary>
/// The catalog surface is not anonymous, and the probes are.
///
/// The design terminates JWT validation at the API Management edge. These tests exist because a
/// service whose only protection is an upstream gateway fails open the moment anything reaches it
/// directly, and inside the core subnet plenty can.
/// </summary>
public sealed class CatalogAuthorizationTests
{
    // Configured, but with no authentication scheme substituted: this is a real request arriving
    // with no credentials, which is what a caller bypassing the edge looks like.
    private sealed class ConfiguredFactory : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.WithFixtureCatalog().WithAuthenticationConfigured();
    }

    // Authenticated, but with no audience or issuer configured. Nothing can validate a token, so
    // the deployment must deny rather than accept.
    private sealed class UnconfiguredButAuthenticatedFactory : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.WithFixtureCatalog().WithTestAuthentication();
    }

    // No authentication configured and no scheme substituted, but the fixture selected so the host
    // starts: this isolates token validation from catalog wiring.
    private sealed class FixtureOnlyFactory : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) => builder.WithFixtureCatalog();
    }

    public static TheoryData<string> CatalogRoutes() =>
    [
        "/v1/catalog/categories",
        "/v1/catalog/packages",
        "/v1/catalog/packages/FAFSA_STANDARD",
        "/v1/catalog/authorities/US-ED/forms/FAFSA/editions/2025-01-01/schemas/1.0.0",
    ];

    [Theory]
    [MemberData(nameof(CatalogRoutes))]
    public async Task Every_catalog_route_rejects_an_unauthenticated_request(string route)
    {
        using var factory = new ConfiguredFactory();

        var response = await factory.CreateClient().GetAsync(route);

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

        var response = await client.GetAsync("/v1/catalog/packages");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_unconfigured_deployment_denies_even_an_authenticated_caller()
    {
        // The fail-closed case, and the reason the policy carries an explicit deny rather than
        // relying on token validation alone. With no audience and issuer there is nothing to
        // validate against; a deployment in that state must serve nothing rather than everything.
        using var factory = new UnconfiguredButAuthenticatedFactory();

        var response = await factory.CreateClient().GetAsync("/v1/catalog/packages");

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

        var response = await factory.CreateClient().GetAsync("/v1/catalog/packages");

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
            .GetAsync("/v1/catalog/packages/DISTINCTIVE_MARKER?activationState=ANOTHER_MARKER");

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("DISTINCTIVE_MARKER", body, StringComparison.Ordinal);
        Assert.DoesNotContain("ANOTHER_MARKER", body, StringComparison.Ordinal);
    }
}
