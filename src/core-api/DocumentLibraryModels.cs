using System.Text.Json;
using System.Text.Json.Serialization;

namespace LaPluma.CoreApi;

[JsonConverter(typeof(JsonStringEnumConverter<PreparationMode>))]
public enum PreparationMode
{
    [JsonStringEnumMemberName("FILLABLE_PDF")]
    FillablePdf,
    [JsonStringEnumMemberName("STATIC_ASSISTED")]
    StaticAssisted,
    [JsonStringEnumMemberName("EXTERNAL_REFERENCE")]
    ExternalReference
}

[JsonConverter(typeof(JsonStringEnumConverter<PublicationState>))]
public enum PublicationState
{
    [JsonStringEnumMemberName("DRAFT")]
    Draft,
    [JsonStringEnumMemberName("VALIDATED")]
    Validated,
    [JsonStringEnumMemberName("IN_REVIEW")]
    InReview,
    [JsonStringEnumMemberName("PUBLISHED")]
    Published,
    [JsonStringEnumMemberName("QUARANTINED")]
    Quarantined,
    [JsonStringEnumMemberName("WITHDRAWN")]
    Withdrawn,
    [JsonStringEnumMemberName("ROLLED_BACK")]
    RolledBack
}

[JsonConverter(typeof(JsonStringEnumConverter<BlueprintArtifactType>))]
public enum BlueprintArtifactType
{
    [JsonStringEnumMemberName("OFFICIAL_PDF")]
    OfficialPdf,
    [JsonStringEnumMemberName("XFA")]
    Xfa,
    [JsonStringEnumMemberName("FLAT")]
    Flat,
    [JsonStringEnumMemberName("EXTERNAL_LINK")]
    ExternalLink,
    [JsonStringEnumMemberName("AUTHORED_TEMPLATE")]
    AuthoredTemplate
}

/// <summary>
/// Immutable, versioned Document Blueprint definition.
/// Identity: (Namespace, BlueprintId, Revision).
/// </summary>
public sealed record DocumentBlueprint(
    string Namespace,
    string BlueprintId,
    int Revision,
    string Title,
    string Issuer,
    DateOnly? OfficialEditionDate,
    PreparationMode PreparationMode,
    BlueprintArtifactType ArtifactType,
    Uri? SourceUrl,
    string? SourceSha256,
    JsonDocument FieldsSchema,
    JsonDocument ValidationRules,
    JsonDocument EvidenceRequirements,
    PublicationState PublicationState,
    string? ReviewedBy,
    DateTimeOffset? ReviewedAt,
    DateTimeOffset? PublishedAt,
    bool IsLatest,
    DateTimeOffset CreatedAt);

/// <summary>
/// Versioned collection of Blueprints forming a cohesive package or workflow.
/// </summary>
public sealed record DocumentCollection(
    string Namespace,
    string CollectionId,
    int Revision,
    string Title,
    string? Description,
    JsonDocument WorkflowSettings,
    PublicationState PublicationState,
    bool IsLatest,
    DateTimeOffset CreatedAt,
    IReadOnlyList<CollectionMember> Members);

public sealed record CollectionMember(
    string BlueprintNamespace,
    string BlueprintId,
    int BlueprintRevision,
    int SortOrder,
    bool IsRequired);

public sealed record InstitutionTenant(
    string TenantId,
    string DisplayName,
    bool IsActive,
    DateTimeOffset CreatedAt);

public sealed record BlueprintReviewRequest(
    string ReviewerId,
    string AuthorId,
    string? Notes = null);

public sealed record BlueprintPublishRequest(
    string PublisherId,
    string ReviewerId,
    string? AuthorId,
    string ManifestSha256);

public sealed record BlueprintDriftCheckRequest(
    string ObservedSourceSha256,
    string OperatorId,
    string? Reason = null);

public sealed record BlueprintRollbackRequest(
    int TargetRevision,
    string OperatorId,
    string Reason);

public sealed record BlueprintPublicationAuditEntry(
    Guid AuditId,
    string Namespace,
    string BlueprintId,
    int Revision,
    string Action,
    string? AuthorId,
    string? ReviewerId,
    string OperatorId,
    string? SourceSha256,
    string? ManifestSha256,
    string? Reason,
    DateTimeOffset CreatedAt);

/// <summary>
/// Source-cited, revision-bound official guidance, evidence requirements, and fee references (INF-09).
/// </summary>
public sealed record DocumentGuidance(
    [property: JsonPropertyName("formId")] string FormId,
    [property: JsonPropertyName("formNumber")] string FormNumber,
    [property: JsonPropertyName("authority")] string Authority,
    [property: JsonPropertyName("officialInstructionsUrl")] Uri OfficialInstructionsUrl,
    [property: JsonPropertyName("feeScheduleCitationUrl")] Uri FeeScheduleCitationUrl,
    [property: JsonPropertyName("feeUsdCents")] int? FeeUsdCents,
    [property: JsonPropertyName("feeNotes")] string? FeeNotes,
    [property: JsonPropertyName("evidenceChecklist")] IReadOnlyList<string> EvidenceChecklist,
    [property: JsonPropertyName("institutionGuidanceNotes")] string? InstitutionalGuidanceNotes);

