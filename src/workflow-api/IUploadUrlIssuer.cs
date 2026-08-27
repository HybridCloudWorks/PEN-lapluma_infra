namespace LaPluma.WorkflowApi;

/// <summary>Configuration keys for the upload path. Set by compute.bicep, never hard-coded.</summary>
public static class UploadConfiguration
{
    public const string QuarantineBlobEndpointSetting = "Workflow:QuarantineBlobEndpoint";
    public const string ManagedIdentityClientIdSetting = "AZURE_CLIENT_ID";
}

/// <summary>Raised when upload issuing is requested in an environment that has no storage wired.</summary>
public sealed class UploadIssuingNotConfiguredException : InvalidOperationException
{
    public UploadIssuingNotConfiguredException()
        : base("No quarantine blob endpoint is configured, so no upload URL can be issued.")
    {
    }
}

/// <summary>
/// Mints the write-only, single-blob URL an upload session hands to the client. The bytes never
/// traverse the API: the client PUTs directly to this URL.
/// </summary>
public interface IUploadUrlIssuer
{
    Task<Uri> MintWriteOnlyUrlAsync(
        string blobName, DateTimeOffset expiresOn, CancellationToken cancellationToken);
}

/// <summary>
/// Fail-closed issuer for environments with no storage configured: creating an upload session
/// returns 503 rather than a URL that cannot work. The point is that "upload is not wired up" is a
/// visible, typed condition, not a plausible-looking session whose PUT then fails mysteriously.
/// </summary>
public sealed class NotConfiguredUploadUrlIssuer : IUploadUrlIssuer
{
    public Task<Uri> MintWriteOnlyUrlAsync(
        string blobName, DateTimeOffset expiresOn, CancellationToken cancellationToken) =>
        throw new UploadIssuingNotConfiguredException();
}
