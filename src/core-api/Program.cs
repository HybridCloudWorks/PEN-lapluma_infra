using System.Globalization;
using LaPluma.CoreApi;

var builder = WebApplication.CreateBuilder(args);

// Enum wire names are declared per type in CatalogModels.cs with [JsonStringEnumMemberName].
// There is deliberately no global JsonStringEnumConverter: a type-level attribute takes precedence
// over a globally registered converter, so the global one never applied to any existing enum while
// making a new enum added without those attributes look handled — it would quietly serialise in
// PascalCase instead of failing visibly.
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
        "request-invalid", "Request is invalid", response.StatusCode);
    await response.WriteAsJsonAsync(problem, options: null, contentType: CatalogProblem.ContentType);
});

app.MapGet("/health", () =>
    Results.Ok(new HealthResponse("ok", ServiceMetadata.Name, ServiceMetadata.Version)));
app.MapGet("/ready", () =>
    Results.Ok(new HealthResponse("ready", ServiceMetadata.Name, ServiceMetadata.Version)));

var catalog = app.MapGroup("/v1/catalog");

catalog.MapGet("/categories", (CatalogRepository repository) =>
    Results.Ok(new CatalogHierarchyResponse(repository.GetHierarchy())));

catalog.MapGet("/packages", IResult (
    string? categoryCode,
    string? subcategoryCode,
    string? activationState,
    CatalogRepository repository) =>
{
    // The contract constrains these; enforcing it here is what makes a typo a diagnosable 400
    // rather than a successful empty catalog indistinguishable from a legitimately empty one.
    if (categoryCode is not null && !CatalogPatterns.CatalogCode().IsMatch(categoryCode))
    {
        return CatalogProblem.Result("catalog-code-invalid", "categoryCode is invalid", 400);
    }

    if (subcategoryCode is not null && !CatalogPatterns.CatalogCode().IsMatch(subcategoryCode))
    {
        return CatalogProblem.Result("catalog-code-invalid", "subcategoryCode is invalid", 400);
    }

    if (!TryParseActivationState(activationState, out var parsedActivationState))
    {
        return CatalogProblem.Result(
            "catalog-activation-invalid", "Activation state is invalid", 400);
    }

    var packages = repository.ListPackages(categoryCode, subcategoryCode, parsedActivationState);
    return Results.Ok(new FormPackageListResponse(packages));
});

catalog.MapGet("/packages/{packageCode}", IResult (
    string packageCode,
    CatalogRepository repository) =>
{
    if (!CatalogPatterns.CatalogCode().IsMatch(packageCode))
    {
        return CatalogProblem.Result("catalog-code-invalid", "packageCode is invalid", 400);
    }

    return repository.GetPackage(packageCode) is { } package
        ? Results.Ok(package)
        : CatalogProblem.Result("catalog-package-not-found", "Catalog package not found", 404);
});

catalog.MapGet(
    "/authorities/{authority}/forms/{formId}/editions/{editionDate}/schemas/{schemaVersion}",
    // editionDate binds as a string and is parsed here rather than as a DateOnly route value.
    // Framework binding failures produce a text/plain diagnostic in Development and a bare empty
    // 400 in Production, so the error a client sees would depend on the environment.
    IResult (
        string authority,
        string formId,
        string editionDate,
        string schemaVersion,
        CatalogRepository repository) =>
{
    if (authority.Length is 0 or > 160)
    {
        return CatalogProblem.Result("catalog-authority-invalid", "authority is invalid", 400);
    }

    if (!CatalogPatterns.FormId().IsMatch(formId))
    {
        return CatalogProblem.Result("catalog-form-id-invalid", "formId is invalid", 400);
    }

    if (!CatalogPatterns.SchemaVersion().IsMatch(schemaVersion))
    {
        return CatalogProblem.Result(
            "catalog-schema-version-invalid", "schemaVersion is invalid", 400);
    }

    if (!DateOnly.TryParseExact(
            editionDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None,
            out var parsedEditionDate))
    {
        return CatalogProblem.Result(
            "catalog-edition-date-invalid", "editionDate must be an ISO 8601 date", 400);
    }

    return repository.GetSchema(authority, formId, parsedEditionDate, schemaVersion) is { } schema
        ? Results.Ok(schema)
        : CatalogProblem.Result("catalog-schema-not-found", "Catalog schema not found", 404);
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
