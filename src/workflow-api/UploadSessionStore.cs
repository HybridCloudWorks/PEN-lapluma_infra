using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LaPluma.WorkflowApi;

public enum CompleteUploadOutcome
{
    Completed,
    Replayed,
    NotFound,
    AlreadyConsumed,
    Expired,
}

public sealed record CompleteUploadResult(CompleteUploadOutcome Outcome, UploadReceipt? Receipt);

/// <summary>
/// Session bookkeeping for direct-to-storage uploads. In-memory, like the fixture — another reason
/// max replicas stays at 1 until the durable store exists (TODO 5.8). Digest verification against
/// the actual blob bytes is TODO 5.9: nothing here reads storage, so completing a session records
/// the declared digest and hands the object to processing rather than proving the bytes match.
/// </summary>
public sealed partial class UploadSessionStore(TimeProvider timeProvider)
{
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long an expired session is kept after it stops being usable. Dropping it the moment it
    /// expires would turn a late completion into a 404, which tells the client its session never
    /// existed rather than that it ran out of time; keeping it forever is what this window exists
    /// to stop. Fifteen minutes is long enough that a retry of a request that timed out still gets
    /// the truthful answer.
    /// </summary>
    public static readonly TimeSpan ExpiredRetention = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    public const long MaximumSizeBytes = 104_857_600;
    public const int MaximumOriginalNameLength = 255;

    [GeneratedRegex("^[a-f0-9]{64}$")]
    public static partial Regex ContentSha256();

    private sealed record Session(
        string SessionId,
        string DocumentId,
        string ExpectedContentSha256,
        DateTimeOffset ExpiresAt,
        string CreateKey)
    {
        public string? ConsumedByKey { get; set; }
    }

    private readonly ConcurrentDictionary<string, Session> sessions = new();
    // Replaying the create with the same key returns the same session (minus a fresh URL mint —
    // the stored expiry still bounds it); a different payload under the same key is a conflict.
    private readonly ConcurrentDictionary<string, (string PayloadHash, string SessionId)> createKeys = new();
    private long lastSweptTicks;

    public static string HashPayload(CreateUploadSessionRequest request) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{request.FolderId}\n{request.OriginalName}\n{request.SizeBytes}\n{request.ContentSha256}")));

    public (IdempotencyOutcome Outcome, string SessionId, string DocumentId, DateTimeOffset ExpiresAt)
        Create(string idempotencyKey, CreateUploadSessionRequest request)
    {
        SweepIfDue();
        var payloadHash = HashPayload(request);

        while (true)
        {
            // Built before the registration rather than inside a GetOrAdd factory.
            // ConcurrentDictionary does not promise a value factory runs once: under contention on
            // one key it may run on several threads and keep a single result. A factory that also
            // decided "did I create this?" by setting a captured flag therefore told every racing
            // thread it had won, which skipped the payload check below for all but one of them —
            // two simultaneous requests sharing a key but carrying different payloads got 201 and
            // the winner's session instead of 409, with the idempotency contract failing under
            // exactly the concurrency it exists to handle. The factory's side effect leaked too:
            // every loser's session stayed in `sessions`, unreachable and never swept. The value
            // overload has no factory, so winning is decided by identity.
            var candidateId = $"upload-{Guid.NewGuid():N}";
            var candidate = new Session(
                candidateId,
                $"doc-{Guid.NewGuid():N}",
                // Validated against the contract's pattern before the store is called.
                request.ContentSha256!,
                timeProvider.GetUtcNow().Add(SessionLifetime),
                idempotencyKey);

            // Registered before the id can be observed through createKeys, so a replay that reads
            // the winner's id always finds the session behind it.
            sessions[candidateId] = candidate;
            var registered = createKeys.GetOrAdd(idempotencyKey, (payloadHash, candidateId));

            if (string.Equals(registered.SessionId, candidateId, StringComparison.Ordinal))
            {
                return (IdempotencyOutcome.Created, candidate.SessionId, candidate.DocumentId, candidate.ExpiresAt);
            }

            // Lost the race, or this is an ordinary replay. Either way the candidate is unused.
            sessions.TryRemove(candidateId, out _);
            if (!string.Equals(registered.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                return (IdempotencyOutcome.Conflict, registered.SessionId, "", default);
            }

            if (sessions.TryGetValue(registered.SessionId, out var stored))
            {
                return (IdempotencyOutcome.Replayed, stored.SessionId, stored.DocumentId, stored.ExpiresAt);
            }

            // The sweep removed the session this key pointed at between the two reads above.
            // Retiring the key is what the sweep was in the middle of doing, so finish it and
            // start over: the retry then registers a fresh session, which is the right answer for
            // a key whose session has aged out. The compare-and-remove overload means a key some
            // other thread has already re-registered is left alone, so the loop cannot spin —
            // every pass either returns or removes one mapping that only a sweep can recreate.
            createKeys.TryRemove(
                new KeyValuePair<string, (string, string)>(idempotencyKey, registered));
        }
    }

    public CompleteUploadResult Complete(string sessionId, string idempotencyKey)
    {
        if (!sessions.TryGetValue(sessionId, out var session))
        {
            return new(CompleteUploadOutcome.NotFound, null);
        }

        if (timeProvider.GetUtcNow() > session.ExpiresAt)
        {
            return new(CompleteUploadOutcome.Expired, null);
        }

        lock (session)
        {
            if (session.ConsumedByKey is { } consumedBy)
            {
                // The same key completing again is the retry the idempotency contract promises to
                // absorb; a different key is a second caller consuming someone else's session.
                return string.Equals(consumedBy, idempotencyKey, StringComparison.Ordinal)
                    ? new(CompleteUploadOutcome.Replayed, Receipt(session))
                    : new(CompleteUploadOutcome.AlreadyConsumed, null);
            }

            session.ConsumedByKey = idempotencyKey;
            return new(CompleteUploadOutcome.Completed, Receipt(session));
        }
    }

    /// <summary>
    /// Drops sessions that expired longer ago than <see cref="ExpiredRetention"/>, along with the
    /// idempotency keys that point at them. Without this nothing is ever removed: every session
    /// this process has issued — completed, abandoned, or long dead — stays in memory for the life
    /// of the container, on a service pinned to a single replica precisely because its state is in
    /// memory. Public so a test can run it against a controlled clock rather than inferring it.
    /// </summary>
    public void SweepExpired()
    {
        var horizon = timeProvider.GetUtcNow() - ExpiredRetention;
        foreach (var (sessionId, session) in sessions)
        {
            if (session.ExpiresAt > horizon)
            {
                continue;
            }

            // Key first, so a create that reads the key still finds the session behind it; the
            // ordering the other way would hand a caller an id that had already been removed.
            // Both removals compare before deleting, so a key or session replaced since this loop
            // read it belongs to a newer create and survives.
            if (createKeys.TryGetValue(session.CreateKey, out var registered)
                && string.Equals(registered.SessionId, sessionId, StringComparison.Ordinal))
            {
                createKeys.TryRemove(
                    new KeyValuePair<string, (string, string)>(session.CreateKey, registered));
            }

            sessions.TryRemove(new KeyValuePair<string, Session>(sessionId, session));
        }
    }

    /// <summary>
    /// Sweeps at most once per <see cref="SweepInterval"/>, driven by creates rather than by a
    /// timer: creates are the only thing that adds to either map, so a store nobody is writing to
    /// is a store that is not growing and does not need collecting.
    /// </summary>
    private void SweepIfDue()
    {
        var nowTicks = timeProvider.GetUtcNow().UtcTicks;
        var last = Interlocked.Read(ref lastSweptTicks);
        if (nowTicks - last < SweepInterval.Ticks)
        {
            return;
        }

        // One winner sweeps; the rest carry on with their create rather than queueing behind it.
        if (Interlocked.CompareExchange(ref lastSweptTicks, nowTicks, last) == last)
        {
            SweepExpired();
        }
    }

    private static UploadReceipt Receipt(Session session) =>
        new(session.SessionId, session.DocumentId, session.ExpectedContentSha256, "SCANNING");
}
