namespace LaPluma.CoreApi;

/// <summary>
/// Service interface for Blueprint publication lifecycle, independent review,
/// edition drift detection, and rollback (INF-04).
/// </summary>
public interface IBlueprintPublicationService
{
    Task ReviewBlueprintAsync(
        string ns,
        string blueprintId,
        int revision,
        BlueprintReviewRequest request,
        CancellationToken cancellationToken = default);

    Task PublishBlueprintAsync(
        string ns,
        string blueprintId,
        int revision,
        BlueprintPublishRequest request,
        CancellationToken cancellationToken = default);

    Task<bool> CheckAndQuarantineDriftAsync(
        string ns,
        string blueprintId,
        int revision,
        BlueprintDriftCheckRequest request,
        CancellationToken cancellationToken = default);

    Task RollbackBlueprintAsync(
        string ns,
        string blueprintId,
        BlueprintRollbackRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BlueprintPublicationAuditEntry>> GetAuditTrailAsync(
        string ns,
        string blueprintId,
        int? revision = null,
        CancellationToken cancellationToken = default);
}
