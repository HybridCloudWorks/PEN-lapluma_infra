using Microsoft.Data.SqlClient;

namespace LaPluma.CoreApi;

/// <summary>
/// Chooses which catalog the service serves.
///
/// The fixture is opt-in and SQL is the default, not the other way round. Defaulting to the fixture
/// would mean a deployment that failed to configure its database served a plausible catalog instead
/// of failing — and a catalog that looks right while being a hard-coded fixture is worse than an
/// outage, because nothing about the response says so.
/// </summary>
public static class CatalogSourceRegistration
{
    public const string SourceSetting = "Catalog:Source";
    public const string SqlServerSetting = "Catalog:SqlServer";
    public const string SqlDatabaseSetting = "Catalog:SqlDatabase";
    public const string ManagedIdentityClientIdSetting = "AZURE_CLIENT_ID";

    public const string FixtureSource = "fixture";
    public const string SqlSource = "sql";

    public static IServiceCollection AddCatalogSource(
        this IServiceCollection services, IConfiguration configuration)
    {
        var source = configuration[SourceSetting] ?? SqlSource;

        if (string.Equals(source, FixtureSource, StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<CatalogRepository>();
            services.AddSingleton<ICatalogSource>(provider => provider.GetRequiredService<CatalogRepository>());
            return services;
        }

        if (!string.Equals(source, SqlSource, StringComparison.OrdinalIgnoreCase))
        {
            // Neither value: refuse to guess. Picking one would mean a typo silently selected a
            // catalog nobody chose.
            throw new InvalidOperationException(
                $"{SourceSetting} must be '{FixtureSource}' or '{SqlSource}', not '{source}'.");
        }

        // Built now, not lazily. A factory closure would defer this to the first request, so a
        // deployment missing its server name would start, pass its liveness probe, stay in
        // rotation, and fail every catalog call. Registering the instance makes the failure happen
        // while the host is starting, where it is visible.
        services.AddSingleton(BuildSqlOptions(configuration));
        services.AddSingleton<ICatalogSource, SqlCatalogSource>();
        return services;
    }

    public static SqlCatalogOptions BuildSqlOptions(IConfiguration configuration)
    {
        var server = configuration[SqlServerSetting];
        var database = configuration[SqlDatabaseSetting];
        if (string.IsNullOrWhiteSpace(server) || string.IsNullOrWhiteSpace(database))
        {
            throw new InvalidOperationException(
                $"{SqlServerSetting} and {SqlDatabaseSetting} are required when the catalog source is '{SqlSource}'.");
        }

        // Assembled from a server and a database name, never read from a configured connection
        // string. A connection string is where a password would live, and there is no password:
        // the server sets azureADOnlyAuthentication, so only a token is accepted.
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,
            InitialCatalog = database,
            Authentication = SqlAuthenticationMethod.ActiveDirectoryDefault,
            Encrypt = true,
            TrustServerCertificate = false,
            ConnectTimeout = 15,
        };

        var clientId = configuration[ManagedIdentityClientIdSetting];
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            // Four user-assigned identities exist in this subscription; without this the credential
            // has to guess which one, and on a host with more than one attached it fails.
            builder.UserID = clientId;
        }

        return new SqlCatalogOptions { ConnectionString = builder.ConnectionString };
    }
}
