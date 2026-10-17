using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LaPluma.WorkflowApi.Tests;

/// <summary>
/// INF-25: upload URLs are real V4 signed URLs. The canonical request and string-to-sign come from
/// the Cloud Storage client library, the RSA-SHA256 signature from the IAM Credentials signBlob
/// API. The IAM call is the one thing replaced: it is answered in process, and the tests check
/// both what was sent to it and how its answer ends up in the URL.
/// </summary>
public sealed class GoogleCloudStorageUploadSessionTests
{
    private const string ValidSha256 =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string TestBucket = "lp-quarantine-test-bucket";
    private const string Signer = "lp-wf-api-test@lapluma-test.iam.gserviceaccount.com";

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

    private static GoogleCloudStorageUploadUrlIssuer Issuer(FakeIamCredentials iam, TimeProvider? clock = null) =>
        new(TestBucket, new IamCredentialsBlobSigner(Signer, new SingleHandlerFactory(iam), new FixedTokenSource()), clock ?? TimeProvider.System);

    private static Dictionary<string, string> QueryOf(Uri uri) =>
        uri.Query.TrimStart('?').Split('&')
            .Select(part => part.Split('=', 2))
            .ToDictionary(pair => pair[0], pair => Uri.UnescapeDataString(pair[1]), StringComparer.Ordinal);

    [Fact]
    public async Task Issued_url_is_a_v4_signed_put_url_signed_by_iam_signBlob()
    {
        var iam = new FakeIamCredentials();
        var uri = await Issuer(iam).MintWriteOnlyUrlAsync(
            "doc-test-001", DateTimeOffset.UtcNow.AddMinutes(20), CancellationToken.None);

        Assert.Equal("https", uri.Scheme);
        Assert.Equal(GoogleCloudStorageUploadUrlIssuer.StorageHost, uri.Host);
        Assert.Equal($"/{TestBucket}/doc-test-001", uri.AbsolutePath);

        var query = QueryOf(uri);
        var timestamp = query["X-Goog-Date"];
        Assert.Matches(new Regex("^[0-9]{8}T[0-9]{6}Z$"), timestamp);
        var scope = $"{timestamp[..8]}/auto/storage/goog4_request";

        Assert.Equal("GOOG4-RSA-SHA256", query["X-Goog-Algorithm"]);
        Assert.Equal($"{Signer}/{scope}", query["X-Goog-Credential"]);
        Assert.Equal("900", query["X-Goog-Expires"]);
        Assert.Equal("host", query["X-Goog-SignedHeaders"]);

        // The signature is IAM's answer, hex-encoded — not a digest computed in process.
        Assert.Equal(Convert.ToHexStringLower(FakeIamCredentials.Signature), query["X-Goog-Signature"]);

        // Exactly one signBlob call, for this service account, with the service's own token.
        var call = Assert.Single(iam.Calls);
        Assert.Equal(HttpMethod.Post, call.Method);
        Assert.Equal(
            $"https://iamcredentials.googleapis.com/v1/projects/-/serviceAccounts/{Signer}:signBlob",
            call.Uri.AbsoluteUri);
        Assert.Equal("Bearer test-access-token", call.Authorization);

        // What was signed is the V4 string-to-sign for exactly this request: the algorithm, the
        // timestamp, the scope, and the SHA-256 of the canonical request rebuilt from the URL.
        var lines = Encoding.UTF8.GetString(call.Payload).Split('\n');
        Assert.Equal(4, lines.Length);
        Assert.Equal("GOOG4-RSA-SHA256", lines[0]);
        Assert.Equal(timestamp, lines[1]);
        Assert.Equal(scope, lines[2]);

        var canonicalQuery = string.Join('&', uri.Query.TrimStart('?').Split('&')
            .Where(part => !part.StartsWith("X-Goog-Signature=", StringComparison.Ordinal))
            .OrderBy(part => part, StringComparer.Ordinal));
        var canonicalRequest = string.Join('\n',
            "PUT",
            $"/{TestBucket}/doc-test-001",
            canonicalQuery,
            $"host:{GoogleCloudStorageUploadUrlIssuer.StorageHost}",
            string.Empty,
            "host",
            "UNSIGNED-PAYLOAD");
        Assert.Equal(
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))),
            lines[3]);
    }

    [Theory]
    [InlineData(5, "300")]
    [InlineData(15, "900")]
    [InlineData(60, "900")]
    public async Task The_url_lives_as_long_as_the_session_and_never_more_than_15_minutes(int minutes, string expected)
    {
        var clock = new TestClock();
        var uri = await Issuer(new FakeIamCredentials(), clock).MintWriteOnlyUrlAsync(
            "doc-test-002", clock.GetUtcNow().AddMinutes(minutes), CancellationToken.None);

        Assert.Equal(expected, QueryOf(uri)["X-Goog-Expires"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task A_refused_signBlob_call_fails_the_issue_instead_of_producing_a_url(HttpStatusCode status)
    {
        var issuer = Issuer(new FakeIamCredentials(status));

        var error = await Assert.ThrowsAsync<UploadSigningFailedException>(() =>
            issuer.MintWriteOnlyUrlAsync("doc-test-003", DateTimeOffset.UtcNow.AddMinutes(15), CancellationToken.None));
        Assert.Contains(((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture), error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("../secret.txt")]
    [InlineData("/leading-slash")]
    public async Task Gcs_upload_url_issuer_rejects_path_traversal_and_invalid_blob_names(string invalidBlob)
    {
        var iam = new FakeIamCredentials();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Issuer(iam).MintWriteOnlyUrlAsync(invalidBlob, DateTimeOffset.UtcNow.AddMinutes(15), CancellationToken.None));
        Assert.Empty(iam.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("someone@example.com")]
    [InlineData("lp-wf-api/../x@p.iam.gserviceaccount.com")]
    [InlineData("lp-wf-api-test@lapluma-test.iam.gserviceaccount.com/x")]
    [InlineData("lp-wf-api-test@lapluma-test.iam.gserviceaccount.com?x=1")]
    public void The_signer_must_be_a_plain_service_account_email(string email) =>
        Assert.Throws<ArgumentException>(() => IamCredentialsBlobSigner.ValidateServiceAccountEmail(email));

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

        var uploadUrl = new Uri(body.GetProperty("uploadUrl").GetString()!);
        Assert.StartsWith($"https://{GoogleCloudStorageUploadUrlIssuer.StorageHost}/{TestBucket}/", uploadUrl.AbsoluteUri, StringComparison.Ordinal);
        var query = QueryOf(uploadUrl);
        Assert.Equal("GOOG4-RSA-SHA256", query["X-Goog-Algorithm"]);
        Assert.StartsWith($"{Signer}/", query["X-Goog-Credential"], StringComparison.Ordinal);
        Assert.Equal(Convert.ToHexStringLower(FakeIamCredentials.Signature), query["X-Goog-Signature"]);
        Assert.Single(factory.Iam.Calls);
    }

    [Fact]
    public async Task A_signing_failure_is_a_typed_503_not_a_broken_url()
    {
        using var factory = new GcsConfiguredFactory(iamStatus: HttpStatusCode.Forbidden);

        var response = await factory.CreateClient().SendAsync(Create(ValidRequest(), Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            "urn:lapluma:problem:upload-signing-unavailable",
            (await ReadJson(response)).GetProperty("type").GetString());
    }

    [Fact]
    public void A_bucket_without_a_signer_stops_the_host()
    {
        using var factory = new WebApplicationFactory<global::Program>().WithWebHostBuilder(builder =>
            builder.WithFixtureWorkflow().WithAuthenticationConfigured().WithTestAuthentication()
                .UseSetting(UploadConfiguration.GcsQuarantineBucketSetting, TestBucket));

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains(UploadConfiguration.GcsSignerServiceAccountSetting, error.ToString());
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

    /// <summary>
    /// The deployed configuration — bucket and signer — with the IAM Credentials API and the
    /// metadata-server token answered in process.
    /// </summary>
    private sealed class GcsConfiguredFactory(
        IStoredDocumentVerifier? verifier = null,
        HttpStatusCode iamStatus = HttpStatusCode.OK)
        : WebApplicationFactory<global::Program>
    {
        public FakeIamCredentials Iam { get; } = new(iamStatus);

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.WithFixtureWorkflow().WithAuthenticationConfigured().WithTestAuthentication();
            builder.UseSetting(UploadConfiguration.GcsQuarantineBucketSetting, TestBucket);
            builder.UseSetting(UploadConfiguration.GcsSignerServiceAccountSetting, Signer);
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IGoogleAccessTokenSource>(new FixedTokenSource());
                services.AddHttpClient(IamCredentialsBlobSigner.HttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => Iam);
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

/// <summary>
/// Stands in for <c>iamcredentials.googleapis.com</c>: records each signBlob call and answers with
/// a fixed 256-byte signature (the size of an RSA-2048 signature), or with the given error status.
/// </summary>
public sealed class FakeIamCredentials(HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
{
    public static readonly byte[] Signature = Enumerable.Range(0, 256).Select(i => (byte)(255 - i)).ToArray();

    private readonly List<SignBlobCall> calls = [];

    public IReadOnlyList<SignBlobCall> Calls
    {
        get
        {
            lock (calls)
            {
                return calls.ToArray();
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
        lock (calls)
        {
            calls.Add(new SignBlobCall(
                request.Method,
                request.RequestUri!,
                request.Headers.Authorization?.ToString(),
                Convert.FromBase64String(body.GetProperty("payload").GetString()!)));
        }

        if (status != HttpStatusCode.OK)
        {
            return new HttpResponseMessage(status) { Content = JsonContent.Create(new { error = new { code = (int)status } }) };
        }

        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new { keyId = "test-key", signedBlob = Convert.ToBase64String(Signature) }),
        };
    }

    public sealed record SignBlobCall(HttpMethod Method, Uri Uri, string? Authorization, byte[] Payload);
}

public sealed class FixedTokenSource : IGoogleAccessTokenSource
{
    public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult("test-access-token");
}

public sealed class SingleHandlerFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}
