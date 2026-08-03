using System.Text.Json.Serialization;
using LaPluma.CoreApi;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddSingleton<CatalogRepository>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new HealthResponse("ok", "core-api", "0.2.0")));
app.MapGet("/ready", () => Results.Ok(new HealthResponse("ready", "core-api", "0.2.0")));

var catalog = app.MapGroup("/v1/catalog");

catalog.MapGet("/categories", (CatalogRepository repository) =>
    Results.Ok(new CatalogHierarchyResponse(repository.GetHierarchy())));

catalog.MapGet("/packages", IResult (
    string? categoryCode,
    string? subcategoryCode,
    string? activationState,
    CatalogRepository repository) =>
{
    if (!TryParseActivationState(activationState, out var parsedActivationState))
    {
        return Results.BadRequest(
            Problem("catalog-activation-invalid", "Activation state is invalid", 400));
    }
    var packages = repository.ListPackages(categoryCode, subcategoryCode, parsedActivationState);
    return Results.Ok(new FormPackageListResponse(packages));
});

catalog.MapGet("/packages/{packageCode}", (string packageCode, CatalogRepository repository) =>
    repository.GetPackage(packageCode) is { } package
        ? Results.Ok(package)
        : Results.NotFound(Problem("catalog-package-not-found", "Catalog package not found", 404)));

catalog.MapGet(
    "/authorities/{authority}/forms/{formId}/editions/{editionDate}/schemas/{schemaVersion}",
    (string authority, string formId, DateOnly editionDate, string schemaVersion, CatalogRepository repository) =>
        repository.GetSchema(authority, formId, editionDate, schemaVersion) is { } schema
            ? Results.Ok(schema)
            : Results.NotFound(Problem("catalog-schema-not-found", "Catalog schema not found", 404)));

app.Run();

static ProblemDetailsResponse Problem(string type, string title, int status) =>
    new($"urn:lapluma:problem:{type}", title, status, null, Guid.NewGuid());

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
