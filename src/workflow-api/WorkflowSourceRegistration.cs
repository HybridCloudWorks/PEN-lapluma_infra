namespace LaPluma.WorkflowApi;

/// <summary>
/// Chooses which workflow store the service serves.
///
/// Unlike the catalog — where SQL exists and is the default — no durable workflow store exists yet
/// (TODO 5.8), so there is nothing safe to default to: defaulting to the fixture would let a
/// deployment serve synthetic workflow state without ever having said so. The deployment must name
/// the fixture explicitly (compute.bicep does), and any other value, including none, refuses to
/// start.
/// </summary>
public static class WorkflowSourceRegistration
{
    public const string SourceSetting = "Workflow:Source";
    public const string FixtureSource = "fixture";

    public static IServiceCollection AddWorkflowSource(
        this IServiceCollection services, IConfiguration configuration)
    {
        var source = configuration[SourceSetting];
        if (!string.Equals(source, FixtureSource, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{SourceSetting} must be '{FixtureSource}' (the only implemented store, TODO 5.8 "
                + $"tracks the durable one), not '{source ?? "<unset>"}'.");
        }

        services.AddSingleton<WorkflowFixtureSource>();
        services.AddSingleton<IWorkflowSource>(provider =>
            provider.GetRequiredService<WorkflowFixtureSource>());
        return services;
    }
}
