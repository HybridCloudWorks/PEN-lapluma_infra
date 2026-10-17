namespace LaPluma.WorkflowApi;

/// <summary>
/// Outcome of registering an idempotency key against a payload. Replay of the same payload returns
/// the original result; the same key with a different payload is a caller bug worth a 409.
/// </summary>
public enum IdempotencyOutcome
{
    Created,
    Replayed,
    Conflict,
}

public sealed record CreateClientOutcome(IdempotencyOutcome Outcome, ClientDirectoryEntry Entry);

public enum ReviewDecisionStatus { Success, NotFound, InvalidState, Conflict }
public sealed record RecordReviewDecisionOutcome(ReviewDecisionStatus Status, ReviewDecision? Decision = null);

public enum DraftPreviewStatus { Success, NotFound, InvalidState }
public sealed record CreateDraftPreviewOutcome(DraftPreviewStatus Status, DraftFormPreview? Preview = null);

public enum StepUpChallengeStatus { Success, NotFound }
public sealed record CreateStepUpChallengeOutcome(StepUpChallengeStatus Status, StepUpChallenge? Challenge = null);

public enum ApproveCaseStatus { Success, NotFound, InvalidState, StepUpRequired, SeparationOfDutiesViolation, StalePreview, Conflict }
public sealed record ApproveCaseOutcome(ApproveCaseStatus Status, ApprovalRecord? Record = null);

public enum CommitSectionStatus { Success, NotFound, VersionConflict, Conflict }
public sealed record CommitSectionOutcome(CommitSectionStatus Status, SectionCommit? Result = null);

public enum PackageGenerationStatus { Success, NotFound, InvalidState, EditionDrift, HumanConfirmationRequired, ApprovalInvalidated, Conflict }
public sealed record PackageGenerationOutcome(PackageGenerationStatus Status, GeneratedPackage? Package = null, int UnconfirmedCount = 0);

public enum PackageDownloadStatus { Success, NotFound, ApprovalInvalidated, NotReady }
public sealed record PackageDownloadOutcome(PackageDownloadStatus Status, ScopedDownloadGrant? Grant = null);

public enum WorkflowEventProcessingStatus { Processed, DuplicateIgnored, RegressionPrevented, Quarantined, BadPayload }
public sealed record WorkflowEventOutcome(WorkflowEventProcessingStatus Status, WorkflowEventReceipt? Receipt = null);

/// <summary>
/// The workflow read and write surface behind the HTTP handlers.
///
/// Every operation on client or case data takes the caller's tenant, resolved from the session
/// token by <see cref="WorkflowTenant"/>, and must neither return nor change another tenant's
/// rows: a case that belongs to someone else is indistinguishable from one that does not exist.
/// </summary>
public interface IWorkflowSource
{
    Task<AuthenticatedContext> GetSessionContextAsync(string userId, CancellationToken cancellationToken);

    /// <summary>
    /// Proves the store is reachable and its schema is present, without reading any tenant's
    /// data. Readiness calls this; it must not require rows to exist.
    /// </summary>
    Task CheckReadinessAsync(CancellationToken cancellationToken);

    Task<ClientDirectoryPage> ListClientsAsync(
        string tenantId, string? query, string? cursor, CancellationToken cancellationToken);

    Task<CreateClientOutcome> CreateClientAsync(
        string tenantId, string idempotencyKey, CreateClientRequest request, CancellationToken cancellationToken);

    /// <summary>Null when the case is missing or the caller is not authorized to see it — one 404.</summary>
    Task<CaseWorkspace?> GetCaseWorkspaceAsync(string tenantId, string caseId, CancellationToken cancellationToken);

    Task<IReadOnlyList<ReviewQueueItem>> GetReviewQueueAsync(string tenantId, CancellationToken cancellationToken);

    Task<RecordReviewDecisionOutcome> RecordReviewDecisionAsync(
        string tenantId, string caseId, string reviewerId, ReviewDecisionRequest request, string idempotencyKey, CancellationToken cancellationToken);

    Task<CreateDraftPreviewOutcome> CreateDraftPreviewAsync(
        string tenantId, string caseId, CancellationToken cancellationToken);

    Task<CreateStepUpChallengeOutcome> CreateStepUpChallengeAsync(
        string tenantId, string caseId, string userId, CancellationToken cancellationToken);

    Task<ApproveCaseOutcome> ApproveCaseAsync(
        string tenantId, string caseId, string approverId, CaseApprovalRequest request, string idempotencyKey, CancellationToken cancellationToken);

    Task<IReadOnlyList<CaseHistoryEvent>> GetCaseHistoryAsync(
        string tenantId, string caseId, CancellationToken cancellationToken);

    Task<CommitSectionOutcome> CommitSectionAsync(
        string tenantId, string caseId, string sectionId, int baseRevision, Dictionary<string, string> values, string userId, string idempotencyKey, CancellationToken cancellationToken);

    Task<PackageGenerationOutcome> RequestPackageGenerationAsync(
        string tenantId, string caseId, string userId, string idempotencyKey, CancellationToken cancellationToken);

    Task<PackageDownloadOutcome> GetPackageDownloadAsync(
        string tenantId, string caseId, string packageId, string userId, CancellationToken cancellationToken);

    Task<WorkflowEventOutcome> ProcessWorkflowEventAsync(
        string tenantId, PubSubWorkflowEvent workflowEvent, string idempotencyKey, CancellationToken cancellationToken);
}
