using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;

namespace LaPluma.WorkflowApi;

/// <summary>
/// Mints a user-delegation SAS against the quarantine account. Shared-key access is disabled on
/// every storage account in this estate, so an account-key SAS is impossible by construction; a
/// user-delegation SAS is authorized against this service's managed identity at use time, which is
/// why rbac.bicep grants the core identity Storage Blob Data Contributor on the quarantine account
/// (the role that carries generateUserDelegationKey). Create-and-write only, one blob, and the
/// caller supplies the expiry — fifteen minutes, set where the session is created.
/// </summary>
public sealed class UserDelegationUploadUrlIssuer(
    Uri quarantineBlobEndpoint, string? managedIdentityClientId) : IUploadUrlIssuer
{
    /// <summary>The ingestion container: data.bicep creates one container per account, named for
    /// the account's purpose.</summary>
    public const string ContainerName = "quarantine";

    private readonly BlobServiceClient serviceClient = new(
        quarantineBlobEndpoint,
        // Four user-assigned identities exist in this subscription; without the client id the
        // credential has to guess which one, and on a host with more than one attached it fails.
        string.IsNullOrWhiteSpace(managedIdentityClientId)
            ? new DefaultAzureCredential()
            : new DefaultAzureCredential(new DefaultAzureCredentialOptions
            {
                ManagedIdentityClientId = managedIdentityClientId,
            }));

    public async Task<Uri> MintWriteOnlyUrlAsync(
        string blobName, DateTimeOffset expiresOn, CancellationToken cancellationToken)
    {
        var delegationKey = await serviceClient.GetUserDelegationKeyAsync(
            startsOn: null, expiresOn, cancellationToken);

        var sasBuilder = new BlobSasBuilder(
            BlobSasPermissions.Create | BlobSasPermissions.Write, expiresOn)
        {
            BlobContainerName = ContainerName,
            BlobName = blobName,
        };

        var blobClient = serviceClient.GetBlobContainerClient(ContainerName).GetBlobClient(blobName);
        return new BlobUriBuilder(blobClient.Uri)
        {
            Sas = sasBuilder.ToSasQueryParameters(delegationKey.Value, serviceClient.AccountName),
        }.ToUri();
    }
}
