using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Google.Apis.Auth.OAuth2;
using Google.Cloud.Storage.V1;

namespace LaPluma.WorkflowApi;

/// <summary>Supplies an OAuth 2.0 access token for calls to Google APIs.</summary>
public interface IGoogleAccessTokenSource
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Application Default Credentials. On Cloud Run that is the service's runtime service account,
/// read from the metadata server: there is no key file anywhere.
/// </summary>
public sealed class ApplicationDefaultAccessTokenSource : IGoogleAccessTokenSource
{
    private const string CloudPlatformScope = "https://www.googleapis.com/auth/cloud-platform";

    private readonly SemaphoreSlim gate = new(1, 1);
    private ITokenAccess? credential;

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        // Kept once obtained, so its token cache is reused; a failed lookup is retried next time
        // rather than remembered.
        var current = credential;
        if (current is null)
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                current = credential ??= (await GoogleCredential.GetApplicationDefaultAsync(cancellationToken).ConfigureAwait(false))
                    .CreateScoped(CloudPlatformScope);
            }
            finally
            {
                gate.Release();
            }
        }

        return await current.GetAccessTokenForRequestAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Raised when the IAM Credentials API does not return a signature.</summary>
public sealed class UploadSigningFailedException(string message, Exception? inner = null)
    : InvalidOperationException(message, inner);

/// <summary>
/// Signs with the service account's Google-managed key through the IAM Credentials API
/// (<c>projects/-/serviceAccounts/{email}:signBlob</c>). The private key never leaves Google, and
/// the caller needs <c>iam.serviceAccounts.signBlob</c> on that account, which Terraform grants the
/// workflow API on its own service account only (<c>roles/iam.serviceAccountTokenCreator</c>).
///
/// The API returns the RSA-SHA256 signature Base64-encoded, which is the form
/// <see cref="UrlSigner"/> expects; the URL carries it hex-encoded as <c>X-Goog-Signature</c>.
/// </summary>
public sealed partial class IamCredentialsBlobSigner : UrlSigner.IBlobSigner
{
    /// <summary>The named HTTP client the signing requests are sent with.</summary>
    public const string HttpClientName = "LaPluma.IamCredentials";

    public const string SigningAlgorithm = "GOOG4-RSA-SHA256";

    public static readonly Uri DefaultEndpoint = new("https://iamcredentials.googleapis.com/");

    private readonly IHttpClientFactory httpClientFactory;
    private readonly IGoogleAccessTokenSource tokens;
    private readonly Uri signBlobUri;

    public IamCredentialsBlobSigner(
        string serviceAccountEmail,
        IHttpClientFactory httpClientFactory,
        IGoogleAccessTokenSource tokens,
        Uri? endpoint = null)
    {
        Id = ValidateServiceAccountEmail(serviceAccountEmail);
        this.httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
        this.tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        signBlobUri = new Uri(endpoint ?? DefaultEndpoint, $"v1/projects/-/serviceAccounts/{Id}:signBlob");
    }

    /// <summary>The service account email; it is also the V4 credential's identity.</summary>
    public string Id { get; }

    public string Algorithm => SigningAlgorithm;

    /// <summary>
    /// Returns the trimmed email, or throws if it is not a plain service account address. It goes
    /// into a request path, so nothing that could change the path is let through.
    /// </summary>
    public static string ValidateServiceAccountEmail(string? serviceAccountEmail)
    {
        var email = serviceAccountEmail?.Trim() ?? string.Empty;
        if (!ServiceAccountEmail().IsMatch(email))
        {
            throw new ArgumentException(
                "The signer must be a service account email such as name@project.iam.gserviceaccount.com.",
                nameof(serviceAccountEmail));
        }

        return email;
    }

    public string CreateSignature(byte[] data, UrlSigner.BlobSignerParameters signerParameters) =>
        CreateSignatureAsync(data, signerParameters, CancellationToken.None).GetAwaiter().GetResult();

    public async Task<string> CreateSignatureAsync(
        byte[] data, UrlSigner.BlobSignerParameters signerParameters, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(data);

        using var request = new HttpRequestMessage(HttpMethod.Post, signBlobUri)
        {
            Content = JsonContent.Create(new SignBlobRequest(Convert.ToBase64String(data))),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", await tokens.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false));

        HttpResponseMessage response;
        try
        {
            response = await httpClientFactory.CreateClient(HttpClientName)
                .SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException error)
        {
            throw new UploadSigningFailedException("The IAM Credentials API could not be reached.", error);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                // The status is enough to act on (403: the grant is missing; 404: wrong account).
                // The body is not logged or echoed.
                throw new UploadSigningFailedException(
                    $"The IAM Credentials API refused to sign ({(int)response.StatusCode}).");
            }

            var body = await response.Content.ReadFromJsonAsync<SignBlobResponse>(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body?.SignedBlob))
            {
                throw new UploadSigningFailedException("The IAM Credentials API returned no signature.");
            }

            return body.SignedBlob;
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9-]{4,28}[a-z0-9]@[a-z0-9.-]+\\.gserviceaccount\\.com$")]
    private static partial Regex ServiceAccountEmail();

    private sealed record SignBlobRequest([property: JsonPropertyName("payload")] string Payload);

    private sealed record SignBlobResponse(
        [property: JsonPropertyName("keyId")] string? KeyId,
        [property: JsonPropertyName("signedBlob")] string? SignedBlob);
}
