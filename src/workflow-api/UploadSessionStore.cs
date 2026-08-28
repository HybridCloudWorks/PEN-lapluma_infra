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

    public static string HashPayload(CreateUploadSessionRequest request) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{request.FolderId}\n{request.OriginalName}\n{request.SizeBytes}\n{request.ContentSha256}")));

    public (IdempotencyOutcome Outcome, string SessionId, string DocumentId, DateTimeOffset ExpiresAt)
        Create(string idempotencyKey, CreateUploadSessionRequest request)
    {
        var payloadHash = HashPayload(request);

        // Built before the registration rather than inside a GetOrAdd factory. ConcurrentDictionary
        // does not promise a value factory runs once: under contention on one key it may run on
        // several threads and keep a single result. A factory that also decided "did I create this?"
        // by setting a captured flag therefore told every racing thread it had won, which skipped
        // the payload check below for all but one of them — two simultaneous requests sharing a key
        // but carrying different payloads got 201 and the winner's session instead of 409, with the
        // idempotency contract failing under exactly the concurrency it exists to handle. The
        // factory's side effect leaked too: every loser's session stayed in `sessions`, unreachable
        // and never swept. The value overload has no factory, so winning is decided by identity.
        var candidateId = $"upload-{Guid.NewGuid():N}";
        var candidate = new Session(
            candidateId,
            $"doc-{Guid.NewGuid():N}",
            // Validated against the contract's pattern before the store is called.
            request.ContentSha256!,
            timeProvider.GetUtcNow().Add(SessionLifetime),
            idempotencyKey);

        // Registered before the id can be observed through createKeys, so a replay that reads the
        // winner's id always finds the session behind it.
        sessions[candidateId] = candidate;
        var registered = createKeys.GetOrAdd(idempotencyKey, (payloadHash, candidateId));
        var isNew = string.Equals(registered.SessionId, candidateId, StringComparison.Ordinal);

        if (!isNew)
        {
            // Lost the race, or this is an ordinary replay. Either way the candidate is unused.
            sessions.TryRemove(candidateId, out _);
            if (!string.Equals(registered.PayloadHash, payloadHash, StringComparison.Ordinal))
            {
                return (IdempotencyOutcome.Conflict, registered.SessionId, "", default);
            }
        }

        var stored = sessions[registered.SessionId];
        return (
            isNew ? IdempotencyOutcome.Created : IdempotencyOutcome.Replayed,
            stored.SessionId,
            stored.DocumentId,
            stored.ExpiresAt);
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

    private static UploadReceipt Receipt(Session session) =>
        new(session.SessionId, session.DocumentId, session.ExpectedContentSha256, "SCANNING");
}
