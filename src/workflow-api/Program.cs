using System.Security.Claims;
using LaPluma.WorkflowApi;

var builder = WebApplication.CreateBuilder(args);

// ASP.NET Core's request logging emits the full URL, query string included, at Information — on by
// default. Telemetry here must be content-free, so those two categories are raised to Warning: the
// service logs every rejection itself with a correlation identifier and no request content, which
// is the signal worth keeping. Warnings and errors from both categories still come through.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.AspNetCore.Routing", LogLevel.Warning);

builder.Services.AddWorkflowSource(builder.Configuration);
builder.Services.AddWorkflowAuthentication(builder.Configuration);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<UploadSessionStore>();
builder.Services.AddSingleton<IStoredDocumentVerifier, PassThroughStoredDocumentVerifier>();

// Wired when the deployment names a GCP quarantine bucket or Azure quarantine endpoint; fail-closed otherwise. Built now, not
// lazily, for the same reason the catalog's SQL options are: a misconfigured endpoint should fail
// the starting host, not the first upload.
var gcsQuarantineBucket = builder.Configuration[UploadConfiguration.GcsQuarantineBucketSetting];
var quarantineEndpoint = builder.Configuration[UploadConfiguration.QuarantineBlobEndpointSetting];
if (!string.IsNullOrWhiteSpace(gcsQuarantineBucket))
{
    builder.Services.AddSingleton<IUploadUrlIssuer>(new GoogleCloudStorageUploadUrlIssuer(
        gcsQuarantineBucket,
        builder.Configuration[UploadConfiguration.GcsSignerServiceAccountSetting]));
}
else if (!string.IsNullOrWhiteSpace(quarantineEndpoint))
{
    builder.Services.AddSingleton<IUploadUrlIssuer>(new UserDelegationUploadUrlIssuer(
        new Uri(quarantineEndpoint, UriKind.Absolute),
        builder.Configuration[UploadConfiguration.ManagedIdentityClientIdSetting]));
}
else
{
    builder.Services.AddSingleton<IUploadUrlIssuer, NotConfiguredUploadUrlIssuer>();
}

var app = builder.Build();

// Ordering matters and is not cosmetic. UseStatusCodePages inspects the response on the way out, so
// it only sees what the middleware registered after it produced. Authentication and authorization
// go below it: registered above, their 401 and 403 would travel outward past this and reach the
// client as a bare status code with no body, while every other failure carried a problem document.
app.UseStatusCodePages(async context =>
{
    var response = context.HttpContext.Response;
    if (response.HasStarted)
    {
        return;
    }

    var problem = WorkflowProblem.Create(
        context.HttpContext, "request-invalid", "Request is invalid", response.StatusCode);
    await response.WriteAsJsonAsync(problem, options: null, contentType: WorkflowProblem.ContentType);
});

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () =>
    Results.Ok(new HealthResponse("ok", ServiceMetadata.Name, ServiceMetadata.Version)));
app.MapGet("/healthz", () =>
    Results.Ok(new HealthResponse("ok", ServiceMetadata.Name, ServiceMetadata.Version)));
// Readiness resolves the source and queries it rather than answering from a literal: if the
// registration refused a store nobody chose, or the store cannot answer, readiness must say so.
// It deliberately does not require the answer to be non-empty — an empty client directory is a
// healthy answer from a freshly provisioned durable store, not a failed replica.
app.MapGet("/ready", async (
    IServiceProvider services, ILoggerFactory loggerFactory, CancellationToken cancellationToken) =>
{
    try
    {
        var source = services.GetRequiredService<IWorkflowSource>();
        _ = await source.ListClientsAsync(null, null, cancellationToken);
        return Results.Ok(
            new HealthResponse("ready", ServiceMetadata.Name, ServiceMetadata.Version));
    }
    catch (Exception error)
    {
        // Deliberately broad: any failure to construct the workflow source means this replica
        // cannot serve its only purpose, and readiness must say so rather than propagate.
        loggerFactory.CreateLogger(WorkflowProblem.LogCategory)
            .LogError(error, "Workflow source could not be initialised; reporting not ready.");
    }

    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
});

// Applied to the group, so a route added later inherits it rather than having to remember it.
// /health and /ready are deliberately outside: an orchestrator probing them holds no token.
var v1 = app.MapGroup("/v1").RequireAuthorization(WorkflowAuthentication.PolicyName);

v1.MapGet("/session", async (
    HttpContext context, IWorkflowSource source, CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "caller-unidentified";
    return Results.Ok(await source.GetSessionContextAsync(userId, cancellationToken));
});

v1.MapGet("/clients", async Task<IResult> (
    HttpContext context,
    string? query,
    string? cursor,
    IWorkflowSource source,
    CancellationToken cancellationToken) =>
{
    // Bounded inputs. The directory query is a search term, not a place for free text of
    // arbitrary size, and a cursor is opaque but not unbounded.
    if (query is { Length: > 256 })
    {
        return WorkflowProblem.Result(context, "clients-query-invalid", "query is too long", 400);
    }

    if (cursor is { Length: > 512 })
    {
        return WorkflowProblem.Result(context, "clients-cursor-invalid", "cursor is invalid", 400);
    }

    return Results.Ok(await source.ListClientsAsync(query, cursor, cancellationToken));
});

v1.MapPost("/clients", async Task<IResult> (
    HttpContext context,
    CreateClientRequest request,
    IWorkflowSource source,
    CancellationToken cancellationToken) =>
{
    if (RequireIdempotencyKey(context) is { } keyProblem)
    {
        return keyProblem;
    }

    if (string.IsNullOrWhiteSpace(request.DisplayLabel) || request.DisplayLabel.Length > 120)
    {
        return WorkflowProblem.Result(
            context, "client-label-invalid", "displayLabel must be 1 to 120 characters", 422);
    }

    var outcome = await source.CreateClientAsync(
        IdempotencyKey(context), request, cancellationToken);
    return outcome.Outcome switch
    {
        IdempotencyOutcome.Conflict => WorkflowProblem.Result(
            context,
            "idempotency-key-conflict",
            "Idempotency key was already used with a different payload",
            409),
        // A replay returns the original result — same body, same status — which is the retry
        // contract the client's offline mutation queue depends on.
        _ => Results.Created($"/v1/clients/{outcome.Entry.Id}", outcome.Entry),
    };
});

v1.MapGet("/cases/{caseId}/workspace", async Task<IResult> (
    HttpContext context,
    string caseId,
    IWorkflowSource source,
    CancellationToken cancellationToken) =>
    await source.GetCaseWorkspaceAsync(caseId, cancellationToken) is { } workspace
        ? Results.Ok(workspace)
        // One problem type for missing and unauthorized. Distinguishing them would let a caller
        // probe for the existence of cases they cannot see.
        : WorkflowProblem.Result(context, "not-found", "Missing or unauthorized", 404));

v1.MapPost("/documents/upload-sessions", async Task<IResult> (
    HttpContext context,
    CreateUploadSessionRequest request,
    UploadSessionStore store,
    IUploadUrlIssuer issuer,
    CancellationToken cancellationToken) =>
{
    if (RequireIdempotencyKey(context) is { } keyProblem)
    {
        return keyProblem;
    }

    if (string.IsNullOrWhiteSpace(request.FolderId)
        || string.IsNullOrWhiteSpace(request.OriginalName)
        || request.OriginalName.Length > UploadSessionStore.MaximumOriginalNameLength
        || request.SizeBytes is < 1 or > UploadSessionStore.MaximumSizeBytes
        || request.ContentSha256 is null
        || !UploadSessionStore.ContentSha256().IsMatch(request.ContentSha256))
    {
        return WorkflowProblem.Result(
            context, "upload-session-invalid", "File metadata exceeds capture limits", 422);
    }

    var (outcome, sessionId, documentId, expiresAt) =
        store.Create(IdempotencyKey(context), request);
    if (outcome == IdempotencyOutcome.Conflict)
    {
        return WorkflowProblem.Result(
            context,
            "idempotency-key-conflict",
            "Idempotency key was already used with a different payload",
            409);
    }

    try
    {
        var uploadUrl = await issuer.MintWriteOnlyUrlAsync(documentId, expiresAt, cancellationToken);
        return Results.Created(
            $"/v1/documents/upload-sessions/{sessionId}",
            new UploadSession(
                sessionId, documentId, uploadUrl, "PUT", expiresAt, request.ContentSha256));
    }
    catch (UploadIssuingNotConfiguredException)
    {
        return WorkflowProblem.Result(
            context,
            "upload-not-configured",
            "Upload issuing is not configured in this environment",
            503);
    }
});

v1.MapPost("/documents/upload-sessions/{sessionId}/complete", async (
    HttpContext context,
    string sessionId,
    UploadSessionStore store,
    IStoredDocumentVerifier verifier,
    CancellationToken cancellationToken) =>
{
    if (RequireIdempotencyKey(context) is { } keyProblem)
    {
        return keyProblem;
    }

    string? actualSha256 = null;
    long? actualSizeBytes = null;
    if (store.TryGetSession(sessionId, out var sessionMeta))
    {
        var verification = await verifier.VerifyAsync(
            sessionMeta.DocumentId,
            sessionMeta.ExpectedSha256,
            sessionMeta.DeclaredSizeBytes,
            cancellationToken);

        if (verification.Outcome == StoredDocumentVerificationOutcome.NotFound)
        {
            return WorkflowProblem.Result(
                context, "upload-blob-missing", "Uploaded blob not found in storage", 422);
        }
        if (verification.Outcome == StoredDocumentVerificationOutcome.DigestMismatch)
        {
            return WorkflowProblem.Result(
                context, "upload-digest-mismatch",
                "Actual content SHA-256 digest does not match declared digest", 422);
        }
        if (verification.Outcome == StoredDocumentVerificationOutcome.SizeMismatch)
        {
            return WorkflowProblem.Result(
                context, "upload-size-invalid",
                "Actual file size does not match declared size or exceeds limits", 422);
        }

        actualSha256 = verification.ActualSha256 ?? sessionMeta.ExpectedSha256;
        actualSizeBytes = verification.ActualSizeBytes ?? sessionMeta.DeclaredSizeBytes;
    }

    var result = store.Complete(sessionId, IdempotencyKey(context), actualSha256, actualSizeBytes);
    return result.Outcome switch
    {
        CompleteUploadOutcome.Completed or CompleteUploadOutcome.Replayed =>
            Results.Ok(result.Receipt),
        CompleteUploadOutcome.NotFound =>
            WorkflowProblem.Result(context, "not-found", "Missing or unauthorized", 404),
        CompleteUploadOutcome.AlreadyConsumed => WorkflowProblem.Result(
            context, "upload-session-consumed",
            "Session already consumed by a different request", 409),
        CompleteUploadOutcome.DigestMismatch => WorkflowProblem.Result(
            context, "upload-digest-mismatch",
            "Actual content SHA-256 digest does not match declared digest", 422),
        CompleteUploadOutcome.SizeMismatch => WorkflowProblem.Result(
            context, "upload-size-invalid",
            "Actual file size does not match declared size or exceeds limits", 422),
        _ => WorkflowProblem.Result(
            context, "upload-session-expired", "Upload session expired", 422),
    };
});

v1.MapGet("/review-queue", async (
    HttpContext context,
    IWorkflowSource source,
    CancellationToken cancellationToken) =>
    Results.Ok(await source.GetReviewQueueAsync(cancellationToken)));

v1.MapPost("/cases/{caseId}/review-decisions", async (
    HttpContext context,
    string caseId,
    ReviewDecisionRequest request,
    IWorkflowSource source,
    CancellationToken cancellationToken) =>
{
    if (RequireIdempotencyKey(context) is { } keyProblem)
    {
        return keyProblem;
    }

    var outcomeStr = request.Outcome?.ToUpperInvariant();
    if (outcomeStr is not ("CHANGES_REQUESTED" or "READY_FOR_APPROVAL"))
    {
        return WorkflowProblem.Result(
            context, "review-outcome-invalid", "outcome must be CHANGES_REQUESTED or READY_FOR_APPROVAL", 422);
    }

    var reviewerId = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "reviewer-unidentified";
    var outcome = await source.RecordReviewDecisionAsync(
        caseId, reviewerId, request, IdempotencyKey(context), cancellationToken);

    return outcome.Status switch
    {
        ReviewDecisionStatus.Success => Results.Created($"/v1/cases/{caseId}/review-decisions", outcome.Decision),
        ReviewDecisionStatus.NotFound => WorkflowProblem.Result(context, "not-found", "Missing or unauthorized", 404),
        ReviewDecisionStatus.InvalidState => WorkflowProblem.Result(context, "review-state", "Case is not in review", 409),
        _ => WorkflowProblem.Result(context, "idempotency-key-conflict", "Idempotency key was already used with a different payload", 409),
    };
});

v1.MapPost("/cases/{caseId}/draft-preview", async (
    HttpContext context,
    string caseId,
    IWorkflowSource source,
    CancellationToken cancellationToken) =>
{
    if (RequireIdempotencyKey(context) is { } keyProblem)
    {
        return keyProblem;
    }

    var outcome = await source.CreateDraftPreviewAsync(caseId, cancellationToken);
    return outcome.Status switch
    {
        DraftPreviewStatus.Success => Results.Created($"/v1/cases/{caseId}/draft-preview", outcome.Preview),
        DraftPreviewStatus.NotFound => WorkflowProblem.Result(context, "not-found", "Missing or unauthorized", 404),
        _ => WorkflowProblem.Result(context, "preview-state", "Preview is not available at this stage", 409),
    };
});

v1.MapPost("/cases/{caseId}/step-up-challenge", async (
    HttpContext context,
    string caseId,
    IWorkflowSource source,
    CancellationToken cancellationToken) =>
{
    if (RequireIdempotencyKey(context) is { } keyProblem)
    {
        return keyProblem;
    }

    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "approver-unidentified";
    var outcome = await source.CreateStepUpChallengeAsync(caseId, userId, cancellationToken);

    return outcome.Status switch
    {
        StepUpChallengeStatus.Success => Results.Created($"/v1/cases/{caseId}/step-up-challenge", outcome.Challenge),
        _ => WorkflowProblem.Result(context, "not-found", "Missing or unauthorized", 404),
    };
});

v1.MapPost("/cases/{caseId}/approval", async (
    HttpContext context,
    string caseId,
    CaseApprovalRequest request,
    IWorkflowSource source,
    CancellationToken cancellationToken) =>
{
    if (RequireIdempotencyKey(context) is { } keyProblem)
    {
        return keyProblem;
    }

    if (!request.Attested || string.IsNullOrWhiteSpace(request.StepUpChallenge))
    {
        return WorkflowProblem.Result(
            context, "step-up-required", "Step-up authentication and attestation are required", 401);
    }

    var approverId = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "user-fixture-approver";
    var outcome = await source.ApproveCaseAsync(caseId, approverId, request, IdempotencyKey(context), cancellationToken);

    return outcome.Status switch
    {
        ApproveCaseStatus.Success => Results.Created($"/v1/cases/{caseId}/approval", outcome.Record),
        ApproveCaseStatus.NotFound => WorkflowProblem.Result(context, "not-found", "Missing or unauthorized", 404),
        ApproveCaseStatus.StepUpRequired => WorkflowProblem.Result(context, "step-up-required", "Step-up authentication and attestation are required", 401),
        ApproveCaseStatus.SeparationOfDutiesViolation => WorkflowProblem.Result(context, "separation-of-duties", "Approver must be distinct from preparer and reviewer", 403),
        ApproveCaseStatus.InvalidState => WorkflowProblem.Result(context, "approval-state", "Case is not ready for approval", 409),
        ApproveCaseStatus.StalePreview => WorkflowProblem.Result(context, "stale-preview", "Preview is no longer current", 409),
        _ => WorkflowProblem.Result(context, "idempotency-key-conflict", "Idempotency key was already used with a different payload", 409),
    };
});

v1.MapGet("/cases/{caseId}/history", async (
    HttpContext context,
    string caseId,
    IWorkflowSource source,
    CancellationToken cancellationToken) =>
    Results.Ok(await source.GetCaseHistoryAsync(caseId, cancellationToken)));

v1.MapPost("/cases/{caseId}/sections/{sectionId}/commit", async (
    HttpContext context,
    string caseId,
    string sectionId,
    CommitSectionRequest request,
    IWorkflowSource source,
    CancellationToken cancellationToken) =>
{
    if (RequireIdempotencyKey(context) is { } keyProblem)
    {
        return keyProblem;
    }

    var baseRevision = request.BaseRevision;
    var ifMatch = context.Request.Headers["If-Match"].ToString().Trim('"');
    if (int.TryParse(ifMatch, out var parsedIfMatch))
    {
        baseRevision = parsedIfMatch;
    }

    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "caller-unidentified";
    var outcome = await source.CommitSectionAsync(
        caseId, sectionId, baseRevision, request.Values ?? [], userId, IdempotencyKey(context), cancellationToken);

    return outcome.Status switch
    {
        CommitSectionStatus.Success => Results.Ok(outcome.Result),
        CommitSectionStatus.NotFound => WorkflowProblem.Result(context, "not-found", "Missing or unauthorized", 404),
        CommitSectionStatus.VersionConflict => WorkflowProblem.Result(context, "version-conflict", "This section changed on another device", 412),
        _ => WorkflowProblem.Result(context, "idempotency-key-conflict", "Idempotency key was already used with a different payload", 409),
    };
});

v1.MapPost("/cases/{caseId}/package-generation", async (
    HttpContext context,
    string caseId,
    IWorkflowSource source,
    CancellationToken cancellationToken) =>
{
    if (RequireIdempotencyKey(context) is { } keyProblem)
    {
        return keyProblem;
    }

    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "caller-unidentified";
    var outcome = await source.RequestPackageGenerationAsync(caseId, userId, IdempotencyKey(context), cancellationToken);

    return outcome.Status switch
    {
        PackageGenerationStatus.Success => Results.Created($"/v1/cases/{caseId}/package", outcome.Package),
        PackageGenerationStatus.NotFound => WorkflowProblem.Result(context, "not-found", "Missing or unauthorized", 404),
        PackageGenerationStatus.InvalidState => WorkflowProblem.Result(context, "case-state-forbids-generation", "The application's current state does not allow generating forms", 409),
        PackageGenerationStatus.ApprovalInvalidated => WorkflowProblem.Result(context, "approval-invalidated", "Case approval was invalidated by subsequent field edits", 409),
        PackageGenerationStatus.EditionDrift => WorkflowProblem.Result(context, "form-edition-drift", "This application is pinned to a replaced form edition", 409),
        PackageGenerationStatus.HumanConfirmationRequired => WorkflowProblem.Result(context, "human-confirmation-required", "Every required value must be confirmed by a human", 409),
        _ => WorkflowProblem.Result(context, "idempotency-key-conflict", "Idempotency key was already used with a different payload", 409),
    };
});

v1.MapGet("/cases/{caseId}/packages/{packageId}/download", async (
    HttpContext context,
    string caseId,
    string packageId,
    IWorkflowSource source,
    CancellationToken cancellationToken) =>
{
    var userId = context.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "caller-unidentified";
    var outcome = await source.GetPackageDownloadAsync(caseId, packageId, userId, cancellationToken);

    return outcome.Status switch
    {
        PackageDownloadStatus.Success => Results.Ok(outcome.Grant),
        PackageDownloadStatus.NotFound => WorkflowProblem.Result(context, "not-found", "Missing or unauthorized", 404),
        PackageDownloadStatus.ApprovalInvalidated => WorkflowProblem.Result(context, "approval-invalidated", "Case approval was invalidated by subsequent field edits", 409),
        PackageDownloadStatus.NotReady => WorkflowProblem.Result(context, "package-not-ready", "Package is not ready for download", 409),
        _ => WorkflowProblem.Result(context, "download-failed", "Unable to issue download grant", 500),
    };
});

v1.MapPost("/events/workflow", async (
    HttpContext context,
    PubSubWorkflowEvent workflowEvent,
    IWorkflowSource source,
    CancellationToken cancellationToken) =>
{
    if (RequireIdempotencyKey(context) is { } keyProblem)
    {
        return keyProblem;
    }

    var outcome = await source.ProcessWorkflowEventAsync(workflowEvent, IdempotencyKey(context), cancellationToken);

    return outcome.Status switch
    {
        WorkflowEventProcessingStatus.Processed => Results.Ok(outcome.Receipt),
        WorkflowEventProcessingStatus.DuplicateIgnored => Results.Ok(outcome.Receipt),
        WorkflowEventProcessingStatus.RegressionPrevented => Results.Ok(outcome.Receipt),
        WorkflowEventProcessingStatus.Quarantined => Results.Ok(outcome.Receipt),
        _ => WorkflowProblem.Result(context, "bad-event-payload", "Invalid event payload", 400),
    };
});

// Every remaining contract operation is mapped explicitly and answers 501, so "not built yet" is
// distinguishable from "wrong URL": an unmapped path is a routing 404, these are a typed problem.
// The two anonymous relay endpoints (GET /relay/{token}, POST /relay/{token}/unlock) are
// deliberately NOT mapped at all: they are the only unauthenticated public surface in the whole
// contract and do not get a handler — even a 501 — before their own security review (TODO 5.8).
foreach (var (method, pattern) in new (string Method, string Pattern)[]
{
    ("POST", "/clients/{clientId}/people"),
    ("POST", "/clients/{clientId}/access-grants"),
    ("POST", "/clients/{clientId}/invitations"),
    ("PUT", "/cases/{caseId}/assignments"),
    ("POST", "/cases/{caseId}/transitions"),
    ("POST", "/cases/{caseId}/evidence-links"),
    ("GET", "/cases/{caseId}/guided-finish"),
    ("GET", "/cases/{caseId}/proof-map"),
    ("GET", "/documents/{documentId}/pages/{pageNumber}/preview"),
    ("GET", "/cases/{caseId}/evidence-relays"),
    ("POST", "/cases/{caseId}/evidence-relays"),
    ("POST", "/evidence-relays/{relayId}/revoke"),
    ("POST", "/evidence-relays/{relayId}/accept"),
    ("POST", "/evidence-relays/{relayId}/reject"),
    ("POST", "/relay-upload-sessions"),
    ("POST", "/relay-upload-sessions/{sessionId}/complete"),
    ("GET", "/admin/members"),
    ("GET", "/admin/sessions"),
    ("GET", "/admin/audit-summary"),
    ("POST", "/admin/demo/enter"),
    ("POST", "/admin/demo/reset"),
    ("POST", "/admin/demo/exit"),
})
{
    v1.MapMethods(pattern, [method], (HttpContext context) =>
        WorkflowProblem.Result(
            context, "not-implemented", "Operation is not implemented in this increment", 501));
}

app.Run();

static string IdempotencyKey(HttpContext context) =>
    context.Request.Headers["Idempotency-Key"].ToString();

static IResult? RequireIdempotencyKey(HttpContext context)
{
    var key = IdempotencyKey(context);
    if (key.Length is < 1 or > 128)
    {
        return WorkflowProblem.Result(
            context,
            "idempotency-key-required",
            "Idempotency-Key header of 1 to 128 characters is required",
            400);
    }

    return null;
}

public partial class Program { }
