using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace LaPluma.WorkflowApi.Tests;

/// <summary>
/// The progress-language invariant, checked against what the service actually serialises.
///
/// The app's architecture handoff prohibits percentages and completion scores on every surface, and
/// the DevSecOps invariant gate lists "percentage key present" among the failures that cannot be
/// overridden. The reason is a safety one: an applicant reads a completion percentage as a
/// prediction that the case will be approved, which is the eligibility judgement this product must
/// never appear to make. What the dashboard gets instead is exact counts.
///
/// <c>tools/validate_foundation.py</c> enforces the same rule statically over the contracts and the
/// wire models. This suite covers what that cannot see: a computed property, a serializer naming
/// policy, or a dictionary key assembled at run time, none of which appear in a declaration.
/// </summary>
public sealed class ProgressLanguageInvariantTests : IClassFixture<AuthenticatedFactory>
{
    private readonly AuthenticatedFactory factory;

    public ProgressLanguageInvariantTests(AuthenticatedFactory factory) => this.factory = factory;

    // Mirrors PERCENTAGE_LIKE in tools/validate_foundation.py. Separators are stripped before the
    // match, so pct_complete and pctComplete are the same field to this rule as to a reader.
    private static readonly Regex PercentageLike =
        new("percent|pct|completion(score|rate|ratio)|progress(score|ratio)", RegexOptions.Compiled);

    private static string Normalise(string name) =>
        Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]", "");

    /// <summary>
    /// Read a response, prove it succeeded, and parse it.
    ///
    /// The status assertion is what stops this suite passing for the wrong reason. A problem
    /// document carries no percentage-like key either, so a sweep over a regressed endpoint's 404
    /// or 500 body would come back clean and report the invariant held. The body travels into the
    /// assertion message so a failure names what came back rather than only the code.
    /// </summary>
    private static async Task<JsonDocument> SucceedingJson(HttpResponseMessage response, string what)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(
            response.IsSuccessStatusCode,
            $"{what} returned {(int)response.StatusCode}, so the sweep would prove nothing: {body}");
        return JsonDocument.Parse(body);
    }

    /// <summary>Every JSON property name in a response, at any depth.</summary>
    private static IEnumerable<string> PropertyNames(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (var nested in PropertyNames(property.Value))
                    {
                        yield return nested;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var nested in PropertyNames(item))
                    {
                        yield return nested;
                    }
                }

                break;
        }
    }

    public static TheoryData<string> ImplementedReadSurfaces() =>
    [
        "/health",
        "/ready",
        "/v1/session",
        "/v1/clients",
        "/v1/cases/case-fixture-0001/workspace",
    ];

    [Theory]
    [MemberData(nameof(ImplementedReadSurfaces))]
    public async Task No_response_carries_a_percentage_like_field(string path)
    {
        var response = await factory.CreateClient().GetAsync(path);
        using var document = await SucceedingJson(response, path);

        var offending = PropertyNames(document.RootElement)
            .Where(name => PercentageLike.IsMatch(Normalise(name)))
            .ToArray();

        Assert.True(offending.Length == 0, $"{path} returned percentage-like fields: {string.Join(", ", offending)}");
    }

    [Fact]
    public async Task A_created_resource_carries_no_percentage_like_field_either()
    {
        // The write path serialises the same models, but through a different result type, so a
        // read-only sweep would not see a field introduced on creation.
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/clients")
        {
            Content = JsonContent.Create(new { displayLabel = "Invariant Sweep Client" }),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        var response = await factory.CreateClient().SendAsync(request);
        using var document = await SucceedingJson(response, "POST /v1/clients");

        Assert.DoesNotContain(
            PropertyNames(document.RootElement), name => PercentageLike.IsMatch(Normalise(name)));
    }

    [Fact]
    public async Task The_upload_session_pair_carries_no_percentage_like_field()
    {
        // The upload models are serialised by the same policy but reached only with a mintable
        // issuer, so the sweeps above never touch them. UploadSession and UploadReceipt are the
        // two shapes a percentage could appear on without any other test noticing.
        using var uploadFactory = new UploadReadyFactory();
        var client = uploadFactory.CreateClient();

        var create = new HttpRequestMessage(HttpMethod.Post, "/v1/documents/upload-sessions")
        {
            Content = JsonContent.Create(new
            {
                folderId = "folder-fixture-0001",
                originalName = "invariant-sweep.pdf",
                sizeBytes = 2048,
                contentSha256 = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
            }),
        };
        create.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var session = await SucceedingJson(
            await client.SendAsync(create), "POST /v1/documents/upload-sessions");
        Assert.DoesNotContain(
            PropertyNames(session.RootElement), name => PercentageLike.IsMatch(Normalise(name)));

        var sessionId = session.RootElement.GetProperty("sessionId").GetString();
        var complete = new HttpRequestMessage(
            HttpMethod.Post, $"/v1/documents/upload-sessions/{sessionId}/complete");
        complete.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        using var receipt = await SucceedingJson(
            await client.SendAsync(complete), "POST /v1/documents/upload-sessions/{id}/complete");
        Assert.DoesNotContain(
            PropertyNames(receipt.RootElement), name => PercentageLike.IsMatch(Normalise(name)));
    }

    [Fact]
    public async Task Progress_is_reported_as_the_mechanical_counters_the_contract_names()
    {
        // The invariant is only meaningful if something concrete replaces the percentage. Asserting
        // the counters are present stops the field being dropped altogether and the check above
        // passing on an empty object.
        var response = await factory.CreateClient().GetAsync("/v1/clients");
        using var document = await SucceedingJson(response, "/v1/clients");

        var counters = document.RootElement
            .GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("primaryCase"))
            .First(primaryCase => primaryCase.ValueKind == JsonValueKind.Object)
            .GetProperty("counters");

        foreach (var expected in new[]
                 {
                     "fieldsFilled", "fieldsRequired", "documentsCollected",
                     "documentsRequired", "blockingItems", "advisoryItems",
                 })
        {
            Assert.True(counters.TryGetProperty(expected, out var value), $"missing counter: {expected}");
            Assert.Equal(JsonValueKind.Number, value.ValueKind);
        }
    }

    [Fact]
    public void The_sweep_would_catch_a_percentage_field_if_one_appeared()
    {
        // A sweep that cannot fail is not a gate. This proves the walker descends into nested
        // objects and arrays rather than only reading the root.
        using var planted = JsonDocument.Parse(
            """{"items":[{"primaryCase":{"counters":{"fieldsFilled":1,"percentComplete":42}}}]}""");

        var offending = PropertyNames(planted.RootElement)
            .Where(name => PercentageLike.IsMatch(Normalise(name)))
            .ToArray();

        Assert.Equal(["percentComplete"], offending);
    }
}
