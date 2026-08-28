using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace LaPluma.WorkflowApi;

/// <summary>
/// The in-memory synthetic fixture. Every label is obviously synthetic and content-free — no name,
/// address, or fact a real applicant could have supplied. State lives in this process only, which
/// is one of the reasons max replicas must stay at 1 until the durable store exists (TODO 5.8).
/// </summary>
public sealed class WorkflowFixtureSource : IWorkflowSource
{
    private const string FixtureCaseId = "case-fixture-0001";
    private const string FixtureFolderId = "folder-fixture-0001";

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

    // Keyed by idempotency key: replaying a create returns the entry the first call made, and the
    // payload hash is what turns "same key, different payload" into a diagnosable conflict.
    private readonly ConcurrentDictionary<string, (string PayloadHash, ClientDirectoryEntry Entry)> created = new();
    private int createdCount;

    public Task<AuthenticatedContext> GetSessionContextAsync(
        string userId, CancellationToken cancellationToken) =>
        Task.FromResult(new AuthenticatedContext(
            userId,
            "FIXTURE-DEMO",
            ["WORKFORCE"],
            ["PREPARER"],
            ["viewClientDirectory", "createClient", "prepareCase", "viewProofMap", "runGuidedFinish", "manageEvidenceRelay"],
            true));

    public Task<ClientDirectoryPage> ListClientsAsync(
        string? query, string? cursor, CancellationToken cancellationToken)
    {
        // The fixture is a single page; a cursor is accepted but never issued, so any non-null
        // value is a page that does not exist rather than an error. Paging this surface is not a
        // change this repository can make alone: the app requests the directory with a null cursor
        // and never follows `nextCursor`, so a server that paged would truncate its client list
        // silently. REVIEW.md R-21 carries the ordering that fix needs.
        if (cursor is not null)
        {
            return Task.FromResult(new ClientDirectoryPage([], null));
        }

        var entries = new List<ClientDirectoryEntry> { SeedClient };
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
        // Validated by the handler before the source is called.
        var displayLabel = request.DisplayLabel!;
        var payloadHash = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(displayLabel)));

        // Built before the registration, for the reason spelled out in UploadSessionStore.Create:
        // a ConcurrentDictionary value factory may run on several threads for one key, so deciding
        // "did I create this?" inside it told every racing thread it had won and skipped the
        // payload check for all but one. The value overload takes no factory, and identity decides.
        // The ordinal advances on every call rather than only on a win, so numbers can skip; these
        // ids need to be unique and readable, not dense.
        var ordinal = Interlocked.Increment(ref createdCount);
        var candidate = new ClientDirectoryEntry(
            $"folder-fixture-{ordinal + 1:0000}", displayLabel, 1, 0, null, 0);
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
        if (!string.Equals(caseId, FixtureCaseId, StringComparison.Ordinal))
        {
            return Task.FromResult<CaseWorkspace?>(null);
        }

        return Task.FromResult<CaseWorkspace?>(new CaseWorkspace(
            SeedClient,
            SeedClient.PrimaryCase!,
            new CaseAssignments("user-fixture-preparer", null, null),
            [],
            []));
    }
}
