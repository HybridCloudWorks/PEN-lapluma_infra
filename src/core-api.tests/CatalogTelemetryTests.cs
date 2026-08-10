using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace LaPluma.CoreApi.Tests;

/// <summary>
/// The correlation identifier is what a user reports to support, so it has to exist somewhere a
/// operator can search — and the logs it appears in must carry no case content.
/// </summary>
public sealed class CatalogTelemetryTests
{
    [Fact]
    public async Task A_rejected_request_logs_once_with_the_identifier_it_returned()
    {
        using var factory = new CapturingFactory();

        var response = await factory.CreateClient()
            .GetAsync("/v1/catalog/packages?activationState=NOT_A_STATE");

        var correlationId = (await ReadJson(response)).GetProperty("correlationId").GetGuid();
        var logged = factory.ProblemEntries();
        Assert.Single(logged);
        Assert.Equal(LogLevel.Warning, logged[0].Level);
        Assert.Contains(correlationId.ToString(), logged[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_identifier_is_not_a_fresh_random_number_per_response()
    {
        using var factory = new CapturingFactory();
        var client = factory.CreateClient();

        var first = (await ReadJson(await client.GetAsync("/v1/catalog/packages/NO_SUCH_PACKAGE")))
            .GetProperty("correlationId").GetGuid();
        var second = (await ReadJson(await client.GetAsync("/v1/catalog/packages/NO_SUCH_PACKAGE")))
            .GetProperty("correlationId").GetGuid();

        // Distinct requests correlate to distinct traces, and neither is empty.
        Assert.NotEqual(Guid.Empty, first);
        Assert.NotEqual(Guid.Empty, second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task Problem_logs_carry_no_path_query_or_route_value()
    {
        // The content-free telemetry constraint. src/document-processing/worker.py suppresses all
        // request logging for the same reason; this asserts the equivalent for what this service
        // writes itself.
        using var factory = new CapturingFactory();

        await factory.CreateClient().GetAsync(
            "/v1/catalog/packages/DISTINCTIVE_MARKER?activationState=ANOTHER_MARKER");

        var logged = factory.ProblemEntries();
        Assert.NotEmpty(logged);
        foreach (var entry in logged)
        {
            Assert.DoesNotContain("DISTINCTIVE_MARKER", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("ANOTHER_MARKER", entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("/v1/catalog", entry.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_successful_request_logs_no_problem()
    {
        using var factory = new CapturingFactory();

        await factory.CreateClient().GetAsync("/v1/catalog/packages");

        Assert.Empty(factory.ProblemEntries());
    }

    [Fact]
    public async Task Readiness_exercises_the_catalog_rather_than_answering_from_a_literal()
    {
        using var factory = new CapturingFactory();

        var response = await factory.CreateClient().GetAsync("/ready");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ready", (await ReadJson(response)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Readiness_reports_not_ready_when_the_catalog_cannot_be_built()
    {
        // The failure this probe exists to catch: CatalogRepository builds its fixture in a static
        // constructor that throws on an unrecognised form number. A probe answering from a literal
        // stays green while every catalog route returns 500.
        using var factory = new CapturingFactory().WithBrokenCatalog();

        var response = await factory.CreateClient().GetAsync("/ready");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Liveness_stays_green_when_the_catalog_cannot_be_built()
    {
        // /health is liveness: the process is up. Conflating it with readiness would make an
        // orchestrator restart a replica that is running perfectly well.
        using var factory = new CapturingFactory().WithBrokenCatalog();

        var response = await factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private sealed record CapturedLog(string Category, LogLevel Level, string Message);

    private sealed class CapturingFactory : WebApplicationFactory<global::Program>
    {
        private readonly CapturingLoggerProvider provider = new();
        private bool breakCatalog;

        /// <summary>Stand in for a fixture that cannot initialise.</summary>
        public CapturingFactory WithBrokenCatalog()
        {
            breakCatalog = true;
            return this;
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureLogging(logging => logging.AddProvider(provider));
            if (breakCatalog)
            {
                builder.ConfigureServices(services => services.AddSingleton<CatalogRepository>(
                    _ => throw new InvalidOperationException("Unapproved Alpha 0.2 priority form")));
            }
        }

        /// <summary>
        /// Only this service's own problem logs. The framework's request logging is a separate
        /// category and is not what these tests are asserting about.
        /// </summary>
        public CapturedLog[] ProblemEntries() =>
            provider.Entries.Where(entry => entry.Category == CatalogProblem.LogCategory).ToArray();

        private sealed class CapturingLoggerProvider : ILoggerProvider
        {
            public ConcurrentBag<CapturedLog> Entries { get; } = [];

            public ILogger CreateLogger(string categoryName) => new Capturing(categoryName, Entries);

            public void Dispose()
            {
            }

            private sealed class Capturing(string category, ConcurrentBag<CapturedLog> entries) : ILogger
            {
                public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

                public bool IsEnabled(LogLevel logLevel) => true;

                public void Log<TState>(
                    LogLevel logLevel,
                    EventId eventId,
                    TState state,
                    Exception? exception,
                    Func<TState, Exception?, string> formatter) =>
                    entries.Add(new CapturedLog(category, logLevel, formatter(state, exception)));
            }
        }
    }
}
