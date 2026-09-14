using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace LaPluma.WorkflowApi;

/// <summary>
/// The in-memory synthetic fixture. Every label is obviously synthetic and content-free.
/// </summary>
public sealed class WorkflowFixtureSource : IWorkflowSource
{
    public const string FixtureCaseId = "case-fixture-0001";
    public const string FixtureFolderId = "folder-fixture-0001";

    public const string FixtureReviewCaseId = "case-fixture-0002";
    public const string FixtureReviewFolderId = "folder-fixture-0002";
    public const string FixtureQueueCaseId = "case-fixture-0003";
    public const string FixtureQueueFolderId = "folder-fixture-0003";

    private static readonly ClientDirectoryEntry SeedClient = new(
        FixtureFolderId,
        "Fixture Client One",
        2,
        3,
        new CaseSummary(
            FixtureCaseId,
            FixtureFolderId,
            "FAMILY_I130",
            "Petition for Alien Relative",
            "COLLECTING",
            new ProgressCounters(12, 48, 3, 9, 2, 1),
            [new PinnedForm(
                "I-130",
                new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero),
                new string('0', 64),
                "ACROFORM",
                false)]),
        2);

    private static readonly ClientDirectoryEntry SeedReviewClient = new(
        FixtureReviewFolderId,
        "Fixture Client Two",
        1,
        2,
        new CaseSummary(
            FixtureReviewCaseId,
            FixtureReviewFolderId,
            "FAMILY_I130",
            "Petition for Alien Relative",
            "IN_REVIEW",
            new ProgressCounters(48, 48, 9, 9, 0, 0),
            [new PinnedForm(
                "I-130",
                new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero),
                new string('0', 64),
                "ACROFORM",
                false)]),
        0);

    private static readonly ClientDirectoryEntry SeedQueueClient = new(
        FixtureQueueFolderId,
        "Fixture Queue Client",
        1,
        1,
        new CaseSummary(
            FixtureQueueCaseId,
            FixtureQueueFolderId,
            "FAMILY_I130",
            "Petition for Alien Relative",
            "IN_REVIEW",
            new ProgressCounters(48, 48, 9, 9, 0, 0),
            [new PinnedForm(
                "I-130",
                new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero),
                new string('0', 64),
                "ACROFORM",
                false)]),
        0);

    private readonly ConcurrentDictionary<string, (string PayloadHash, ClientDirectoryEntry Entry)> created = new();
    private int createdCount;

    private readonly ConcurrentDictionary<string, string> caseStates = new(StringComparer.OrdinalIgnoreCase)
    {
        [FixtureCaseId] = "COLLECTING",
        [FixtureReviewCaseId] = "IN_REVIEW",
        [FixtureQueueCaseId] = "IN_REVIEW"
    };

    private readonly ConcurrentDictionary<string, CaseAssignments> assignments = new(StringComparer.OrdinalIgnoreCase)
    {
        [FixtureCaseId] = new CaseAssignments("user-fixture-preparer", null, null),
        [FixtureReviewCaseId] = new CaseAssignments("user-fixture-preparer", "user-fixture-reviewer", "user-fixture-approver")
    };

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, int>> sectionRevisions = new(StringComparer.OrdinalIgnoreCase)
    {
        [FixtureCaseId] = new(new[] { new KeyValuePair<string, int>("identity", 1) }, StringComparer.OrdinalIgnoreCase),
        [FixtureReviewCaseId] = new(new[] { new KeyValuePair<string, int>("identity", 1) }, StringComparer.OrdinalIgnoreCase)
    };

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Dictionary<string, string>>> sectionValues = new(StringComparer.OrdinalIgnoreCase)
    {
        [FixtureCaseId] = new(new[] { new KeyValuePair<string, Dictionary<string, string>>("identity", new() { ["applicant.name.first"] = "Fixture", ["applicant.name.last"] = "One" }) }, StringComparer.OrdinalIgnoreCase),
        [FixtureReviewCaseId] = new(new[] { new KeyValuePair<string, Dictionary<string, string>>("identity", new() { ["applicant.name.first"] = "Fixture", ["applicant.name.last"] = "Two" }) }, StringComparer.OrdinalIgnoreCase)
    };

    private readonly ConcurrentDictionary<string, ApprovalRecord> approvals = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (string Token, DateTimeOffset ExpiresAt)> challenges = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<ReviewDecision>> reviewDecisions = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<CaseHistoryEvent>> history = new(StringComparer.OrdinalIgnoreCase)
    {
        [FixtureCaseId] = [new CaseHistoryEvent(Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(-2), "system", "CASE_CREATED", "Synthetic case initialized")],
        [FixtureReviewCaseId] = [
            new CaseHistoryEvent(Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(-3), "system", "CASE_CREATED", "Synthetic case initialized"),
            new CaseHistoryEvent(Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(-1), "user-fixture-preparer", "STATE_CHANGED", "Case moved to IN_REVIEW")
        ]
    };

    private readonly ConcurrentDictionary<string, GeneratedPackage> packages = new(StringComparer.OrdinalIgnoreCase);

    public Task<AuthenticatedContext> GetSessionContextAsync(
        string userId, CancellationToken cancellationToken) =>
        Task.FromResult(new AuthenticatedContext(
            userId,
            "FIXTURE-DEMO",
            ["WORKFORCE"],
            ["PREPARER", "REVIEWER", "APPROVER"],
            ["viewClientDirectory", "createClient", "prepareCase", "reviewCase", "approveCase", "generatePackage", "viewProofMap", "runGuidedFinish", "manageEvidenceRelay"],
            true));

    public Task<ClientDirectoryPage> ListClientsAsync(
        string? query, string? cursor, CancellationToken cancellationToken)
    {
        if (cursor is not null)
        {
            return Task.FromResult(new ClientDirectoryPage([], null));
        }

        var entries = new List<ClientDirectoryEntry>
        {
            GetUpdatedClientEntry(SeedClient),
            GetUpdatedClientEntry(SeedReviewClient)
        };
        entries.AddRange(created.Values.Select(value => value.Entry));
        if (!string.IsNullOrWhiteSpace(query))
        {
            entries = entries
                .Where(entry => entry.DisplayLabel.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return Task.FromResult(new ClientDirectoryPage(
            entries.OrderBy(entry => entry.Id, StringComparer.Ordinal).ToArray(), null));
    }

    public Task<CreateClientOutcome> CreateClientAsync(
        string idempotencyKey, CreateClientRequest request, CancellationToken cancellationToken)
    {
        var displayLabel = request.DisplayLabel!;
        var payloadHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(displayLabel)));

        var ordinal = Interlocked.Increment(ref createdCount);
        var candidate = new ClientDirectoryEntry(
            $"folder-fixture-{ordinal + 2:0000}", displayLabel, 1, 0, null, 0);
        var stored = created.GetOrAdd(idempotencyKey, (payloadHash, candidate));
        var isNew = ReferenceEquals(stored.Entry, candidate);

        if (!isNew && !string.Equals(stored.PayloadHash, payloadHash, StringComparison.Ordinal))
        {
            return Task.FromResult(new CreateClientOutcome(IdempotencyOutcome.Conflict, stored.Entry));
        }

        return Task.FromResult(new CreateClientOutcome(
            isNew ? IdempotencyOutcome.Created : IdempotencyOutcome.Replayed, stored.Entry));
    }

    public Task<CaseWorkspace?> GetCaseWorkspaceAsync(string caseId, CancellationToken cancellationToken)
    {
        var client = caseId switch
        {
            FixtureCaseId => SeedClient,
            FixtureReviewCaseId => SeedReviewClient,
            _ => null
        };

        if (client is null)
        {
            return Task.FromResult<CaseWorkspace?>(null);
        }

        var summary = GetUpdatedSummary(client.PrimaryCase!);
        var caseAssignment = assignments.GetValueOrDefault(caseId) ?? new CaseAssignments("user-fixture-preparer", null, null);

        return Task.FromResult<CaseWorkspace?>(new CaseWorkspace(
            GetUpdatedClientEntry(client),
            summary,
            caseAssignment,
            caseId == FixtureCaseId ? [] : [new FormSection("identity", "Identity and contact information", "I-130", sectionRevisions.GetValueOrDefault(caseId)?.GetValueOrDefault("identity") ?? 1)],
            []));
    }

    public Task<IReadOnlyList<ReviewQueueItem>> GetReviewQueueAsync(CancellationToken cancellationToken)
    {
        var reviewableStates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "IN_REVIEW", "READY_FOR_APPROVAL", "CHANGES_REQUESTED", "VALIDATING"
        };

        var allSeeds = new[] { SeedClient, SeedReviewClient, SeedQueueClient };
        var queue = new List<ReviewQueueItem>();

        foreach (var seed in allSeeds)
        {
            if (seed.PrimaryCase is not { } primary) continue;
            var currentState = caseStates.GetValueOrDefault(primary.Id) ?? primary.State;
            if (reviewableStates.Contains(currentState))
            {
                var summary = GetUpdatedSummary(primary);
                queue.Add(new ReviewQueueItem(seed.DisplayLabel, summary, 2, summary.Counters.BlockingItems));
            }
        }

        return Task.FromResult<IReadOnlyList<ReviewQueueItem>>(queue);
    }

    public Task<RecordReviewDecisionOutcome> RecordReviewDecisionAsync(
        string caseId, string reviewerId, ReviewDecisionRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (!caseStates.TryGetValue(caseId, out var state))
        {
            return Task.FromResult(new RecordReviewDecisionOutcome(ReviewDecisionStatus.NotFound));
        }

        if (!string.Equals(state, "IN_REVIEW", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(state, "CHANGES_REQUESTED", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new RecordReviewDecisionOutcome(ReviewDecisionStatus.InvalidState));
        }

        var outcome = request.Outcome?.ToUpperInvariant();
        if (outcome is not ("CHANGES_REQUESTED" or "READY_FOR_APPROVAL"))
        {
            return Task.FromResult(new RecordReviewDecisionOutcome(ReviewDecisionStatus.InvalidState));
        }

        var newState = outcome == "READY_FOR_APPROVAL" ? "READY_FOR_APPROVAL" : "CHANGES_REQUESTED";
        caseStates[caseId] = newState;

        var decision = new ReviewDecision(caseId, reviewerId, outcome, request.Note, DateTimeOffset.UtcNow);
        reviewDecisions.AddOrUpdate(caseId, [decision], (_, list) => { lock (list) { list.Add(decision); } return list; });

        AppendHistory(caseId, reviewerId, "REVIEW_DECIDED", outcome);

        return Task.FromResult(new RecordReviewDecisionOutcome(ReviewDecisionStatus.Success, decision));
    }

    public Task<CreateDraftPreviewOutcome> CreateDraftPreviewAsync(
        string caseId, CancellationToken cancellationToken)
    {
        if (!caseStates.TryGetValue(caseId, out var state))
        {
            return Task.FromResult(new CreateDraftPreviewOutcome(DraftPreviewStatus.NotFound));
        }

        var previewableStates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "IN_REVIEW", "READY_FOR_APPROVAL", "APPROVED", "CHANGES_REQUESTED"
        };

        if (!previewableStates.Contains(state))
        {
            return Task.FromResult(new CreateDraftPreviewOutcome(DraftPreviewStatus.InvalidState));
        }

        var rev = sectionRevisions.GetValueOrDefault(caseId)?.GetValueOrDefault("identity") ?? 1;
        var valueSetHash = ComputeSha256($"values-{caseId}-identity-rev-{rev}");
        var editionSetHash = ComputeSha256("pinned-form-I-130-rev-0");

        var preview = new DraftFormPreview(
            caseId,
            "DRAFT â€” NOT FOR FILING",
            8,
            valueSetHash,
            editionSetHash,
            DateTimeOffset.UtcNow.AddMinutes(10));

        return Task.FromResult(new CreateDraftPreviewOutcome(DraftPreviewStatus.Success, preview));
    }

    public Task<CreateStepUpChallengeOutcome> CreateStepUpChallengeAsync(
        string caseId, string userId, CancellationToken cancellationToken)
    {
        if (!caseStates.ContainsKey(caseId))
        {
            return Task.FromResult(new CreateStepUpChallengeOutcome(StepUpChallengeStatus.NotFound));
        }

        var challengeToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        challenges[caseId] = (challengeToken, expiresAt);

        return Task.FromResult(new CreateStepUpChallengeOutcome(
            StepUpChallengeStatus.Success,
            new StepUpChallenge(caseId, challengeToken, expiresAt)));
    }

    public Task<ApproveCaseOutcome> ApproveCaseAsync(
        string caseId, string approverId, CaseApprovalRequest request, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (!caseStates.TryGetValue(caseId, out var state))
        {
            return Task.FromResult(new ApproveCaseOutcome(ApproveCaseStatus.NotFound));
        }

        if (!request.Attested || string.IsNullOrWhiteSpace(request.StepUpChallenge))
        {
            return Task.FromResult(new ApproveCaseOutcome(ApproveCaseStatus.StepUpRequired));
        }

        if (!challenges.TryGetValue(caseId, out var ch) ||
            !string.Equals(ch.Token, request.StepUpChallenge, StringComparison.Ordinal) ||
            DateTimeOffset.UtcNow > ch.ExpiresAt)
        {
            return Task.FromResult(new ApproveCaseOutcome(ApproveCaseStatus.StepUpRequired));
        }

        if (!string.Equals(state, "READY_FOR_APPROVAL", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new ApproveCaseOutcome(ApproveCaseStatus.InvalidState));
        }

        var assign = assignments.GetValueOrDefault(caseId) ?? new CaseAssignments("user-fixture-preparer", "user-fixture-reviewer", approverId);
        if (!WorkflowPolicy.CanApprove(assign.PreparerID, assign.ReviewerID, approverId))
        {
            return Task.FromResult(new ApproveCaseOutcome(ApproveCaseStatus.SeparationOfDutiesViolation));
        }

        var rev = sectionRevisions.GetValueOrDefault(caseId)?.GetValueOrDefault("identity") ?? 1;
        var currentValuesHash = ComputeSha256($"values-{caseId}-identity-rev-{rev}");
        var currentEditionHash = ComputeSha256("pinned-form-I-130-rev-0");

        if (request.Preview is null ||
            !string.Equals(request.Preview.ValueSetHash, currentValuesHash, StringComparison.Ordinal) ||
            !string.Equals(request.Preview.EditionSetHash, currentEditionHash, StringComparison.Ordinal))
        {
            return Task.FromResult(new ApproveCaseOutcome(ApproveCaseStatus.StalePreview));
        }

        var record = new ApprovalRecord(
            caseId, approverId, currentValuesHash, currentEditionHash, DateTimeOffset.UtcNow, Valid: true);

        approvals[caseId] = record;
        caseStates[caseId] = "APPROVED";
        AppendHistory(caseId, approverId, "APPROVED", "Step-up approval recorded");

        return Task.FromResult(new ApproveCaseOutcome(ApproveCaseStatus.Success, record));
    }

    public Task<IReadOnlyList<CaseHistoryEvent>> GetCaseHistoryAsync(
        string caseId, CancellationToken cancellationToken)
    {
        if (!history.TryGetValue(caseId, out var events))
        {
            return Task.FromResult<IReadOnlyList<CaseHistoryEvent>>([]);
        }

        lock (events)
        {
            return Task.FromResult<IReadOnlyList<CaseHistoryEvent>>(
                events.OrderByDescending(e => e.OccurredAt).ToArray());
        }
    }

    public Task<CommitSectionOutcome> CommitSectionAsync(
        string caseId, string sectionId, int baseRevision, Dictionary<string, string> values, string userId, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (!caseStates.TryGetValue(caseId, out var state))
        {
            return Task.FromResult(new CommitSectionOutcome(CommitSectionStatus.NotFound));
        }

        var revMap = sectionRevisions.GetOrAdd(caseId, _ => new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase));
        var currentRev = revMap.GetValueOrDefault(sectionId, 1);

        if (baseRevision != currentRev)
        {
            return Task.FromResult(new CommitSectionOutcome(CommitSectionStatus.VersionConflict));
        }

        var newRev = baseRevision + 1;
        revMap[sectionId] = newRev;

        var valMap = sectionValues.GetOrAdd(caseId, _ => new ConcurrentDictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase));
        valMap[sectionId] = values;

        var reopen = state is "IN_REVIEW" or "CHANGES_REQUESTED" or "READY_FOR_APPROVAL";
        var invalidate = state is "APPROVED" or "GENERATED";

        if (reopen)
        {
            caseStates[caseId] = "IN_PROGRESS";
        }

        if (invalidate)
        {
            caseStates[caseId] = "IN_PROGRESS";
            if (approvals.TryGetValue(caseId, out var existingApp))
            {
                approvals[caseId] = existingApp with { Valid = false };
            }
            packages.TryRemove(caseId, out _);
        }

        AppendHistory(caseId, userId, "SECTION_COMMITTED", $"Canonical values committed from {sectionId}");

        var formSection = new FormSection(sectionId, "Identity and contact information", "I-130", newRev);
        return Task.FromResult(new CommitSectionOutcome(
            CommitSectionStatus.Success,
            new SectionCommit(formSection, reopen, invalidate)));
    }

    public Task<PackageGenerationOutcome> RequestPackageGenerationAsync(
        string caseId, string userId, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (!caseStates.TryGetValue(caseId, out var state))
        {
            return Task.FromResult(new PackageGenerationOutcome(PackageGenerationStatus.NotFound));
        }

        if (approvals.TryGetValue(caseId, out var app) && !app.Valid)
        {
            return Task.FromResult(new PackageGenerationOutcome(PackageGenerationStatus.ApprovalInvalidated));
        }

        if (!string.Equals(state, "APPROVED", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new PackageGenerationOutcome(PackageGenerationStatus.InvalidState));
        }

        if (app == null || !app.Valid)
        {
            return Task.FromResult(new PackageGenerationOutcome(PackageGenerationStatus.ApprovalInvalidated));
        }

        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(15);
        var downloadUrl = new Uri($"https://storage.googleapis.com/lapluma-documents-pilot/packages/pkg-{caseId}.pdf?X-Goog-Algorithm=GOOG4-RSA-SHA256&X-Goog-Expires=900");
        var grant = new ScopedDownloadGrant(
            $"pkg-{caseId}",
            caseId,
            downloadUrl,
            expiresAt,
            ComputeSha256($"package:pkg-{caseId}"),
            1048576);

        var pkg = new GeneratedPackage(
            $"pkg-{caseId}",
            caseId,
            DateTimeOffset.UtcNow,
            new VerificationReport(true, 12, 0),
            new PreparerAttribution("LaPluma Legal Clinic", "VERIFIED", "ACCREDITED_REPRESENTATIVE"),
            [new PDFOutput($"out-{caseId}-1", "FILLED_FORM", "ACROFORM_FILLED", "I-130", new DateTimeOffset(2026, 8, 2, 0, 0, 0, TimeSpan.Zero), 12, 1)],
            new FilingChecklist(53500, "USCIS Phoenix Lockbox", [new SignaturePoint("I-130", "Part 8. Petitioner's Signature")], "8 CFR 204.1(a)(1)"),
            grant,
            app.ValueSetHash,
            app.EditionSetHash,
            $"app-{caseId}");

        packages[caseId] = pkg;
        caseStates[caseId] = "GENERATED";
        AppendHistory(caseId, userId, "PACKAGE_GENERATED", "Official package generated");

        return Task.FromResult(new PackageGenerationOutcome(PackageGenerationStatus.Success, pkg));
    }

    private readonly ConcurrentDictionary<string, WorkflowEventReceipt> processedEvents = new(StringComparer.OrdinalIgnoreCase);

    public Task<PackageDownloadOutcome> GetPackageDownloadAsync(
        string caseId, string packageId, string userId, CancellationToken cancellationToken)
    {
        if (!caseStates.TryGetValue(caseId, out var state))
        {
            return Task.FromResult(new PackageDownloadOutcome(PackageDownloadStatus.NotFound));
        }

        if (approvals.TryGetValue(caseId, out var app) && !app.Valid)
        {
            return Task.FromResult(new PackageDownloadOutcome(PackageDownloadStatus.ApprovalInvalidated));
        }

        if (!packages.TryGetValue(caseId, out var pkg) || !string.Equals(pkg.Id, packageId, StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new PackageDownloadOutcome(PackageDownloadStatus.NotFound));
        }

        if (state != "GENERATED" && state != "APPROVED" && state != "DELIVERED")
        {
            return Task.FromResult(new PackageDownloadOutcome(PackageDownloadStatus.NotReady));
        }

        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(15);
        var downloadUrl = new Uri($"https://storage.googleapis.com/lapluma-documents-pilot/packages/{packageId}.pdf?X-Goog-Algorithm=GOOG4-RSA-SHA256&X-Goog-Expires=900");
        var grant = new ScopedDownloadGrant(
            packageId,
            caseId,
            downloadUrl,
            expiresAt,
            ComputeSha256($"package:{packageId}"),
            1048576);

        AppendHistory(caseId, userId, "PACKAGE_DOWNLOAD_ISSUED", $"Scoped download grant issued for {packageId}");
        return Task.FromResult(new PackageDownloadOutcome(PackageDownloadStatus.Success, grant));
    }

    public Task<WorkflowEventOutcome> ProcessWorkflowEventAsync(
        PubSubWorkflowEvent workflowEvent, string idempotencyKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workflowEvent.EventId) || string.IsNullOrWhiteSpace(workflowEvent.CaseId))
        {
            return Task.FromResult(new WorkflowEventOutcome(WorkflowEventProcessingStatus.BadPayload));
        }

        // 1. Idempotency & Deduplication check
        if (processedEvents.TryGetValue(workflowEvent.EventId, out var existingReceipt))
        {
            return Task.FromResult(new WorkflowEventOutcome(
                WorkflowEventProcessingStatus.DuplicateIgnored,
                existingReceipt with { Status = "DUPLICATE_IGNORED" }));
        }

        if (!caseStates.TryGetValue(workflowEvent.CaseId, out var currentState))
        {
            currentState = "COLLECTING";
            caseStates[workflowEvent.CaseId] = currentState;
        }

        // 2. State Regression Guard
        var isAdvancedState = currentState is "APPROVED" or "GENERATED" or "DELIVERED";
        if (isAdvancedState && workflowEvent.EventType is "DOCUMENT_EXTRACTED" or "QUARANTINE_PROMOTED")
        {
            var regressionReceipt = new WorkflowEventReceipt(
                workflowEvent.EventId,
                "REGRESSION_PREVENTED",
                DateTimeOffset.UtcNow,
                workflowEvent.CorrelationId);
            processedEvents[workflowEvent.EventId] = regressionReceipt;
            return Task.FromResult(new WorkflowEventOutcome(
                WorkflowEventProcessingStatus.RegressionPrevented,
                regressionReceipt));
        }

        // 3. Process event
        if (workflowEvent.EventType == "PACKAGE_COMPILED")
        {
            caseStates[workflowEvent.CaseId] = "GENERATED";
        }
        else if (workflowEvent.EventType == "QUARANTINE_PROMOTED" && currentState == "COLLECTING")
        {
            caseStates[workflowEvent.CaseId] = "VALIDATING";
        }

        AppendHistory(workflowEvent.CaseId, "system:pubsub", workflowEvent.EventType, $"Workflow event processed: {workflowEvent.EventType}");

        var receipt = new WorkflowEventReceipt(
            workflowEvent.EventId,
            "PROCESSED",
            DateTimeOffset.UtcNow,
            workflowEvent.CorrelationId);
        processedEvents[workflowEvent.EventId] = receipt;

        return Task.FromResult(new WorkflowEventOutcome(WorkflowEventProcessingStatus.Processed, receipt));
    }

    public void SetCaseState(string caseId, string state)
    {
        caseStates[caseId] = state;
    }

    public void SetAssignments(string caseId, CaseAssignments caseAssignments)
    {
        assignments[caseId] = caseAssignments;
    }

    private CaseSummary GetUpdatedSummary(CaseSummary original)
    {
        var currentState = caseStates.GetValueOrDefault(original.Id) ?? original.State;
        return original with { State = currentState };
    }

    private ClientDirectoryEntry GetUpdatedClientEntry(ClientDirectoryEntry original)
    {
        if (original.PrimaryCase is null) return original;
        return original with { PrimaryCase = GetUpdatedSummary(original.PrimaryCase) };
    }

    private void AppendHistory(string caseId, string actorId, string kind, string summary)
    {
        var evt = new CaseHistoryEvent(Guid.NewGuid(), DateTimeOffset.UtcNow, actorId, kind, summary);
        history.AddOrUpdate(caseId, [evt], (_, list) => { lock (list) { list.Add(evt); } return list; });
    }

    private static string ComputeSha256(string input) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
}
