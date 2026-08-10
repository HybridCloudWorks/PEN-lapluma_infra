using System.Text.RegularExpressions;

namespace LaPluma.CoreApi;

/// <summary>Identity this service reports about itself.</summary>
public static class ServiceMetadata
{
    public const string Name = "core-api";

    /// <summary>
    /// Must equal <c>info.version</c> in <c>contracts/catalog.openapi.json</c>. A test asserts it.
    /// </summary>
    public const string Version = "0.2.0";
}

/// <summary>
/// Problem documents, served as <c>application/problem+json</c> because that is what the catalog
/// contract declares. The status code travels once, from the caller into both the HTTP status and
/// the body, so the two cannot drift apart.
/// </summary>
public static class CatalogProblem
{
    public const string ContentType = "application/problem+json";

    public static ProblemDetailsResponse Create(string type, string title, int status) =>
        new($"urn:lapluma:problem:{type}", title, status, null, Guid.NewGuid());

    public static IResult Result(string type, string title, int status) =>
        Results.Json(
            Create(type, title, status),
            options: null,
            contentType: ContentType,
            statusCode: status);
}

/// <summary>
/// The input patterns the catalog contract declares. Kept here so the implementation and
/// <c>contracts/catalog.openapi.json</c> can be compared directly.
/// </summary>
public static partial class CatalogPatterns
{
    [GeneratedRegex("^[A-Z][A-Z0-9_]{1,63}$")]
    public static partial Regex CatalogCode();

    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]{0,31}$")]
    public static partial Regex FormId();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,31}$")]
    public static partial Regex SchemaVersion();
}
