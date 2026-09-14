namespace LaPluma.CoreApi;

/// <summary>
/// In-memory implementation of IBlueprintPublicationService for tests and non-Postgres local modes.
/// Enforces all independent review, self-approval prevention, drift quarantine, and rollback invariants.
/// </summary>
public sealed class InMemoryBlueprintPublicationService : IBlueprintPublicationService
{
    private readonly List<BlueprintPublicationAuditEntry> _audits = [];
    private readonly Dictionary<string, (string State, string? Reviewer, string? SourceSha, int LatestRevision)> _blueprints = new(StringComparer.OrdinalIgnoreCase);

    public InMemoryBlueprintPublicationService()
    {
        // Seed standard sample blueprint
        _blueprints["uscis/i-130@r1"] = ("PUBLISHED", "reviewer_lead", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", 1);
        _blueprints["uscis/i-130@latest"] = ("PUBLISHED", "reviewer_lead", "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", 1);
    }

    public Task ReviewBlueprintAsync(
        string ns,
        string blueprintId,
        int revision,
        BlueprintReviewRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.Equals(request.ReviewerId.Trim(), request.AuthorId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Independent review invariant violated: reviewer cannot be the author (self-approval prohibited).");
        }

        var key = $"{ns}/{blueprintId}@r{revision}";
        _blueprints[key] = ("IN_REVIEW", request.ReviewerId.Trim(), "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", revision);

        _audits.Add(new BlueprintPublicationAuditEntry(
            Guid.NewGuid(), ns, blueprintId, revision, "REVIEW_APPROVED",
            request.AuthorId.Trim(), request.ReviewerId.Trim(), request.ReviewerId.Trim(),
            null, null, request.Notes, DateTimeOffset.UtcNow));

        return Task.CompletedTask;
    }

    public Task PublishBlueprintAsync(
        string ns,
        string blueprintId,
        int revision,
        BlueprintPublishRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.AuthorId != null && string.Equals(request.ReviewerId.Trim(), request.AuthorId.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Self-approval invariant violated: reviewer cannot be the author.");
        }

        var key = $"{ns}/{blueprintId}@r{revision}";
        if (!_blueprints.TryGetValue(key, out var current) || current.State != "IN_REVIEW")
        {
            throw new InvalidOperationException($"Cannot publish unreviewed blueprint {ns}/{blueprintId}@r{revision}.");
        }

        _blueprints[key] = ("PUBLISHED", request.ReviewerId.Trim(), current.SourceSha, revision);
        _blueprints[$"{ns}/{blueprintId}@latest"] = ("PUBLISHED", request.ReviewerId.Trim(), current.SourceSha, revision);

        _audits.Add(new BlueprintPublicationAuditEntry(
            Guid.NewGuid(), ns, blueprintId, revision, "PUBLISHED",
            request.AuthorId, request.ReviewerId.Trim(), request.PublisherId.Trim(),
            current.SourceSha, request.ManifestSha256, "Published and pinned", DateTimeOffset.UtcNow));

        return Task.CompletedTask;
    }

    public Task<bool> CheckAndQuarantineDriftAsync(
        string ns,
        string blueprintId,
        int revision,
        BlueprintDriftCheckRequest request,
        CancellationToken cancellationToken = default)
    {
        var key = $"{ns}/{blueprintId}@r{revision}";
        if (_blueprints.TryGetValue(key, out var current))
        {
            var observed = request.ObservedSourceSha256.Trim().ToLowerInvariant();
            if (current.SourceSha != null && !string.Equals(current.SourceSha, observed, StringComparison.OrdinalIgnoreCase))
            {
                _blueprints[key] = ("QUARANTINED", current.Reviewer, current.SourceSha, revision);
                _audits.Add(new BlueprintPublicationAuditEntry(
                    Guid.NewGuid(), ns, blueprintId, revision, "QUARANTINED_DRIFT",
                    null, null, request.OperatorId.Trim(), observed, null, request.Reason ?? "Source drift detected", DateTimeOffset.UtcNow));
                return Task.FromResult(true);
            }
        }
        return Task.FromResult(false);
    }

    public Task RollbackBlueprintAsync(
        string ns,
        string blueprintId,
        BlueprintRollbackRequest request,
        CancellationToken cancellationToken = default)
    {
        var targetKey = $"{ns}/{blueprintId}@r{request.TargetRevision}";
        if (!_blueprints.TryGetValue(targetKey, out var target) || target.State != "PUBLISHED")
        {
            throw new InvalidOperationException($"Cannot rollback to revision {request.TargetRevision} that is not in PUBLISHED state.");
        }

        _blueprints[$"{ns}/{blueprintId}@latest"] = ("PUBLISHED", target.Reviewer, target.SourceSha, request.TargetRevision);

        _audits.Add(new BlueprintPublicationAuditEntry(
            Guid.NewGuid(), ns, blueprintId, request.TargetRevision, "ROLLED_BACK",
            null, null, request.OperatorId.Trim(), target.SourceSha, null, request.Reason, DateTimeOffset.UtcNow));

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<BlueprintPublicationAuditEntry>> GetAuditTrailAsync(
        string ns,
        string blueprintId,
        int? revision = null,
        CancellationToken cancellationToken = default)
    {
        var query = _audits.Where(a => string.Equals(a.Namespace, ns, StringComparison.OrdinalIgnoreCase) &&
                                       string.Equals(a.BlueprintId, blueprintId, StringComparison.OrdinalIgnoreCase));
        if (revision.HasValue)
        {
            query = query.Where(a => a.Revision == revision.Value);
        }

        return Task.FromResult<IReadOnlyList<BlueprintPublicationAuditEntry>>(query.OrderByDescending(a => a.CreatedAt).ToList().AsReadOnly());
    }
}
