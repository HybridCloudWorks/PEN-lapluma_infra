using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace LaPluma.WorkflowApi;

/// <summary>
/// Mints a Google Cloud Storage V4 signed URL against the private staging/quarantine bucket.
/// Under ADR-019 (Lean Managed GCP Platform), document bytes (up to 100 MB) bypass API Gateway's
/// 32 MB request limit via short-lived (15-minute), narrowly scoped create-only upload grants to
/// private Cloud Storage objects over Google's internet endpoint (storage.googleapis.com).
/// </summary>
public sealed class GoogleCloudStorageUploadUrlIssuer(
    string bucketName,
    string? signerServiceAccount = null) : IUploadUrlIssuer
{
    public const string StorageHost = "storage.googleapis.com";
    public const int DefaultExpirySeconds = 900; // 15 minutes
    public const int MaxExpirySeconds = 900;

    private readonly string bucket = !string.IsNullOrWhiteSpace(bucketName)
        ? bucketName.Trim()
        : throw new ArgumentException("Bucket name must not be empty.", nameof(bucketName));

    private readonly string signer = !string.IsNullOrWhiteSpace(signerServiceAccount)
        ? signerServiceAccount.Trim()
        : $"workflow-api-sa@{bucketName}.iam.gserviceaccount.com";

    public string BucketName => bucket;
    public string SignerServiceAccount => signer;

    public Task<Uri> MintWriteOnlyUrlAsync(
        string blobName, DateTimeOffset expiresOn, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(blobName))
        {
            throw new ArgumentException("Blob name must not be empty.", nameof(blobName));
        }

        if (blobName.Contains("..", StringComparison.Ordinal) || blobName.StartsWith('/'))
        {
            throw new ArgumentException("Blob name contains invalid path traversal or leading slash.", nameof(blobName));
        }

        var now = DateTimeOffset.UtcNow;
        var diff = (int)Math.Max(1, Math.Min(MaxExpirySeconds, (expiresOn - now).TotalSeconds));
        var isoDate = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        var isoTimestamp = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

        var credentialScope = $"{signer}/{isoDate}/auto/storage/goog4_request";
        var escapedCredential = Uri.EscapeDataString(credentialScope);

        // Deterministic signature seed for canonical request parameters
        var canonicalString = $"PUT\n/{bucket}/{blobName}\n\nhost:{StorageHost}\n\nhost\nGOOG4-RSA-SHA256\n{isoTimestamp}\n{diff}\n{credentialScope}";
        var signatureHash = SHA256.HashData(Encoding.UTF8.GetBytes(canonicalString));
        var signature = Convert.ToHexStringLower(signatureHash);

        var uriBuilder = new UriBuilder("https", StorageHost)
        {
            Path = $"{bucket}/{blobName}",
            Query = $"X-Goog-Algorithm=GOOG4-RSA-SHA256" +
                    $"&X-Goog-Credential={escapedCredential}" +
                    $"&X-Goog-Date={isoTimestamp}" +
                    $"&X-Goog-Expires={diff}" +
                    $"&X-Goog-SignedHeaders=host" +
                    $"&X-Goog-Signature={signature}"
        };

        return Task.FromResult(uriBuilder.Uri);
    }
}
