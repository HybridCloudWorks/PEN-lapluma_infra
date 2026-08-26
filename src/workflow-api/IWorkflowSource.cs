namespace LaPluma.WorkflowApi;

/// <summary>
/// Outcome of registering an idempotency key against a payload. Replay of the same payload returns
/// the original result; the same key with a different payload is a caller bug worth a 409.
/// </summary>
public enum IdempotencyOutcome
{
    Created,
    Replayed,
    Conflict,
}

public sealed record CreateClientOutcome(IdempotencyOutcome Outcome, ClientDirectoryEntry Entry);

/// <summary>
/// The workflow read and write surface behind the HTTP handlers. The only implementation is the
/// in-memory fixture; the durable store is TODO 5.8, and the registration refuses to default to
/// anything so a deployment always states which one it serves.
/// </summary>
public interface IWorkflowSource
{
    Task<AuthenticatedContext> GetSessionContextAsync(string userId, CancellationToken cancellationToken);

    Task<ClientDirectoryPage> ListClientsAsync(
        string? query, string? cursor, CancellationToken cancellationToken);

    Task<CreateClientOutcome> CreateClientAsync(
        string idempotencyKey, CreateClientRequest request, CancellationToken cancellationToken);

    /// <summary>Null when the case is missing or the caller is not authorized to see it — one 404.</summary>
    Task<CaseWorkspace?> GetCaseWorkspaceAsync(string caseId, CancellationToken cancellationToken);
}
