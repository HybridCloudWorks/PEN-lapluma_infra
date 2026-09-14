namespace LaPluma.WorkflowApi;

// Wire names follow the Swift models in the app repository's ApertureDomain package
// (WorkflowModels.swift, CaseAggregate.swift, ProgressCounters.swift): ASP.NET's camel-case policy
// turns UserID into userID and FolderID into folderID, which is exactly what the Swift Codable
// encoder produces from those property names. A serialization test pins the load-bearing keys.

public sealed record HealthResponse(string Status, string Service, string Version);

public sealed record ProblemDetailsResponse(
    string Type,
    string Title,
    int Status,
    string? Detail,
    Guid CorrelationId);

/// <summary>Persona and capability projection for the authenticated caller.</summary>
public sealed record AuthenticatedContext(
    string UserID,
    string WorkspaceCode,
    IReadOnlyList<string> Personas,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Capabilities,
    bool IsDemo);

public sealed record ProgressCounters(
    int FieldsFilled,
    int FieldsRequired,
    int DocumentsCollected,
    int DocumentsRequired,
    int BlockingItems,
    int AdvisoryItems);

public sealed record PinnedForm(
    string FormNumber,
    DateTimeOffset EditionDate,
    string SourceSHA256,
    string Encoding,
    bool DriftDetected);

public sealed record CaseSummary(
    string Id,
    string FolderID,
    string PackageCode,
    string PackageTitle,
    string State,
    ProgressCounters Counters,
    IReadOnlyList<PinnedForm> PinnedForms);

public sealed record ClientDirectoryEntry(
    string Id,
    string DisplayLabel,
    int PersonCount,
    int DocumentCount,
    CaseSummary? PrimaryCase,
    int AttentionCount);

public sealed record ClientDirectoryPage(
    IReadOnlyList<ClientDirectoryEntry> Items,
    string? NextCursor);

public sealed record CaseAssignments(
    string? PreparerID,
    string? ReviewerID,
    string? ApproverID);

// The section and evidence element shapes complete when the durable workflow store lands
// (TODO 5.8); until then the fixture serves empty collections, so only the property names on the
// records below are on the wire.
public sealed record FormSection(string Id, string Title, string FormNumber, int Revision);

public sealed record EvidenceRequirementItem(
    string Code,
    string Title,
    string PersonRole,
    IReadOnlyList<string> LinkedDocumentIDs);

public sealed record CaseWorkspace(
    ClientDirectoryEntry Client,
    CaseSummary Summary,
    CaseAssignments Assignments,
    IReadOnlyList<FormSection> Sections,
    IReadOnlyList<EvidenceRequirementItem> Evidence);

// Request fields are nullable because System.Text.Json binds a missing key to null rather than
// failing; the handlers validate and reject before any null can travel further.
public sealed record CreateClientRequest(string? DisplayLabel);

// contracts/openapi/documents-upload.yaml shapes. Field names deliberately match the relay-upload
// schemas in the workflow contract so the generated client models converge.
public sealed record CreateUploadSessionRequest(
    string? FolderId,
    string? SubjectPersonId,
    string? OriginalName,
    string? DeclaredMimeType,
    long SizeBytes,
    string? ContentSha256,
    string? SourceChannel);

public sealed record UploadSession(
    string SessionId,
    string DocumentId,
    Uri UploadUrl,
    string UploadMethod,
    DateTimeOffset ExpiresAt,
    string ExpectedContentSha256);

public sealed record UploadReceipt(
    string SessionId,
    string DocumentId,
    string ContentSha256,
    string ProcessingState);

// MARK: Review, Approval, and Output Models (Phase 6 / INT-06 / INF-11)
public sealed record ReviewQueueItem(
    string ClientLabel,
    CaseSummary CaseSummary,
    int AgeDays,
    int BlockerCount);

public sealed record ReviewDecisionRequest(
    string? Outcome,
    string? Note);

public sealed record ReviewDecision(
    string CaseId,
    string ReviewerId,
    string Outcome,
    string? Note,
    DateTimeOffset DecidedAt);

public sealed record DraftFormPreview(
    string CaseId,
    string Watermark,
    int PageCount,
    string ValueSetHash,
    string EditionSetHash,
    DateTimeOffset ExpiresAt);

public sealed record StepUpChallenge(
    string CaseId,
    string ChallengeToken,
    DateTimeOffset ExpiresAt);

public sealed record CaseApprovalRequest(
    DraftFormPreview? Preview,
    string? StepUpChallenge,
    bool Attested);

public sealed record ApprovalRecord(
    string CaseId,
    string ApproverId,
    string ValueSetHash,
    string EditionSetHash,
    DateTimeOffset AttestedAt,
    bool Valid = true);

public sealed record CaseHistoryEvent(
    Guid Id,
    DateTimeOffset OccurredAt,
    string ActorId,
    string Kind,
    string Summary);

public sealed record CommitSectionRequest(
    int BaseRevision,
    Dictionary<string, string>? Values);

public sealed record SectionCommit(
    FormSection Section,
    bool ReopenedReview,
    bool InvalidatedApproval);

public sealed record VerificationReport(
    bool Passed,
    int FieldsVerified,
    int Mismatches);

public sealed record PreparerAttribution(
    string OrganizationName,
    string VerificationStatus,
    string? VerificationType);

public sealed record PDFOutput(
    string Id,
    string Kind,
    string FillMode,
    string? FormNumber,
    DateTimeOffset? EditionDate,
    int PageCount,
    int SortOrder,
    string? Reason = null);

public sealed record SignaturePoint(
    string FormNumber,
    string PartLabel);

public sealed record FilingChecklist(
    int? FeeUSDCents,
    string? FilingAddress,
    IReadOnlyList<SignaturePoint> WetInkSignaturePoints,
    string? Citation);

public sealed record ScopedDownloadGrant(
    string PackageId,
    string CaseId,
    Uri DownloadUrl,
    DateTimeOffset ExpiresAt,
    string ContentSha256,
    long SizeBytes);

public sealed record GeneratedPackage(
    string Id,
    string CaseId,
    DateTimeOffset GeneratedAt,
    VerificationReport Verification,
    PreparerAttribution Preparer,
    IReadOnlyList<PDFOutput> Outputs,
    FilingChecklist FilingChecklist,
    ScopedDownloadGrant? DownloadGrant = null,
    string? ValuesHash = null,
    string? BlueprintRevisionHash = null,
    string? ApprovalId = null);

public sealed record PackageGenerationReadiness(
    bool CaseStateAllowsGeneration,
    int UnconfirmedRequiredFields,
    int OpenProposals,
    int BlockingDiscrepancies,
    IReadOnlyList<string> FormsWithEditionDrift);

public sealed record PubSubWorkflowEvent(
    string EventId,
    string EventType,
    string CaseId,
    DateTimeOffset OccurredAt,
    string? CorrelationId = null,
    Dictionary<string, object>? Payload = null);

public sealed record WorkflowEventReceipt(
    string EventId,
    string Status,
    DateTimeOffset ProcessedAt,
    string? CorrelationId = null);

