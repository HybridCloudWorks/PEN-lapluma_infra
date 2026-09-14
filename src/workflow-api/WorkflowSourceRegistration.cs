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
    public const string PostgresSource = "postgres";
    public const string PostgreSqlSource = "postgresql";
    public const string PostgresConnectionStringSetting = "Database:ConnectionString";
    public const string AltPostgresConnectionStringSetting = "Workflow:PostgresConnectionString";

    public static IServiceCollection AddWorkflowSource(
        this IServiceCollection services, IConfiguration configuration)
    {
        var source = configuration[SourceSetting] ?? FixtureSource;

        if (string.Equals(source, FixtureSource, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<WorkflowFixtureSource>();
            services.AddSingleton<IWorkflowSource>(provider =>
                provider.GetRequiredService<WorkflowFixtureSource>());
            return services;
        }

        if (string.Equals(source, PostgresSource, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(source, PostgreSqlSource, StringComparison.OrdinalIgnoreCase))
        {
            var connStr = configuration[PostgresConnectionStringSetting] ?? configuration[AltPostgresConnectionStringSetting];
            if (string.IsNullOrWhiteSpace(connStr))
            {
                throw new InvalidOperationException(
                    $"{PostgresConnectionStringSetting} is required when workflow source is '{PostgresSource}'.");
            }

            var dataSource = Npgsql.NpgsqlDataSource.Create(connStr);
            services.AddSingleton(dataSource);
            services.AddSingleton<IWorkflowSource, PostgresWorkflowSource>();
            return services;
        }

        throw new InvalidOperationException(
            $"{SourceSetting} must be '{FixtureSource}', '{PostgresSource}', or '{PostgreSqlSource}', not '{source}'.");
    }
}
