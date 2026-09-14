using Xunit;

namespace LaPluma.CoreApi.Tests;

public sealed class BlueprintPublicationLifecycleTests
{
    private readonly InMemoryBlueprintPublicationService _service = new();

    [Fact]
    public async Task ReviewBlueprint_RejectsSelfApproval()
    {
        var request = new BlueprintReviewRequest(
            ReviewerId: "author_alice",
            AuthorId: "author_alice",
            Notes: "Attempting self-approval");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.ReviewBlueprintAsync("uscis", "i-130", 2, request));

        Assert.Contains("self-approval prohibited", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReviewBlueprint_SucceedsWithIndependentReviewer()
    {
        var request = new BlueprintReviewRequest(
            ReviewerId: "reviewer_bob",
            AuthorId: "author_alice",
            Notes: "Independent legal and schema verification passed");

        await _service.ReviewBlueprintAsync("uscis", "i-130", 2, request);

        var audit = await _service.GetAuditTrailAsync("uscis", "i-130", 2);
        Assert.NotEmpty(audit);
        Assert.Equal("REVIEW_APPROVED", audit[0].Action);
        Assert.Equal("reviewer_bob", audit[0].ReviewerId);
        Assert.Equal("author_alice", audit[0].AuthorId);
    }

    [Fact]
    public async Task PublishBlueprint_RejectsSelfApproval()
    {
        var request = new BlueprintPublishRequest(
            PublisherId: "lead_charlie",
            ReviewerId: "author_alice",
            AuthorId: "author_alice",
            ManifestSha256: "229b699b7c23986f0a38776c54a1f90afbdae8ca65e6af0613c3937087c055a5");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.PublishBlueprintAsync("uscis", "i-130", 2, request));

        Assert.Contains("Self-approval invariant violated", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublishBlueprint_RejectsUnreviewedBlueprint()
    {
        var request = new BlueprintPublishRequest(
            PublisherId: "lead_charlie",
            ReviewerId: "reviewer_bob",
            AuthorId: "author_alice",
            ManifestSha256: "229b699b7c23986f0a38776c54a1f90afbdae8ca65e6af0613c3937087c055a5");

        // Revision 99 was never submitted for review
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _service.PublishBlueprintAsync("uscis", "i-130", 99, request));

        Assert.Contains("Cannot publish unreviewed blueprint", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PublishBlueprint_SucceedsAfterIndependentReview()
    {
        // 1. Independent Review
        await _service.ReviewBlueprintAsync("uscis", "i-130", 2, new BlueprintReviewRequest("reviewer_bob", "author_alice"));

        // 2. Publish
        var pubRequest = new BlueprintPublishRequest(
            PublisherId: "lead_charlie",
            ReviewerId: "reviewer_bob",
            AuthorId: "author_alice",
            ManifestSha256: "229b699b7c23986f0a38776c54a1f90afbdae8ca65e6af0613c3937087c055a5");

        await _service.PublishBlueprintAsync("uscis", "i-130", 2, pubRequest);

        var audit = await _service.GetAuditTrailAsync("uscis", "i-130", 2);
        Assert.Contains(audit, a => a.Action == "PUBLISHED" && a.ManifestSha256 == "229b699b7c23986f0a38776c54a1f90afbdae8ca65e6af0613c3937087c055a5");
    }

    [Fact]
    public async Task CheckDrift_QuarantinesWhenOfficialSourceHashDiffers()
    {
        // Matching hash does not quarantine
        var matchResult = await _service.CheckAndQuarantineDriftAsync("uscis", "i-130", 1, new BlueprintDriftCheckRequest(
            ObservedSourceSha256: "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            OperatorId: "drift_monitor"));
        Assert.False(matchResult);

        // Drifted hash triggers quarantine
        var driftResult = await _service.CheckAndQuarantineDriftAsync("uscis", "i-130", 1, new BlueprintDriftCheckRequest(
            ObservedSourceSha256: "1111111111111111111111111111111111111111111111111111111111111111",
            OperatorId: "drift_monitor",
            Reason: "Official USCIS PDF changed upstream"));
        Assert.True(driftResult);

        var audit = await _service.GetAuditTrailAsync("uscis", "i-130", 1);
        Assert.Contains(audit, a => a.Action == "QUARANTINED_DRIFT");
    }

    [Fact]
    public async Task RollbackBlueprint_PromotesTargetRevisionWhilePreservingHistory()
    {
        var rollbackRequest = new BlueprintRollbackRequest(
            TargetRevision: 1,
            OperatorId: "lead_operator",
            Reason: "Hotfix rollback to r1");

        await _service.RollbackBlueprintAsync("uscis", "i-130", rollbackRequest);

        var audit = await _service.GetAuditTrailAsync("uscis", "i-130", 1);
        Assert.Contains(audit, a => a.Action == "ROLLED_BACK" && a.Reason == "Hotfix rollback to r1");
    }
}
