using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace LaPluma.CoreApi.Tests;

/// <summary>
/// End-to-end tests over the real request pipeline, so serialization, routing, parameter binding,
/// and status codes are all exercised rather than the handler bodies alone.
/// </summary>
public sealed class CatalogApiTests : IClassFixture<WebApplicationFactory<global::Program>>
{
    private readonly WebApplicationFactory<global::Program> factory;

    public CatalogApiTests(WebApplicationFactory<global::Program> factory) => this.factory = factory;

    private HttpClient Client() => factory.CreateClient();

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Theory]
    [InlineData("/health", "ok")]
    [InlineData("/ready", "ready")]
    public async Task Probes_report_their_status(string path, string expectedStatus)
    {
        var response = await Client().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJson(response);
        Assert.Equal(expectedStatus, body.GetProperty("status").GetString());
        Assert.Equal("core-api", body.GetProperty("service").GetString());
    }

    [Fact]
    public async Task Categories_return_the_full_hierarchy()
    {
        var response = await Client().GetAsync("/v1/catalog/categories");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var data = (await ReadJson(response)).GetProperty("data");
        var codes = data.EnumerateArray()
            .Select(node => node.GetProperty("category").GetProperty("code").GetString())
            .ToArray();
        Assert.Equal(new[] { "FEDERAL", "EDUCATION" }, codes);

        var federalSubcategories = data.EnumerateArray().First()
            .GetProperty("subcategories").EnumerateArray()
            .Select(sub => sub.GetProperty("code").GetString())
            .ToArray();
        Assert.Equal(new[] { "IMMIGRATION", "PASSPORT" }, federalSubcategories);
    }

    [Fact]
    public async Task Packages_return_the_alpha_priority_set_in_catalog_order()
    {
        var response = await Client().GetAsync("/v1/catalog/packages");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var codes = PackageCodes(await ReadJson(response));
        Assert.Equal(
            new[] { "ADJUSTMENT_I485_I864", "FAMILY_I130", "PASSPORT_DS11", "FINANCIAL_AID_FAFSA" },
            codes);
    }

    [Theory]
    [InlineData("categoryCode=EDUCATION", new[] { "FINANCIAL_AID_FAFSA" })]
    [InlineData("subcategoryCode=PASSPORT", new[] { "PASSPORT_DS11" })]
    [InlineData("categoryCode=FEDERAL&subcategoryCode=IMMIGRATION",
        new[] { "ADJUSTMENT_I485_I864", "FAMILY_I130" })]
    public async Task Packages_filter_by_taxonomy(string query, string[] expected)
    {
        var response = await Client().GetAsync($"/v1/catalog/packages?{query}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, PackageCodes(await ReadJson(response)));
    }

    [Fact]
    public async Task Packages_filter_by_derived_activation_state()
    {
        // Package activation is derived from the weakest child form, never stored. FAFSA's only
        // form is UNAVAILABLE, so its package is too; the other three are CATALOG_ONLY.
        var catalogOnly = await Client().GetAsync("/v1/catalog/packages?activationState=CATALOG_ONLY");
        Assert.Equal(
            new[] { "ADJUSTMENT_I485_I864", "FAMILY_I130", "PASSPORT_DS11" },
            PackageCodes(await ReadJson(catalogOnly)));

        var unavailable = await Client().GetAsync("/v1/catalog/packages?activationState=UNAVAILABLE");
        Assert.Equal(new[] { "FINANCIAL_AID_FAFSA" }, PackageCodes(await ReadJson(unavailable)));

        var pilot = await Client().GetAsync("/v1/catalog/packages?activationState=PILOT");
        Assert.Empty((await ReadJson(pilot)).GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task An_omitted_activation_state_is_not_a_parse_failure()
    {
        // TryParseActivationState returns true for null. Without this the whole list 400s.
        var response = await Client().GetAsync("/v1/catalog/packages");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(4, (await ReadJson(response)).GetProperty("data").GetArrayLength());
    }

    [Theory]
    [InlineData("catalog_only")]
    [InlineData("CatalogOnly")]
    [InlineData("NOT_A_STATE")]
    [InlineData("")]
    public async Task An_unrecognised_activation_state_is_rejected(string value)
    {
        var response = await Client().GetAsync($"/v1/catalog/packages?activationState={value}");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await ReadJson(response);
        Assert.Equal("urn:lapluma:problem:catalog-activation-invalid", body.GetProperty("type").GetString());
        Assert.Equal(400, body.GetProperty("status").GetInt32());
        Assert.NotEqual(Guid.Empty, body.GetProperty("correlationId").GetGuid());
    }

    [Fact]
    public async Task A_known_package_returns_its_detail()
    {
        var response = await Client().GetAsync("/v1/catalog/packages/FAMILY_I130");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await ReadJson(response);
        Assert.Equal("FAMILY_I130", body.GetProperty("packageCode").GetString());
        Assert.Equal(
            new[] { "I-130", "I-130A" },
            body.GetProperty("forms").EnumerateArray()
                .Select(form => form.GetProperty("formNumber").GetString()).ToArray());

        // Package activation is derived, so it must not appear on the wire — the iOS client
        // computes it from the child forms.
        Assert.False(body.TryGetProperty("activationState", out _));
    }

    [Fact]
    public async Task An_unknown_package_returns_a_problem_document()
    {
        var response = await Client().GetAsync("/v1/catalog/packages/NO_SUCH_PACKAGE");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await ReadJson(response);
        Assert.Equal("urn:lapluma:problem:catalog-package-not-found", body.GetProperty("type").GetString());
        Assert.Equal(404, body.GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Schema_lookup_fails_closed_until_an_edition_is_approved()
    {
        // No schema is activated until acquisition, immutable edition metadata, and the two-person
        // field-map approval gate exist. 404 is the contract, not a gap.
        var response = await Client().GetAsync(
            "/v1/catalog/authorities/USCIS/forms/I-130/editions/2024-04-01/schemas/v1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var body = await ReadJson(response);
        Assert.Equal("urn:lapluma:problem:catalog-schema-not-found", body.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Enum_values_are_serialised_in_the_contract_wire_format()
    {
        // Guards the SCREAMING_SNAKE_CASE wire names the iOS client decodes. A new enum added
        // without its [JsonStringEnumMemberName] attributes would serialise as PascalCase.
        var response = await Client().GetAsync("/v1/catalog/packages/FINANCIAL_AID_FAFSA");

        var form = (await ReadJson(response)).GetProperty("forms").EnumerateArray().First();
        Assert.Equal("EXTERNAL_WORKFLOW", form.GetProperty("artifactKind").GetString());
        Assert.Equal("REFERENCE_ONLY", form.GetProperty("fillCapability").GetString());
        Assert.Equal("UNAVAILABLE", form.GetProperty("activationState").GetString());
        Assert.Equal("FLAT", form.GetProperty("encoding").GetString());
    }

    [Fact]
    public async Task No_catalog_endpoint_accepts_a_person_or_case_parameter()
    {
        // The catalog is deliberately anonymous: it must not become a place where an applicant
        // identifier can be passed. These are ignored today, and must never become meaningful.
        var response = await Client().GetAsync(
            "/v1/catalog/packages?personId=p-1&caseId=c-1&eligibility=asylum");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(4, (await ReadJson(response)).GetProperty("data").GetArrayLength());
    }

    private static string[] PackageCodes(JsonElement body) =>
        body.GetProperty("data").EnumerateArray()
            .Select(package => package.GetProperty("packageCode").GetString()!)
            .ToArray();
}
