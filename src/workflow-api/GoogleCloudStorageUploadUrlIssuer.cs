using Google.Cloud.Storage.V1;

namespace LaPluma.WorkflowApi;

/// <summary>
/// Mints a Google Cloud Storage V4 signed URL against the private staging/quarantine bucket.
/// Under ADR-019 (Lean Managed GCP Platform), document bytes (up to 100 MB) bypass API Gateway's
/// 32 MB request limit via short-lived (15-minute), narrowly scoped create-only upload grants to
/// private Cloud Storage objects over Google's internet endpoint (storage.googleapis.com).
///
/// The canonical request, string-to-sign, and query parameters are built by
/// <see cref="UrlSigner"/>, the Cloud Storage client library's V4 implementation, and the signature
/// is an RSA-SHA256 signature made by the service account itself (see
/// <see cref="IamCredentialsBlobSigner"/>). Cloud Storage accepts the URL only if that signature
/// verifies against one of the service account's Google-managed public keys, so a URL cannot be
/// forged, widened, or extended by whoever holds it.
/// </summary>
public sealed class GoogleCloudStorageUploadUrlIssuer : IUploadUrlIssuer
{
    public const string StorageHost = "storage.googleapis.com";
    public const int DefaultExpirySeconds = 900; // 15 minutes
    public const int MaxExpirySeconds = 900;

    private readonly UrlSigner urlSigner;
    private readonly TimeProvider timeProvider;

    public GoogleCloudStorageUploadUrlIssuer(
        string bucketName, UrlSigner.IBlobSigner blobSigner, TimeProvider timeProvider)
    {
        if (string.IsNullOrWhiteSpace(bucketName))
        {
            throw new ArgumentException("Bucket name must not be empty.", nameof(bucketName));
        }

        ArgumentNullException.ThrowIfNull(blobSigner);
        ArgumentNullException.ThrowIfNull(timeProvider);

        BucketName = bucketName.Trim();
        SignerServiceAccount = blobSigner.Id;
        this.timeProvider = timeProvider;
        urlSigner = UrlSigner.FromBlobSigner(blobSigner);
    }

    public string BucketName { get; }

    public string SignerServiceAccount { get; }

    public async Task<Uri> MintWriteOnlyUrlAsync(
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

        // The lifetime is taken from the session's expiry on the service's clock and handed to the
        // signer as a duration (X-Goog-Expires), capped at 15 minutes.
        var now = timeProvider.GetUtcNow();
        var seconds = (int)Math.Max(1, Math.Min(MaxExpirySeconds, (expiresOn - now).TotalSeconds));

        // PUT to one named object, signing only the host header: the client sends the bytes with
        // a plain PUT and needs no extra headers, and the URL is good for nothing else — not a
        // read, not a list, not another object name.
        var template = UrlSigner.RequestTemplate
            .FromBucket(BucketName)
            .WithObjectName(blobName)
            .WithHttpMethod(HttpMethod.Put);
        var options = UrlSigner.Options
            .FromDuration(TimeSpan.FromSeconds(seconds))
            .WithSigningVersion(SigningVersion.V4)
            .WithScheme("https");

        var signed = await urlSigner.SignAsync(template, options, cancellationToken).ConfigureAwait(false);
        return new Uri(signed, UriKind.Absolute);
    }
}
