using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LaPluma.WorkflowApi.Tests;

public sealed class GoogleCloudStorageUploadSessionTests
{
    private const string ValidSha256 =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string TestBucket = "lp-quarantine-test-bucket";

    private static object ValidRequest(long sizeBytes = 1_048_576, string sha256 = ValidSha256) => new
    {
        folderId = "folder-fixture-0001",
        originalName = "passport-scan.pdf",
        sizeBytes,
        contentSha256 = sha256,
    };

    private static HttpRequestMessage Create(object body, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/documents/upload-sessions")
        {
            Content = JsonContent.Create(body),
        };
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private static HttpRequestMessage Complete(string sessionId, string key)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/documents/upload-sessions/{sessionId}/complete");
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task Gcs_upload_url_issuer_mints_compliant_v4_signed_put_url()
    {
        var issuer = new GoogleCloudStorageUploadUrlIssuer(TestBucket, "signer-sa@project.iam.gserviceaccount.com");
        var expiresOn = DateTimeOffset.UtcNow.AddMinutes(15);

        var uri = await issuer.MintWriteOnlyUrlAsync("doc-test-001", expiresOn, CancellationToken.None);

        Assert.Equal("https", uri.Scheme);
        Assert.Equal(GoogleCloudStorageUploadUrlIssuer.StorageHost, uri.Host);
        Assert.Equal($"/{TestBucket}/doc-test-001", uri.AbsolutePath);

        var query = uri.Query;
        Assert.Contains("X-Goog-Algorithm=GOOG4-RSA-SHA256", query, StringComparison.Ordinal);
        Assert.Contains("X-Goog-Credential=", query, StringComparison.Ordinal);
        Assert.Contains("X-Goog-Date=", query, StringComparison.Ordinal);
        Assert.Contains("X-Goog-Expires=", query, StringComparison.Ordinal);
        Assert.Contains("X-Goog-SignedHeaders=host", query, StringComparison.Ordinal);
        Assert.Contains("X-Goog-Signature=", query, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("../secret.txt")]
    [InlineData("/leading-slash")]
    public async Task Gcs_upload_url_issuer_rejects_path_traversal_and_invalid_blob_names(string invalidBlob)
    {
        var issuer = new GoogleCloudStorageUploadUrlIssuer(TestBucket);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            issuer.MintWriteOnlyUrlAsync(invalidBlob, DateTimeOffset.UtcNow.AddMinutes(15), CancellationToken.None));
    }

    [Fact]
    public async Task Workflow_api_serves_gcs_upload_session_when_configured()
    {
        using var factory = new GcsConfiguredFactory();
        var client = factory.CreateClient();

        var response = await client.SendAsync(Create(ValidRequest(), Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJson(response);
        Assert.Equal("PUT", body.GetProperty("uploadMethod").GetString());
        Assert.Equal(ValidSha256, body.GetProperty("expectedContentSha256").GetString());

        var uploadUrl = body.GetProperty("uploadUrl").GetString()!;
        Assert.StartsWith($"https://{GoogleCloudStorageUploadUrlIssuer.StorageHost}/{TestBucket}/", uploadUrl, StringComparison.Ordinal);
        Assert.Contains("X-Goog-Algorithm=GOOG4-RSA-SHA256", uploadUrl, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(104_857_600, HttpStatusCode.Created)]
    [InlineData(104_857_601, HttpStatusCode.UnprocessableEntity)]
    [InlineData(0, HttpStatusCode.UnprocessableEntity)]
    public async Task Upload_session_enforces_100_mb_capture_limit(long sizeBytes, HttpStatusCode expectedStatus)
    {
        using var factory = new GcsConfiguredFactory();
        var client = factory.CreateClient();

        var response = await client.SendAsync(Create(ValidRequest(sizeBytes), Guid.NewGuid().ToString()));
        Assert.Equal(expectedStatus, response.StatusCode);
    }

    [Fact]
    public async Task Upload_completion_succeeds_when_stored_bytes_and_digest_match()
    {
        using var factory = new GcsConfiguredFactory();
        var client = factory.CreateClient();

        var createRes = await client.SendAsync(Create(ValidRequest(), Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.Created, createRes.StatusCode);
        var created = await ReadJson(createRes);
        var sessionId = created.GetProperty("sessionId").GetString()!;

        var completeRes = await client.SendAsync(Complete(sessionId, Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.OK, completeRes.StatusCode);

        var receipt = await ReadJson(completeRes);
        Assert.Equal("SCANNING", receipt.GetProperty("processingState").GetString());
        Assert.Equal(ValidSha256, receipt.GetProperty("contentSha256").GetString());
        Assert.Equal(sessionId, receipt.GetProperty("sessionId").GetString());
    }

    [Fact]
    public async Task Upload_completion_rejects_when_digest_mismatches()
    {
        using var factory = new GcsConfiguredFactory(verifier: new TestConfigurableVerifier(
            outcome: StoredDocumentVerificationOutcome.DigestMismatch,
            actualSha256: "badbadbadbadbadbadbadbadbadbadbadbadbadbadbadbadbadbadbadbadbad0"));
        var client = factory.CreateClient();

        var createRes = await client.SendAsync(Create(ValidRequest(), Guid.NewGuid().ToString()));
        var created = await ReadJson(createRes);
        var sessionId = created.GetProperty("sessionId").GetString()!;

        var completeRes = await client.SendAsync(Complete(sessionId, Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, completeRes.StatusCode);
        var problem = await ReadJson(completeRes);
        Assert.Equal("urn:lapluma:problem:upload-digest-mismatch", problem.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Upload_completion_rejects_when_stored_blob_is_missing()
    {
        using var factory = new GcsConfiguredFactory(verifier: new TestConfigurableVerifier(
            outcome: StoredDocumentVerificationOutcome.NotFound));
        var client = factory.CreateClient();

        var createRes = await client.SendAsync(Create(ValidRequest(), Guid.NewGuid().ToString()));
        var created = await ReadJson(createRes);
        var sessionId = created.GetProperty("sessionId").GetString()!;

        var completeRes = await client.SendAsync(Complete(sessionId, Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, completeRes.StatusCode);
        var problem = await ReadJson(completeRes);
        Assert.Equal("urn:lapluma:problem:upload-blob-missing", problem.GetProperty("type").GetString());
    }

    [Fact]
    public async Task Upload_completion_rejects_when_actual_size_mismatches_limits()
    {
        using var factory = new GcsConfiguredFactory(verifier: new TestConfigurableVerifier(
            outcome: StoredDocumentVerificationOutcome.SizeMismatch,
            actualSizeBytes: 104_857_601));
        var client = factory.CreateClient();

        var createRes = await client.SendAsync(Create(ValidRequest(), Guid.NewGuid().ToString()));
        var created = await ReadJson(createRes);
        var sessionId = created.GetProperty("sessionId").GetString()!;

        var completeRes = await client.SendAsync(Complete(sessionId, Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, completeRes.StatusCode);
        var problem = await ReadJson(completeRes);
        Assert.Equal("urn:lapluma:problem:upload-size-invalid", problem.GetProperty("type").GetString());
    }

    private sealed class GcsConfiguredFactory(IStoredDocumentVerifier? verifier = null)
        : WebApplicationFactory<global::Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.WithFixtureWorkflow().WithAuthenticationConfigured().WithTestAuthentication();
            builder.UseSetting(UploadConfiguration.GcsQuarantineBucketSetting, TestBucket);
            builder.ConfigureServices(services =>
            {
                if (verifier is not null)
                {
                    services.AddSingleton(verifier);
                }
            });
        }
    }

    private sealed class TestConfigurableVerifier(
        StoredDocumentVerificationOutcome outcome,
        string? actualSha256 = null,
        long? actualSizeBytes = null) : IStoredDocumentVerifier
    {
        public Task<StoredDocumentVerificationResult> VerifyAsync(
            string documentId,
            string expectedSha256,
            long declaredSizeBytes,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new StoredDocumentVerificationResult(
                outcome,
                actualSha256 ?? expectedSha256,
                actualSizeBytes ?? declaredSizeBytes));
        }
    }
}
