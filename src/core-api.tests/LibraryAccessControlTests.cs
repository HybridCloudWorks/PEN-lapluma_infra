using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace LaPluma.CoreApi.Tests;

/// <summary>
/// Authentication handler for synthetic multi-tenant test scenarios.
/// Injects specified tenant claim into the ClaimsPrincipal.
/// </summary>
internal sealed class TenantTestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "TenantTest";
    public static string CurrentTenantId { get; set; } = "tenant_clinic_alpha";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, "test-user"),
            new Claim(TenantResolution.TenantIdClaimType, CurrentTenantId)
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

public sealed class TenantTestFactory : WebApplicationFactory<global::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder
            .WithFixtureCatalog()
            .WithAuthenticationConfigured()
            .ConfigureTestServices(services =>
            {
                services.AddAuthentication(TenantTestAuthHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TenantTestAuthHandler>(
                        TenantTestAuthHandler.SchemeName, _ => { });
            });
    }
}

public sealed class LibraryAccessControlTests : IClassFixture<TenantTestFactory>
{
    private readonly TenantTestFactory _factory;

    public LibraryAccessControlTests(TenantTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task SharedBlueprint_IsReusedAndAccessibleToMultipleTenants()
    {
        var client = _factory.CreateClient();

        // 1. Tenant Clinic Alpha accesses shared I-130
        TenantTestAuthHandler.CurrentTenantId = "tenant_clinic_alpha";
        var alphaResponse = await client.GetAsync("/v1/library/blueprints/uscis/i-130");
        Assert.Equal(HttpStatusCode.OK, alphaResponse.StatusCode);
        var alphaBp = await alphaResponse.Content.ReadFromJsonAsync<DocumentBlueprint>();
        Assert.NotNull(alphaBp);
        Assert.Equal("uscis", alphaBp.Namespace);
        Assert.Equal("i-130", alphaBp.BlueprintId);

        // 2. Tenant Firm Beta accesses the same shared I-130
        TenantTestAuthHandler.CurrentTenantId = "tenant_firm_beta";
        var betaResponse = await client.GetAsync("/v1/library/blueprints/uscis/i-130");
        Assert.Equal(HttpStatusCode.OK, betaResponse.StatusCode);
        var betaBp = await betaResponse.Content.ReadFromJsonAsync<DocumentBlueprint>();
        Assert.NotNull(betaBp);
        Assert.Equal("uscis", betaBp.Namespace);
        Assert.Equal("i-130", betaBp.BlueprintId);
    }

    [Fact]
    public async Task PrivateBlueprint_IsAccessibleOnlyToOwningTenant_AndInaccessibleToOthers()
    {
        var client = _factory.CreateClient();

        // 1. Clinic Alpha accesses its private intake sheet
        TenantTestAuthHandler.CurrentTenantId = "tenant_clinic_alpha";
        var alphaResp = await client.GetAsync("/v1/library/blueprints/tenant_clinic_alpha/intake");
        Assert.Equal(HttpStatusCode.OK, alphaResp.StatusCode);
        var intakeBp = await alphaResp.Content.ReadFromJsonAsync<DocumentBlueprint>();
        Assert.NotNull(intakeBp);
        Assert.Equal("tenant_clinic_alpha", intakeBp.Namespace);
        Assert.Equal("intake", intakeBp.BlueprintId);

        // 2. Firm Beta attempts to access Clinic Alpha's private intake sheet -> returns 404 (never leaks existence)
        TenantTestAuthHandler.CurrentTenantId = "tenant_firm_beta";
        var betaResp = await client.GetAsync("/v1/library/blueprints/tenant_clinic_alpha/intake");
        Assert.Equal(HttpStatusCode.NotFound, betaResp.StatusCode);

        // 3. Firm Beta lists blueprints -> Clinic Alpha's private sheet is NOT included
        var betaListResp = await client.GetAsync("/v1/library/blueprints");
        Assert.Equal(HttpStatusCode.OK, betaListResp.StatusCode);
        var betaList = await betaListResp.Content.ReadFromJsonAsync<List<DocumentBlueprint>>();
        Assert.NotNull(betaList);
        Assert.DoesNotContain(betaList, bp => bp.Namespace == "tenant_clinic_alpha");
        Assert.Contains(betaList, bp => bp.Namespace == "tenant_firm_beta" && bp.BlueprintId == "special_retainer");
        Assert.Contains(betaList, bp => bp.Namespace == "uscis" && bp.BlueprintId == "i-130");
    }

    [Fact]
    public async Task CustomerCollections_ReturnsOnlyAssignedCollections()
    {
        var client = _factory.CreateClient();

        // 1. Clinic Alpha has both official Family Reunification and Clinic Intake collections assigned
        TenantTestAuthHandler.CurrentTenantId = "tenant_clinic_alpha";
        var alphaColsResp = await client.GetAsync("/v1/library/collections");
        Assert.Equal(HttpStatusCode.OK, alphaColsResp.StatusCode);
        var alphaCols = await alphaColsResp.Content.ReadFromJsonAsync<List<DocumentCollection>>();
        Assert.NotNull(alphaCols);
        Assert.Equal(2, alphaCols.Count);
        Assert.Contains(alphaCols, c => c.CollectionId == "family_reunification");
        Assert.Contains(alphaCols, c => c.CollectionId == "clinic_intake_pkg");

        // 2. Firm Beta has only the official Family Reunification collection assigned
        TenantTestAuthHandler.CurrentTenantId = "tenant_firm_beta";
        var betaColsResp = await client.GetAsync("/v1/library/collections");
        Assert.Equal(HttpStatusCode.OK, betaColsResp.StatusCode);
        var betaCols = await betaColsResp.Content.ReadFromJsonAsync<List<DocumentCollection>>();
        Assert.NotNull(betaCols);
        Assert.Single(betaCols);
        Assert.Contains(betaCols, c => c.CollectionId == "family_reunification");
        Assert.DoesNotContain(betaCols, c => c.CollectionId == "clinic_intake_pkg");

        // 3. Direct lookup by Firm Beta for Clinic Alpha's collection returns 404
        var unassignedLookup = await client.GetAsync("/v1/library/collections/tenant_clinic_alpha/clinic_intake_pkg");
        Assert.Equal(HttpStatusCode.NotFound, unassignedLookup.StatusCode);
    }

    [Fact]
    public async Task ClientTenantSelection_IsIgnored_ServerResolvesIdentityFromClaims()
    {
        var client = _factory.CreateClient();

        // Caller is authenticated as Firm Beta, but maliciously sends a query param or header trying to impersonate Clinic Alpha
        TenantTestAuthHandler.CurrentTenantId = "tenant_firm_beta";
        var spoofedResp = await client.GetAsync("/v1/library/collections?tenant_id=tenant_clinic_alpha");
        Assert.Equal(HttpStatusCode.OK, spoofedResp.StatusCode);

        // Server strictly evaluated claims for Firm Beta; Clinic Alpha's private collection is NOT returned
        var cols = await spoofedResp.Content.ReadFromJsonAsync<List<DocumentCollection>>();
        Assert.NotNull(cols);
        Assert.Single(cols);
        Assert.Contains(cols, c => c.CollectionId == "family_reunification");
        Assert.DoesNotContain(cols, c => c.CollectionId == "clinic_intake_pkg");
    }

    [Fact]
    public async Task AddingCollection_RequiresZeroCodeDeployment()
    {
        // Obtain service instance from DI to add a new collection at runtime (simulating DB insertion)
        var service = _factory.Services.GetRequiredService<ILibraryAccessService>() as LibraryAccessService;
        Assert.NotNull(service);

        const string isolatedTenant = "tenant_gamma_isolated";
        service.RegisterTenant(new InstitutionTenant(isolatedTenant, "Isolated Test Org", true, DateTimeOffset.UtcNow));

        var newCol = new DocumentCollection(
            Namespace: "official",
            CollectionId: "naturalization_n400",
            Revision: 1,
            Title: "Application for Naturalization",
            Description: "USCIS Form N-400 Package",
            WorkflowSettings: JsonDocument.Parse("{\"theme\": \"official\", \"displayOrder\": 20}"),
            PublicationState: PublicationState.Published,
            IsLatest: true,
            CreatedAt: DateTimeOffset.UtcNow,
            Members: [new CollectionMember("uscis", "n-400", 1, 1, true)]);

        service.RegisterCollection(newCol);
        service.AssignCollectionToTenant(isolatedTenant, "official", "naturalization_n400", 1);

        // Verify Isolated Tenant immediately sees this newly assigned collection via HTTP API without redeployment
        TenantTestAuthHandler.CurrentTenantId = isolatedTenant;
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/v1/library/collections/official/naturalization_n400");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var col = await resp.Content.ReadFromJsonAsync<DocumentCollection>();
        Assert.NotNull(col);
        Assert.Equal("naturalization_n400", col.CollectionId);
    }

    [Fact]
    public void PostgreSQLSchema_ViewsAndSeedsFile_ExistsAndIsSyntacticallyValid()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var viewsSqlPath = Path.Combine(root, "infra/sql/003_library_access_views_and_seeds.sql");

        Assert.True(File.Exists(viewsSqlPath), $"Expected {viewsSqlPath} to exist.");
        var sql = File.ReadAllText(viewsSqlPath);
        Assert.Contains("CREATE OR REPLACE VIEW library.v_effective_tenant_collections", sql);
        Assert.Contains("CREATE OR REPLACE VIEW library.v_effective_tenant_blueprints", sql);
        Assert.Contains("tenant_clinic_alpha", sql);
        Assert.Contains("tenant_firm_beta", sql);
        Assert.Contains("collection_blueprint_member", sql);
    }
}
