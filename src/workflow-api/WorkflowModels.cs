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
