using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace LaPluma.WorkflowApi.Tests;

/// <summary>
/// Expiry is not only about refusing a late completion — it is about the session eventually leaving
/// memory. Nothing swept the store before these tests existed, so every session this process had
/// ever issued stayed for the life of the container, on a service pinned to one replica precisely
/// because its state is in memory. The window between "unusable" and "gone" is deliberate: a late
/// completion should be told it ran out of time, not that its session never existed.
/// </summary>
public sealed class UploadSessionRetentionTests
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

    private static HttpRequestMessage Create(string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/documents/upload-sessions")
        {
            Content = JsonContent.Create(ValidRequest()),
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

    /// <summary>
    /// Creates are the only thing that adds to the store, so they are what drives the sweep. This
    /// is the unrelated traffic that makes one run.
    /// </summary>
    private static Task<HttpResponseMessage> UnrelatedTraffic(HttpClient client) =>
        client.SendAsync(Create(Guid.NewGuid().ToString()));

    [Fact]
    public async Task An_expired_session_is_still_told_it_expired_while_inside_the_retention_window()
    {
        using var factory = new UploadReadyFactory();
        var client = factory.CreateClient();
        var sessionId = (await ReadJson(await client.SendAsync(Create(Guid.NewGuid().ToString()))))
            .GetProperty("sessionId").GetString()!;

        // Past the fifteen-minute lifetime, inside the fifteen-minute retention that follows it.
        factory.Clock.Advance(TimeSpan.FromMinutes(20));
        await UnrelatedTraffic(client);

        var response = await client.SendAsync(Complete(sessionId, Guid.NewGuid().ToString()));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(
            "urn:lapluma:problem:upload-session-expired",
            (await ReadJson(response)).GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_session_leaves_the_store_once_it_has_been_expired_past_the_retention_window()
    {
        using var factory = new UploadReadyFactory();
        var client = factory.CreateClient();
        var sessionId = (await ReadJson(await client.SendAsync(Create(Guid.NewGuid().ToString()))))
            .GetProperty("sessionId").GetString()!;

        // Lifetime plus retention, plus enough to be unambiguously past both.
        factory.Clock.Advance(TimeSpan.FromMinutes(31));
        await UnrelatedTraffic(client);

        var response = await client.SendAsync(Complete(sessionId, Guid.NewGuid().ToString()));

        // The session is gone rather than expired, which is the observable proof it was collected:
        // before the sweep existed this stayed 422 for the life of the process.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_key_whose_session_aged_out_starts_a_fresh_session_rather_than_replaying_a_dead_one()
    {
        using var factory = new UploadReadyFactory();
        var client = factory.CreateClient();
        var key = Guid.NewGuid().ToString();
        var first = (await ReadJson(await client.SendAsync(Create(key))))
            .GetProperty("sessionId").GetString()!;

        factory.Clock.Advance(TimeSpan.FromMinutes(31));

        var response = await client.SendAsync(Create(key));
        var second = await ReadJson(response);

        // A key retired alongside its session is a key that can be used again. Leaving it behind
        // would strand the caller: every replay would return an id that can never be completed.
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.NotEqual(first, second.GetProperty("sessionId").GetString());
        Assert.Equal(
            HttpStatusCode.OK,
            (await client.SendAsync(Complete(
                second.GetProperty("sessionId").GetString()!, Guid.NewGuid().ToString()))).StatusCode);
    }

    [Fact]
    public void The_sweep_leaves_a_live_session_and_its_key_where_they_are()
    {
        var clock = new TestClock();
        var store = new UploadSessionStore(clock);
        var key = Guid.NewGuid().ToString();
        var created = store.Create(key, Request());

        clock.Advance(TimeSpan.FromMinutes(14));
        store.SweepExpired();

        var replay = store.Create(key, Request());
        Assert.Equal(IdempotencyOutcome.Replayed, replay.Outcome);
        Assert.Equal(created.SessionId, replay.SessionId);
        Assert.Equal(CompleteUploadOutcome.Completed, store.Complete(created.SessionId, key).Outcome);
    }

    [Fact]
    public void A_create_racing_the_sweep_over_its_own_key_never_faults()
    {
        // The clock moves on every read, so sessions created early in the race are sweepable by the
        // time later calls look at them — which is the only way to reach the window between a
        // create reading its key and reading the session that key names. Resolving the session
        // through the map without allowing for that window threw KeyNotFoundException, turning a
        // routine collection into a 500 for whichever caller happened to be mid-flight.
        var store = new UploadSessionStore(new RatchetingClock(TimeSpan.FromMinutes(5)));
        var key = Guid.NewGuid().ToString();
        var faults = new System.Collections.Concurrent.ConcurrentBag<Exception>();
        var barrier = new Barrier(33);
        var threads = new List<Thread>();

        for (var i = 0; i < 32; i++)
        {
            threads.Add(Run(() =>
            {
                for (var attempt = 0; attempt < 16; attempt++)
                {
                    var result = store.Create(key, Request());
                    Assert.Contains(result.Outcome, new[]
                    {
                        IdempotencyOutcome.Created,
                        IdempotencyOutcome.Replayed,
                        IdempotencyOutcome.Conflict,
                    });
                }
            }));
        }

        threads.Add(Run(() =>
        {
            for (var pass = 0; pass < 64; pass++)
            {
                store.SweepExpired();
            }
        }));

        foreach (var thread in threads)
        {
            thread.Start();
        }

        foreach (var thread in threads)
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "a racer did not finish");
        }

        Assert.Empty(faults);

        Thread Run(Action body)
        {
            var thread = new Thread(() =>
            {
                barrier.SignalAndWait();
                try
                {
                    body();
                }
                catch (Exception exception)
                {
                    faults.Add(exception);
                }
            })
            { IsBackground = true };
            return thread;
        }
    }

    private static CreateUploadSessionRequest Request() =>
        new("folder-fixture-0001", null, "passport-scan.pdf", "application/pdf", 1_048_576, ValidSha256, null);

    /// <summary>A clock that steps forward on every read, so time passes inside a race.</summary>
    private sealed class RatchetingClock(TimeSpan step) : TimeProvider
    {
        private static readonly DateTimeOffset Origin = new(2026, 8, 26, 0, 0, 0, TimeSpan.Zero);
        private long reads;

        public override DateTimeOffset GetUtcNow() =>
            Origin + step * Interlocked.Increment(ref reads);
    }
}
