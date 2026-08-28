using System.Collections.Concurrent;
using Xunit;

namespace LaPluma.WorkflowApi.Tests;

/// <summary>
/// The idempotency stores under simultaneous arrival on one key.
///
/// This is the case the contract exists for rather than an exotic one: the iOS client holds an
/// offline mutation queue and replays it on reconnect, so a burst of identical keys is the normal
/// reconnect shape, and a single replica is no defence — one process serves those requests on many
/// threads at once.
///
/// Both stores previously decided "did I create this?" inside a ConcurrentDictionary value factory.
/// That factory carries no once-only guarantee: under contention on one key it may run on several
/// threads with a single result kept, so every racing thread believed it had won and skipped the
/// payload comparison that turns a reused key into a 409.
/// </summary>
public sealed class IdempotencyConcurrencyTests
{
    private const int Racers = 64;

    private static CreateUploadSessionRequest UploadRequest(string name) => new(
        "folder-fixture-0001", null, name, null, 2048,
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef", null);

    /// <summary>
    /// Release every caller at once, so the calls genuinely overlap.
    ///
    /// Dedicated threads rather than the thread pool: a barrier only falls when every participant
    /// has arrived, and the pool injects threads a couple per second past its core count, so a
    /// pool-backed barrier of this width spends most of a minute waiting to start racing.
    /// </summary>
    private static T[] Race<T>(Func<int, T> body)
    {
        using var gate = new Barrier(Racers);
        var results = new T[Racers];
        var threads = Enumerable.Range(0, Racers).Select(index =>
        {
            var thread = new Thread(() =>
            {
                gate.SignalAndWait();
                results[index] = body(index);
            });
            thread.IsBackground = true;
            thread.Start();
            return thread;
        }).ToArray();

        foreach (var thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "a racing thread did not finish");
        }

        return results;
    }

    [Fact]
    public void One_upload_key_with_differing_payloads_yields_one_creation_and_the_rest_conflict()
    {
        var store = new UploadSessionStore(TimeProvider.System);
        var key = Guid.NewGuid().ToString();

        // Each racer sends a different payload under the same key, so every caller except the one
        // that registers the key must be told 409 rather than handed the winner's session.
        var outcomes = Race(index => store.Create(key, UploadRequest($"file-{index}.pdf")).Outcome);

        Assert.Equal(1, outcomes.Count(outcome => outcome == IdempotencyOutcome.Created));
        Assert.Equal(Racers - 1, outcomes.Count(outcome => outcome == IdempotencyOutcome.Conflict));
    }

    [Fact]
    public void One_upload_key_with_the_same_payload_returns_one_session_to_every_caller()
    {
        var store = new UploadSessionStore(TimeProvider.System);
        var key = Guid.NewGuid().ToString();

        var results = Race(_ => store.Create(key, UploadRequest("same.pdf")));

        Assert.Equal(1, results.Count(result => result.Outcome == IdempotencyOutcome.Created));
        Assert.Single(results.Select(result => result.SessionId).Distinct());
        // Every caller is handed a session that resolves, which is what the orphaned candidates
        // from the old factory side effect made unreliable.
        foreach (var sessionId in results.Select(result => result.SessionId))
        {
            Assert.NotEqual(
                CompleteUploadOutcome.NotFound,
                store.Complete(sessionId, Guid.NewGuid().ToString()).Outcome);
        }
    }

    [Fact]
    public void A_lost_race_leaves_no_unreachable_session_behind()
    {
        // The old factory wrote its session into the store as a side effect, so every loser left
        // one that nothing could reach and nothing sweeps. Only the winner's session may exist, so
        // exactly one of the ids ever minted is completable.
        var store = new UploadSessionStore(TimeProvider.System);
        var key = Guid.NewGuid().ToString();

        var results = Race(index => store.Create(key, UploadRequest($"file-{index}.pdf")));
        var reachable = results
            .Select(result => result.SessionId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Distinct()
            .Count(id => store.Complete(id, Guid.NewGuid().ToString()).Outcome != CompleteUploadOutcome.NotFound);

        Assert.Equal(1, reachable);
    }

    [Fact]
    public void One_client_key_with_differing_payloads_yields_one_creation_and_the_rest_conflict()
    {
        var source = new WorkflowFixtureSource();
        var key = Guid.NewGuid().ToString();
        var outcomes = new ConcurrentBag<IdempotencyOutcome>();

        Race(index =>
        {
            outcomes.Add(source
                .CreateClientAsync(key, new CreateClientRequest($"Racer {index}"), CancellationToken.None)
                .GetAwaiter().GetResult().Outcome);
            return index;
        });

        Assert.Equal(1, outcomes.Count(outcome => outcome == IdempotencyOutcome.Created));
        Assert.Equal(Racers - 1, outcomes.Count(outcome => outcome == IdempotencyOutcome.Conflict));
    }

    [Fact]
    public async Task A_racing_client_create_publishes_exactly_one_directory_entry()
    {
        // A directory that grew one row per racing thread would be the same bug seen from the
        // read side, and the client's reconnect replay is what would produce the duplicates.
        var source = new WorkflowFixtureSource();
        var key = Guid.NewGuid().ToString();

        Race(_ =>
            source.CreateClientAsync(key, new CreateClientRequest("Same Label"), CancellationToken.None)
                .GetAwaiter().GetResult());

        var page = await source.ListClientsAsync(null, null, CancellationToken.None);
        Assert.Equal(1, page.Items.Count(entry => entry.DisplayLabel == "Same Label"));
    }
}
