using System.Globalization;
using LaPluma.CoreApi;

var builder = WebApplication.CreateBuilder(args);

// Enum wire names are declared per type in CatalogModels.cs with [JsonStringEnumMemberName].
// There is deliberately no global JsonStringEnumConverter: a type-level attribute takes precedence
// over a globally registered converter, so the global one never applied to any existing enum while
// making a new enum added without those attributes look handled — it would quietly serialise in
// PascalCase instead of failing visibly.
// ASP.NET Core's request logging emits the full URL, query string included, at Information — on by
// default. Telemetry here must be content-free, so those two categories are raised to Warning: the
// service logs every rejection itself with a correlation identifier and no request content, which
// is the signal worth keeping. Warnings and errors from both categories still come through.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Routing", LogLevel.Warning);

builder.Services.AddCatalogSource(builder.Configuration);
builder.Services.AddCatalogAuthentication(builder.Configuration);

var app = builder.Build();

// Ordering matters and is not cosmetic. UseStatusCodePages inspects the response on the way out, so
// it only sees what the middleware registered after it produced. Authentication and authorization
// go below it: registered above, their 401 and 403 would travel outward past this and reach the
// client as a bare status code with no body, while every other failure carried a problem document.
// A test asserts the 401 body, which is how that ordering was caught.
//
// Failures raised before a handler runs — an unparseable route value, an unmatched route — return a
// bare status code with no body by default. Give them the same problem document every handled
// failure returns, so a client always has a type and a correlation ID to report.
app.UseStatusCodePages(async context =>
{
    var response = context.HttpContext.Response;
    if (response.HasStarted)
    {
        return;
    }

    var problem = CatalogProblem.Create(
        context.HttpContext, "request-invalid", "Request is invalid", response.StatusCode);
    await response.WriteAsJsonAsync(problem, options: null, contentType: CatalogProblem.ContentType);
});

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () =>
    Results.Ok(new HealthResponse("ok", ServiceMetadata.Name, ServiceMetadata.Version)));
// Readiness resolves the repository rather than answering from a literal. CatalogRepository
// initialises its fixture in a static constructor that throws on an unrecognised form number; if
// that happens, every catalog route returns 500 forever while a literal probe stays green.
app.MapGet("/ready", async (
    IServiceProvider services, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
{
    try
    {
        var source = services.GetRequiredService<ICatalogSource>();
        if ((await source.GetHierarchyAsync(cancellationToken)).Count > 0)
        {
            return Results.Ok(
                new HealthResponse("ready", ServiceMetadata.Name, ServiceMetadata.Version));
        }
    }
    catch (Exception error)
    {
        // Deliberately broad: any failure to construct the catalog means this replica cannot serve
        // its only purpose, whatever the cause, and readiness must say so rather than propagate.
        loggerFactory.CreateLogger(CatalogProblem.LogCategory)
            .LogError(error, "Catalog repository could not be initialised; reporting not ready.");
    }

    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

// Applied to the group, so a route added later inherits it rather than having to remember it.
// /health and /ready are deliberately outside: an orchestrator probing them holds no token, and a
// probe that needs one reports the identity provider's health, not this service's.
var catalog = app.MapGroup("/v1/catalog").RequireAuthorization(CatalogAuthentication.PolicyName);

catalog.MapGet("/categories", async (ICatalogSource catalogSource, CancellationToken cancellationToken) =>
    Results.Ok(new CatalogHierarchyResponse(await catalogSource.GetHierarchyAsync(cancellationToken))));

catalog.MapGet("/packages", async Task<IResult> (
    HttpContext context,
    string? categoryCode,
    string? subcategoryCode,
    string? activationState,
    ICatalogSource catalogSource,
    CancellationToken cancellationToken) =>
{
    // The contract constrains these; enforcing it here is what makes a typo a diagnosable 400
    // rather than a successful empty catalog indistinguishable from a legitimately empty one.
    if (categoryCode is not null && !CatalogPatterns.CatalogCode().IsMatch(categoryCode))
    {
        return CatalogProblem.Result(context, "catalog-code-invalid", "categoryCode is invalid", 400);
    }

    if (subcategoryCode is not null && !CatalogPatterns.CatalogCode().IsMatch(subcategoryCode))
    {
        return CatalogProblem.Result(context, "catalog-code-invalid", "subcategoryCode is invalid", 400);
    }

    if (!TryParseActivationState(activationState, out var parsedActivationState))
    {
        return CatalogProblem.Result(
            context, "catalog-activation-invalid", "Activation state is invalid", 400);
    }

    var packages = await catalogSource.ListPackagesAsync(
        categoryCode, subcategoryCode, parsedActivationState, cancellationToken);
    return Results.Ok(new FormPackageListResponse(packages));
});

catalog.MapGet("/packages/{packageCode}", async Task<IResult> (
    HttpContext context,
    string packageCode,
    ICatalogSource catalogSource,
    CancellationToken cancellationToken) =>
{
    if (!CatalogPatterns.CatalogCode().IsMatch(packageCode))
    {
        return CatalogProblem.Result(context, "catalog-code-invalid", "packageCode is invalid", 400);
    }

    return await catalogSource.GetPackageAsync(packageCode, cancellationToken) is { } package
        ? Results.Ok(package)
        : CatalogProblem.Result(context, "catalog-package-not-found", "Catalog package not found", 404);
});

catalog.MapGet(
    "/authorities/{authority}/forms/{formId}/editions/{editionDate}/schemas/{schemaVersion}",
    // editionDate binds as a string and is parsed here rather than as a DateOnly route value.
    // Framework binding failures produce a text/plain diagnostic in Development and a bare empty
    // 400 in Production, so the error a client sees would depend on the environment.
    async Task<IResult> (
        HttpContext context,
        string authority,
        string formId,
        string editionDate,
        string schemaVersion,
        ICatalogSource catalogSource,
        CancellationToken cancellationToken) =>
{
    if (authority.Length is 0 or > 160)
    {
        return CatalogProblem.Result(context, "catalog-authority-invalid", "authority is invalid", 400);
    }

    if (!CatalogPatterns.FormId().IsMatch(formId))
    {
        return CatalogProblem.Result(context, "catalog-form-id-invalid", "formId is invalid", 400);
    }

    if (!CatalogPatterns.SchemaVersion().IsMatch(schemaVersion))
    {
        return CatalogProblem.Result(
            context, "catalog-schema-version-invalid", "schemaVersion is invalid", 400);
    }

    if (!DateOnly.TryParseExact(
            editionDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None,
            out var parsedEditionDate))
    {
        return CatalogProblem.Result(
            context, "catalog-edition-date-invalid", "editionDate must be an ISO 8601 date", 400);
    }

    return await catalogSource.GetSchemaAsync(
            authority, formId, parsedEditionDate, schemaVersion, cancellationToken) is { } schema
        ? Results.Ok(schema)
        : CatalogProblem.Result(context, "catalog-schema-not-found", "Catalog schema not found", 404);
});

// Document Library surface with strict server-side tenant isolation (INF-18).
// Client-provided tenant identifiers are never trusted; identity is resolved strictly from claims.
var library = app.MapGroup("/v1/library").RequireAuthorization(CatalogAuthentication.PolicyName);

library.MapGet("/collections", async Task<IResult> (
    HttpContext context,
    string? query,
    ILibraryAccessService libraryService,
    CancellationToken cancellationToken) =>
{
    var tenantId = TenantResolution.ResolveTenantId(context.User);
    if (tenantId is null)
    {
        return CatalogProblem.Result(context, "tenant-unauthorized", "Caller tenant identity cannot be established", 403);
    }

    var collections = await libraryService.GetAssignedCollectionsAsync(tenantId, cancellationToken);
    if (!string.IsNullOrWhiteSpace(query))
    {
        var q = query.Trim();
        collections = collections.Where(c =>
            c.CollectionId.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            c.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            (c.Description != null && c.Description.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
    }
    return Results.Ok(collections);
});

library.MapGet("/collections/{namespace}/{collectionId}", async Task<IResult> (
    HttpContext context,
    string @namespace,
    string collectionId,
    int? revision,
    ILibraryAccessService libraryService,
    CancellationToken cancellationToken) =>
{
    var tenantId = TenantResolution.ResolveTenantId(context.User);
    if (tenantId is null)
    {
        return CatalogProblem.Result(context, "tenant-unauthorized", "Caller tenant identity cannot be established", 403);
    }

    var collection = await libraryService.GetCollectionAsync(tenantId, @namespace, collectionId, revision, cancellationToken);
    return collection is not null
        ? Results.Ok(collection)
        : CatalogProblem.Result(context, "library-collection-not-found", "Document collection not found", 404);
});

library.MapGet("/blueprints", async Task<IResult> (
    HttpContext context,
    string? query,
    ILibraryAccessService libraryService,
    CancellationToken cancellationToken) =>
{
    var tenantId = TenantResolution.ResolveTenantId(context.User);
    if (tenantId is null)
    {
        return CatalogProblem.Result(context, "tenant-unauthorized", "Caller tenant identity cannot be established", 403);
    }

    var blueprints = await libraryService.ListEffectiveBlueprintsAsync(tenantId, cancellationToken);
    if (!string.IsNullOrWhiteSpace(query))
    {
        var q = query.Trim();
        blueprints = blueprints.Where(b =>
            b.BlueprintId.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            b.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
            (b.Issuer != null && b.Issuer.Contains(q, StringComparison.OrdinalIgnoreCase))).ToList();
    }
    return Results.Ok(blueprints);
});

library.MapGet("/blueprints/{namespace}/{blueprintId}", async Task<IResult> (
    HttpContext context,
    string @namespace,
    string blueprintId,
    int? revision,
    ILibraryAccessService libraryService,
    CancellationToken cancellationToken) =>
{
    var tenantId = TenantResolution.ResolveTenantId(context.User);
    if (tenantId is null)
    {
        return CatalogProblem.Result(context, "tenant-unauthorized", "Caller tenant identity cannot be established", 403);
    }

    var blueprint = await libraryService.GetBlueprintAsync(tenantId, @namespace, blueprintId, revision, cancellationToken);
    return blueprint is not null
        ? Results.Ok(blueprint)
        : CatalogProblem.Result(context, "library-blueprint-not-found", "Document blueprint not found", 404);
});

library.MapPost("/blueprints/{namespace}/{blueprintId}/{revision}/review", async Task<IResult> (
    HttpContext context,
    string @namespace,
    string blueprintId,
    int revision,
    BlueprintReviewRequest request,
    IBlueprintPublicationService publicationService,
    CancellationToken cancellationToken) =>
{
    if (string.Equals(request.ReviewerId.Trim(), request.AuthorId.Trim(), StringComparison.OrdinalIgnoreCase))
    {
        return CatalogProblem.Result(context, "review-self-approval-prohibited", "Reviewer cannot be the author (self-approval prohibited)", 400);
    }

    try
    {
        await publicationService.ReviewBlueprintAsync(@namespace, blueprintId, revision, request, cancellationToken);
        return Results.Ok(new { status = "REVIEW_APPROVED", @namespace, blueprintId, revision, reviewer = request.ReviewerId });
    }
    catch (Exception ex)
    {
        return CatalogProblem.Result(context, "review-failed", ex.Message, 400);
    }
});

library.MapPost("/blueprints/{namespace}/{blueprintId}/{revision}/publish", async Task<IResult> (
    HttpContext context,
    string @namespace,
    string blueprintId,
    int revision,
    BlueprintPublishRequest request,
    IBlueprintPublicationService publicationService,
    CancellationToken cancellationToken) =>
{
    if (request.AuthorId != null && string.Equals(request.ReviewerId.Trim(), request.AuthorId.Trim(), StringComparison.OrdinalIgnoreCase))
    {
        return CatalogProblem.Result(context, "publish-self-approval-prohibited", "Reviewer cannot be the author", 400);
    }

    try
    {
        await publicationService.PublishBlueprintAsync(@namespace, blueprintId, revision, request, cancellationToken);
        return Results.Ok(new { status = "PUBLISHED", @namespace, blueprintId, revision, manifestSha256 = request.ManifestSha256 });
    }
    catch (Exception ex)
    {
        return CatalogProblem.Result(context, "publish-failed", ex.Message, 400);
    }
});

library.MapPost("/blueprints/{namespace}/{blueprintId}/{revision}/check-drift", async Task<IResult> (
    HttpContext context,
    string @namespace,
    string blueprintId,
    int revision,
    BlueprintDriftCheckRequest request,
    IBlueprintPublicationService publicationService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var isQuarantined = await publicationService.CheckAndQuarantineDriftAsync(@namespace, blueprintId, revision, request, cancellationToken);
        return Results.Ok(new { isQuarantined, @namespace, blueprintId, revision });
    }
    catch (Exception ex)
    {
        return CatalogProblem.Result(context, "drift-check-failed", ex.Message, 400);
    }
});

library.MapPost("/blueprints/{namespace}/{blueprintId}/rollback", async Task<IResult> (
    HttpContext context,
    string @namespace,
    string blueprintId,
    BlueprintRollbackRequest request,
    IBlueprintPublicationService publicationService,
    CancellationToken cancellationToken) =>
{
    try
    {
        await publicationService.RollbackBlueprintAsync(@namespace, blueprintId, request, cancellationToken);
        return Results.Ok(new { status = "ROLLED_BACK", @namespace, blueprintId, targetRevision = request.TargetRevision });
    }
    catch (Exception ex)
    {
        return CatalogProblem.Result(context, "rollback-failed", ex.Message, 400);
    }
});

library.MapGet("/blueprints/{namespace}/{blueprintId}/audit", async Task<IResult> (
    HttpContext context,
    string @namespace,
    string blueprintId,
    int? revision,
    IBlueprintPublicationService publicationService,
    CancellationToken cancellationToken) =>
{
    var trail = await publicationService.GetAuditTrailAsync(@namespace, blueprintId, revision, cancellationToken);
    return Results.Ok(trail);
});

app.Run();

static bool TryParseActivationState(string? value, out FormActivationState? state)
{
    state = value switch
    {
        null => null,
        "UNAVAILABLE" => FormActivationState.Unavailable,
        "CATALOG_ONLY" => FormActivationState.CatalogOnly,
        "ASSISTED" => FormActivationState.Assisted,
        "PILOT" => FormActivationState.Pilot,
        _ => null
    };
    return value is null || state is not null;
}

public partial class Program { }
