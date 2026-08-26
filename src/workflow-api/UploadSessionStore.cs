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
        var isNew = false;
        var registered = createKeys.GetOrAdd(idempotencyKey, _ =>
        {
            isNew = true;
            var sessionId = $"upload-{Guid.NewGuid():N}";
            var session = new Session(
                sessionId,
                $"doc-{Guid.NewGuid():N}",
                // Validated against the contract's pattern before the store is called.
                request.ContentSha256!,
                timeProvider.GetUtcNow().Add(SessionLifetime),
                idempotencyKey);
            sessions[sessionId] = session;
            return (payloadHash, sessionId);
        });

        if (!isNew && !string.Equals(registered.PayloadHash, payloadHash, StringComparison.Ordinal))
        {
            return (IdempotencyOutcome.Conflict, registered.SessionId, "", default);
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
