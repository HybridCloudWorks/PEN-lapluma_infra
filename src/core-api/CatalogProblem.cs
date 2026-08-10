using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
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

    /// <summary>
    /// Log category for problem responses. Content-free by construction: it records the problem
    /// type, the status, and the correlation identifier — never a path, query string, route value,
    /// or anything derived from a request body.
    /// </summary>
    public const string LogCategory = "LaPluma.CoreApi.Problem";

    public static ProblemDetailsResponse Create(HttpContext context, string type, string title, int status)
    {
        var correlationId = CorrelationId(context);
        context.RequestServices.GetRequiredService<ILoggerFactory>()
            .CreateLogger(LogCategory)
            .LogWarning(
                "Request rejected with problem {ProblemType} and status {Status}, correlation {CorrelationId}",
                type,
                status,
                correlationId);

        return new($"urn:lapluma:problem:{type}", title, status, null, correlationId);
    }

    public static IResult Result(HttpContext context, string type, string title, int status) =>
        Results.Json(
            Create(context, type, title, status),
            options: null,
            contentType: ContentType,
            // The body's status and the HTTP status come from one value, so they cannot disagree.
            statusCode: status);

    /// <summary>
    /// The identifier a user reports to support, derived from the ambient trace rather than minted
    /// fresh. A W3C trace identifier is sixteen bytes — exactly a GUID — so the contract's
    /// <c>format: uuid</c> holds while the value is something that exists in the trace backend.
    /// </summary>
    internal static Guid CorrelationId(HttpContext context)
    {
        var activity = Activity.Current;
        if (activity is not null && activity.IdFormat == ActivityIdFormat.W3C)
        {
            Span<byte> traceId = stackalloc byte[16];
            activity.TraceId.CopyTo(traceId);
            return new Guid(traceId);
        }

        // No ambient activity. Derive from the connection-scoped identifier so the value is still
        // reproducible from the request rather than a random number tied to nothing.
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(context.TraceIdentifier), digest);
        return new Guid(digest[..16]);
    }
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
