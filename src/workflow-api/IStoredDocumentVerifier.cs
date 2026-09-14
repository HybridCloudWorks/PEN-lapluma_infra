namespace LaPluma.WorkflowApi;

public enum StoredDocumentVerificationOutcome
{
    Verified,
    NotFound,
    DigestMismatch,
    SizeMismatch,
    Unprocessable,
}

public sealed record StoredDocumentVerificationResult(
    StoredDocumentVerificationOutcome Outcome,
    string? ActualSha256 = null,
    long? ActualSizeBytes = null,
    string? Detail = null);

/// <summary>
/// Verifies stored document bytes before completing an upload session and advancing evidence
/// to quarantine / processing (INT-05 / ADR-019).
/// </summary>
public interface IStoredDocumentVerifier
{
    Task<StoredDocumentVerificationResult> VerifyAsync(
        string documentId,
        string expectedSha256,
        long declaredSizeBytes,
        CancellationToken cancellationToken);
}

/// <summary>
/// Default in-memory / pass-through verifier. Asserts non-null expected values and marks
/// as verified.
/// </summary>
public sealed class PassThroughStoredDocumentVerifier : IStoredDocumentVerifier
{
    public Task<StoredDocumentVerificationResult> VerifyAsync(
        string documentId,
        string expectedSha256,
        long declaredSizeBytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(documentId))
        {
            return Task.FromResult(new StoredDocumentVerificationResult(
                StoredDocumentVerificationOutcome.NotFound, Detail: "Missing documentId"));
        }

        if (declaredSizeBytes is < 1 or > UploadSessionStore.MaximumSizeBytes)
        {
            return Task.FromResult(new StoredDocumentVerificationResult(
                StoredDocumentVerificationOutcome.SizeMismatch,
                ActualSizeBytes: declaredSizeBytes,
                Detail: "Declared size exceeds capture limits"));
        }

        return Task.FromResult(new StoredDocumentVerificationResult(
            StoredDocumentVerificationOutcome.Verified,
            ActualSha256: expectedSha256,
            ActualSizeBytes: declaredSizeBytes));
    }
}
