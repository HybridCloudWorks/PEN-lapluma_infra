using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace LaPluma.WorkflowApi.Tests;

/// <summary>
/// The upload-session flow against a mintable issuer and a controllable clock. The one real network
/// path the iOS client already has PUTs to whatever URL a session returns, so the shape of these
/// responses is load-bearing before any storage exists.
/// </summary>
public sealed class UploadSessionTests
{
    private const string ValidSha256 =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static object ValidRequest() => new
    {
        folderId = "folder-fixture-0001",
        originalName = "passport-scan.pdf",
        sizeBytes = 1_048_576,
        contentSha256 = ValidSha256,
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
    public async Task A_session_returns_a_write_only_put_slot_with_the_declared_digest()
    {
        using var factory = new UploadReadyFactory();

        var response = await factory.CreateClient().SendAsync(
            Create(ValidRequest(), Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await ReadJson(response);
        Assert.Equal("PUT", body.GetProperty("uploadMethod").GetString());
        Assert.Equal(ValidSha256, body.GetProperty("expectedContentSha256").GetString());
        Assert.StartsWith(
            "https://upload.example.invalid/",
            body.GetProperty("uploadUrl").GetString(),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(104_857_601)]
    public async Task A_size_outside_the_capture_limits_is_a_422(long sizeBytes)
    {
        using var factory = new UploadReadyFactory();

        var response = await factory.CreateClient().SendAsync(Create(new
        {
            folderId = "folder-fixture-0001",
            originalName = "too-big.pdf",
            sizeBytes,
            contentSha256 = ValidSha256,
        }, Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(
            "urn:lapluma:problem:upload-session-invalid",
            (await ReadJson(response)).GetProperty("type").GetString());
    }

    [Theory]
    [InlineData("not-a-digest")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    public async Task A_malformed_digest_is_rejected_before_any_url_is_minted(string digest)
    {
        // Uppercase hex included: the contract's pattern is lowercase, and the completion
        // comparison is exact, so accepting a variant spelling here would fail later and worse.
        using var factory = new UploadReadyFactory();

        var response = await factory.CreateClient().SendAsync(Create(new
        {
            folderId = "folder-fixture-0001",
            originalName = "scan.pdf",
            sizeBytes = 1024,
            contentSha256 = digest,
        }, Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
    }

    [Fact]
    public async Task Completing_a_session_returns_a_receipt_and_absorbs_the_retry()
    {
        using var factory = new UploadReadyFactory();
        var client = factory.CreateClient();
        var created = await ReadJson(await client.SendAsync(
            Create(ValidRequest(), Guid.NewGuid().ToString())));
        var sessionId = created.GetProperty("sessionId").GetString()!;
        var completeKey = Guid.NewGuid().ToString();

        var first = await client.SendAsync(Complete(sessionId, completeKey));
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var receipt = await ReadJson(first);
        Assert.Equal("SCANNING", receipt.GetProperty("processingState").GetString());
        Assert.Equal(ValidSha256, receipt.GetProperty("contentSha256").GetString());

        // Same key: the retry the idempotency contract promises to absorb.
        var replay = await client.SendAsync(Complete(sessionId, completeKey));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);

        // Different key: a second caller consuming someone else's session is a conflict.
        var conflict = await client.SendAsync(Complete(sessionId, Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal(
            "urn:lapluma:problem:upload-session-consumed",
            (await ReadJson(conflict)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task An_expired_session_cannot_be_completed()
    {
        using var factory = new UploadReadyFactory();
        var client = factory.CreateClient();
        var created = await ReadJson(await client.SendAsync(
            Create(ValidRequest(), Guid.NewGuid().ToString())));
        var sessionId = created.GetProperty("sessionId").GetString()!;

        factory.Clock.Advance(TimeSpan.FromMinutes(16));

        var response = await client.SendAsync(Complete(sessionId, Guid.NewGuid().ToString()));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(
            "urn:lapluma:problem:upload-session-expired",
            (await ReadJson(response)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Completing_an_unknown_session_is_the_same_404_as_everything_else()
    {
        using var factory = new UploadReadyFactory();

        var response = await factory.CreateClient().SendAsync(
            Complete("upload-does-not-exist", Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            "urn:lapluma:problem:not-found",
            (await ReadJson(response)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Without_configured_storage_the_session_is_a_typed_503_not_a_broken_url()
    {
        // The AuthenticatedFactory has no issuer substituted, so this exercises the fail-closed
        // NotConfiguredUploadUrlIssuer that a real unconfigured deployment would run.
        using var factory = new AuthenticatedFactory();

        var response = await factory.CreateClient().SendAsync(
            Create(ValidRequest(), Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(
            "urn:lapluma:problem:upload-not-configured",
            (await ReadJson(response)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task Creating_a_session_is_idempotent_by_key()
    {
        using var factory = new UploadReadyFactory();
        var client = factory.CreateClient();
        var key = Guid.NewGuid().ToString();

        var first = await ReadJson(await client.SendAsync(Create(ValidRequest(), key)));
        var replay = await ReadJson(await client.SendAsync(Create(ValidRequest(), key)));
        Assert.Equal(
            first.GetProperty("sessionId").GetString(),
            replay.GetProperty("sessionId").GetString());

        var conflict = await client.SendAsync(Create(new
        {
            folderId = "folder-fixture-0001",
            originalName = "a-different-file.pdf",
            sizeBytes = 2048,
            contentSha256 = ValidSha256,
        }, key));
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
    }
}
