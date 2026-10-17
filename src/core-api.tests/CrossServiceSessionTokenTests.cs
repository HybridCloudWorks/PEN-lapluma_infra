extern alias workflowapi;

using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using WorkflowAuthentication = workflowapi::LaPluma.WorkflowApi.WorkflowAuthentication;
using WorkflowProgram = workflowapi::Program;
using WorkflowSourceRegistration = workflowapi::LaPluma.WorkflowApi.WorkflowSourceRegistration;

namespace LaPluma.CoreApi.Tests;

/// <summary>
/// The whole chain without the gateway: a signed assertion goes to the core API, the session
/// token it returns is accepted by the core API, and the workflow API accepts the same token
/// after fetching the core API's own <c>/.well-known/jwks.json</c>.
/// </summary>
public sealed class CrossServiceSessionTokenTests
{
    [Fact]
    public async Task A_token_from_a_verified_SAML_sign_in_is_accepted_by_both_APIs()
    {
        using var core = new SessionTokenFactory();
        var coreClient = core.CreateClient();
        var token = await core.SignInAsync();

        using var workflow = new WebApplicationFactory<WorkflowProgram>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(WorkflowSourceRegistration.SourceSetting, WorkflowSourceRegistration.FixtureSource);
            builder.UseSetting(WorkflowAuthentication.IssuerSetting, TestHost.Issuer);
            builder.UseSetting(WorkflowAuthentication.AudienceSetting, TestHost.Audience);
            builder.UseSetting(WorkflowAuthentication.JwksUriSetting, "https://core.example.invalid/.well-known/jwks.json");
            // The key set is served by the core API's real endpoint, in process.
            builder.ConfigureServices(services =>
                services.AddHttpClient(WorkflowAuthentication.JwksHttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => core.Server.CreateHandler()));
        });
        var workflowClient = workflow.CreateClient();

        coreClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        workflowClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var coreResponse = await coreClient.GetAsync("/v1/catalog/packages");
        var workflowResponse = await workflowClient.GetAsync("/v1/session");

        Assert.Equal(HttpStatusCode.OK, coreResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, workflowResponse.StatusCode);
        var session = await workflowResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("spatino@hybridcloudworks.com", session.GetProperty("userID").GetString());
    }

    [Fact]
    public async Task A_token_from_a_different_core_deployment_is_refused_by_the_workflow_API()
    {
        // Two deployments with the same issuer and audience but different keys: the workflow API
        // trusts only the key set it is pointed at.
        using var trusted = new SessionTokenFactory();
        using var other = new SessionTokenFactory();
        var token = await other.SignInAsync();

        using var workflow = new WebApplicationFactory<WorkflowProgram>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting(WorkflowSourceRegistration.SourceSetting, WorkflowSourceRegistration.FixtureSource);
            builder.UseSetting(WorkflowAuthentication.IssuerSetting, TestHost.Issuer);
            builder.UseSetting(WorkflowAuthentication.AudienceSetting, TestHost.Audience);
            builder.UseSetting(WorkflowAuthentication.JwksUriSetting, "https://core.example.invalid/.well-known/jwks.json");
            builder.ConfigureServices(services =>
                services.AddHttpClient(WorkflowAuthentication.JwksHttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => trusted.Server.CreateHandler()));
        });
        var client = workflow.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.GetAsync("/v1/session");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
