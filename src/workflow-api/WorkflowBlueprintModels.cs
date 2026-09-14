using System.Text.Json;

namespace LaPluma.WorkflowApi;

/// <summary>
/// Immutable blueprint pinned to a specific case, protected from edition drift.
/// </summary>
public sealed record CasePinnedBlueprint(
    string CaseId,
    string BlueprintNamespace,
    string BlueprintId,
    int PinnedBlueprintRevision,
    bool DriftDetected,
    DateTimeOffset? DriftQuarantinedAt);

/// <summary>
/// Canonical case field value with human confirmation and extraction provenance.
/// </summary>
public sealed record CaseFieldValue(
    string CaseId,
    string CanonicalPath,
    string? AttributedPersonId,
    string? RawValue,
    string? ConfirmedValue,
    bool IsHumanConfirmed,
    string? ConfirmedByUserId,
    DateTimeOffset? ConfirmedAt,
    decimal? ConfidenceScore,
    string SourceKind,
    string? SourceDocumentId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// Audit-logged case approval record with invalidation tracking.
/// </summary>
public sealed record CaseApproval(
    Guid Id,
    string CaseId,
    string ApprovedByUserId,
    string ApprovalKind,
    DateTimeOffset ApprovedAt,
    bool IsInvalidated,
    DateTimeOffset? InvalidatedAt,
    string? InvalidationReason);

/// <summary>
/// Transactional outbox event record for reliable Pub/Sub dispatch.
/// </summary>
public sealed record OutboxEvent(
    Guid Id,
    string AggregateType,
    string AggregateId,
    string EventType,
    JsonDocument Payload,
    bool Published,
    DateTimeOffset? PublishedAt,
    DateTimeOffset CreatedAt);
