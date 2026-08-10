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

builder.Services.AddSingleton<CatalogRepository>();

var app = builder.Build();

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

app.MapGet("/health", () =>
    Results.Ok(new HealthResponse("ok", ServiceMetadata.Name, ServiceMetadata.Version)));
// Readiness resolves the repository rather than answering from a literal. CatalogRepository
// initialises its fixture in a static constructor that throws on an unrecognised form number; if
// that happens, every catalog route returns 500 forever while a literal probe stays green.
app.MapGet("/ready", (IServiceProvider services, ILoggerFactory loggerFactory) =>
{
    try
    {
        if (services.GetRequiredService<CatalogRepository>().GetHierarchy().Count > 0)
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

var catalog = app.MapGroup("/v1/catalog");

catalog.MapGet("/categories", (CatalogRepository repository) =>
    Results.Ok(new CatalogHierarchyResponse(repository.GetHierarchy())));

catalog.MapGet("/packages", IResult (
    HttpContext context,
    string? categoryCode,
    string? subcategoryCode,
    string? activationState,
    CatalogRepository repository) =>
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

    var packages = repository.ListPackages(categoryCode, subcategoryCode, parsedActivationState);
    return Results.Ok(new FormPackageListResponse(packages));
});

catalog.MapGet("/packages/{packageCode}", IResult (
    HttpContext context,
    string packageCode,
    CatalogRepository repository) =>
{
    if (!CatalogPatterns.CatalogCode().IsMatch(packageCode))
    {
        return CatalogProblem.Result(context, "catalog-code-invalid", "packageCode is invalid", 400);
    }

    return repository.GetPackage(packageCode) is { } package
        ? Results.Ok(package)
        : CatalogProblem.Result(context, "catalog-package-not-found", "Catalog package not found", 404);
});

catalog.MapGet(
    "/authorities/{authority}/forms/{formId}/editions/{editionDate}/schemas/{schemaVersion}",
    // editionDate binds as a string and is parsed here rather than as a DateOnly route value.
    // Framework binding failures produce a text/plain diagnostic in Development and a bare empty
    // 400 in Production, so the error a client sees would depend on the environment.
    IResult (
        HttpContext context,
        string authority,
        string formId,
        string editionDate,
        string schemaVersion,
        CatalogRepository repository) =>
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

    return repository.GetSchema(authority, formId, parsedEditionDate, schemaVersion) is { } schema
        ? Results.Ok(schema)
        : CatalogProblem.Result(context, "catalog-schema-not-found", "Catalog schema not found", 404);
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
