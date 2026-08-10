using System.Text.Json.Serialization;

namespace LaPluma.CoreApi;

public sealed record HealthResponse(string Status, string Service, string Version);

public sealed record CatalogCategory(string Code, string Title, int SortOrder);

public sealed record CatalogSubcategory(
    string Code,
    string CategoryCode,
    string Title,
    int SortOrder);

public sealed record CatalogCategoryNode(
    CatalogCategory Category,
    IReadOnlyList<CatalogSubcategory> Subcategories);

public sealed record CatalogHierarchyResponse(IReadOnlyList<CatalogCategoryNode> Data);

public sealed record FormPackageListResponse(IReadOnlyList<FormPackage> Data);

[JsonConverter(typeof(JsonStringEnumConverter<FormArtifactKind>))]
public enum FormArtifactKind
{
    [JsonStringEnumMemberName("OFFICIAL_PDF")]
    OfficialPdf,
    [JsonStringEnumMemberName("EXTERNAL_WORKFLOW")]
    ExternalWorkflow,
    [JsonStringEnumMemberName("PROPRIETARY_FORM")]
    ProprietaryForm,
    [JsonStringEnumMemberName("AUTHORED_TEMPLATE")]
    AuthoredTemplate
}

[JsonConverter(typeof(JsonStringEnumConverter<FormFillCapability>))]
public enum FormFillCapability
{
    [JsonStringEnumMemberName("AUTOMATIC_FILL")]
    AutomaticFill,
    [JsonStringEnumMemberName("ASSISTED_PREPARATION")]
    AssistedPreparation,
    [JsonStringEnumMemberName("REFERENCE_ONLY")]
    ReferenceOnly
}

[JsonConverter(typeof(JsonStringEnumConverter<FormActivationState>))]
public enum FormActivationState
{
    [JsonStringEnumMemberName("UNAVAILABLE")]
    Unavailable,
    [JsonStringEnumMemberName("CATALOG_ONLY")]
    CatalogOnly,
    [JsonStringEnumMemberName("ASSISTED")]
    Assisted,
    [JsonStringEnumMemberName("PILOT")]
    Pilot
}

[JsonConverter(typeof(JsonStringEnumConverter<FormEncoding>))]
public enum FormEncoding
{
    [JsonStringEnumMemberName("ACROFORM")]
    AcroForm,
    [JsonStringEnumMemberName("XFA")]
    Xfa,
    [JsonStringEnumMemberName("FLAT")]
    Flat
}

[JsonConverter(typeof(JsonStringEnumConverter<ExtractedFieldValueType>))]
public enum ExtractedFieldValueType
{
    [JsonStringEnumMemberName("TEXT")]
    Text,
    [JsonStringEnumMemberName("DATE")]
    Date,
    [JsonStringEnumMemberName("BOOLEAN")]
    Boolean,
    [JsonStringEnumMemberName("CHOICE")]
    Choice,
    [JsonStringEnumMemberName("SIGNATURE")]
    Signature
}

public sealed record FormSourceMetadata(
    string IssuingAuthority,
    [property: JsonPropertyName("sourcePageURL")] Uri SourcePageUrl,
    [property: JsonPropertyName("artifactURL")] Uri? ArtifactUrl,
    string OfficialDomain,
    string? Sha256,
    DateTimeOffset LastVerified);

public sealed record CatalogForm(
    string FormNumber,
    string Title,
    DateOnly EditionDate,
    FormEncoding Encoding,
    int PageCount,
    FormArtifactKind ArtifactKind,
    FormFillCapability FillCapability,
    FormActivationState ActivationState,
    FormSourceMetadata? Source);

public sealed record FormPackage(
    string PackageCode,
    string Title,
    CatalogCategory Category,
    CatalogSubcategory Subcategory,
    string Agency,
    string? AgencyCategoryLabel,
    IReadOnlyList<CatalogForm> Forms,
    [property: JsonPropertyName("feeUSDCents")] int? FeeUsdCents,
    [property: JsonPropertyName("feeCitationURL")] Uri? FeeCitationUrl,
    [property: JsonPropertyName("sourceURL")] Uri SourceUrl,
    DateTimeOffset LastVerified)
{
    [JsonIgnore]
    public FormActivationState ActivationState =>
        // A package is only as activated as its weakest form, so a package with no forms is
        // UNAVAILABLE. Without this branch every Any() below is false and the expression falls
        // through to PILOT — the most permissive state, and one that permits case creation.
        Forms.Count == 0 ? FormActivationState.Unavailable :
        Forms.Any(form => form.ActivationState == FormActivationState.Unavailable) ? FormActivationState.Unavailable :
        Forms.Any(form => form.ActivationState == FormActivationState.CatalogOnly) ? FormActivationState.CatalogOnly :
        Forms.Any(form => form.ActivationState == FormActivationState.Assisted) ? FormActivationState.Assisted :
        FormActivationState.Pilot;
}

public sealed record FormEditionId(
    string Authority,
    [property: JsonPropertyName("formID")] string FormId,
    DateOnly EditionDate);

public sealed record FieldBounds(int Page, decimal X, decimal Y, decimal Width, decimal Height);

public sealed record ExtractedFieldManifest(
    string FieldName,
    string Label,
    ExtractedFieldValueType ValueType,
    bool Required,
    decimal Confidence,
    FieldBounds? Bounds);

public sealed record ExtractedFormSchema(
    FormEditionId Edition,
    string SchemaVersion,
    IReadOnlyList<ExtractedFieldManifest> Fields);

public sealed record ProblemDetailsResponse(
    string Type,
    string Title,
    int Status,
    string? Detail,
    Guid CorrelationId);
