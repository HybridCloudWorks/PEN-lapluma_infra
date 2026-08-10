using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LaPluma.CoreApi.Tests;

/// <summary>
/// The parts of the SQL migration that can be checked without a database.
///
/// <see cref="SqlCatalogSource"/>'s queries cannot: no environment has been provisioned, so nothing
/// here executes SQL. What is testable is the selection — which catalog a given configuration
/// serves — and the wire-name mapping the reader depends on, and both are where a silent wrong
/// answer would come from.
/// </summary>
public sealed class CatalogSourceTests
{
    private static IServiceProvider Build(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)))
            .Build();
        var services = new ServiceCollection();
        services.AddCatalogSource(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void The_default_source_is_sql_rather_than_the_fixture()
    {
        // The heart of it. A deployment that forgot to configure its database must fail, not serve
        // a plausible hard-coded catalog — a wrong answer that looks right is worse than an outage,
        // because nothing in the response says so.
        var provider = Build(
            (CatalogSourceRegistration.SqlServerSetting, "sql-example.database.windows.net"),
            (CatalogSourceRegistration.SqlDatabaseSetting, "lapluma"));

        Assert.IsType<SqlCatalogSource>(provider.GetRequiredService<ICatalogSource>());
    }

    [Fact]
    public void The_fixture_is_served_only_when_it_is_asked_for_by_name()
    {
        var provider = Build(
            (CatalogSourceRegistration.SourceSetting, CatalogSourceRegistration.FixtureSource));

        Assert.IsType<CatalogRepository>(provider.GetRequiredService<ICatalogSource>());
    }

    [Fact]
    public void An_unrecognised_source_is_refused_rather_than_guessed()
    {
        // A typo must not silently select a catalog nobody chose.
        var exception = Assert.Throws<InvalidOperationException>(() =>
            Build((CatalogSourceRegistration.SourceSetting, "postgres")));

        Assert.Contains("must be", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Sql_without_a_server_or_database_fails_at_startup()
    {
        // Fail while starting, not on the first request. A replica that starts and then 500s on
        // every catalog call passes its liveness probe and stays in rotation.
        Assert.Throws<InvalidOperationException>(() =>
            Build((CatalogSourceRegistration.SourceSetting, CatalogSourceRegistration.SqlSource)));
    }

    [Fact]
    public void The_connection_string_carries_no_password_and_authenticates_with_entra()
    {
        var options = CatalogSourceRegistration.BuildSqlOptions(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [CatalogSourceRegistration.SqlServerSetting] = "sql-example.database.windows.net",
                [CatalogSourceRegistration.SqlDatabaseSetting] = "lapluma",
            }).Build());

        // The server sets azureADOnlyAuthentication, so a password would be refused anyway — but a
        // password appearing here at all would mean a secret had entered configuration.
        Assert.DoesNotContain("Password", options.ConnectionString, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Pwd", options.ConnectionString, StringComparison.OrdinalIgnoreCase);

        // Parsed rather than string-matched: the builder renders the enum as ActiveDirectoryDefault
        // with no spaces, so a substring assertion would be testing its formatting, not the intent.
        var parsed = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(options.ConnectionString);
        Assert.Equal(Microsoft.Data.SqlClient.SqlAuthenticationMethod.ActiveDirectoryDefault, parsed.Authentication);
        Assert.True(parsed.Encrypt);
        Assert.False(parsed.TrustServerCertificate);
    }

    [Fact]
    public void The_managed_identity_client_id_is_passed_through_when_present()
    {
        // Four user-assigned identities exist; without this the credential has to guess, and on a
        // host with more than one attached it fails.
        var options = CatalogSourceRegistration.BuildSqlOptions(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [CatalogSourceRegistration.SqlServerSetting] = "sql-example.database.windows.net",
                [CatalogSourceRegistration.SqlDatabaseSetting] = "lapluma",
                [CatalogSourceRegistration.ManagedIdentityClientIdSetting] = "11111111-2222-3333-4444-555555555555",
            }).Build());

        Assert.Contains("11111111-2222-3333-4444-555555555555", options.ConnectionString, StringComparison.Ordinal);
    }
}

/// <summary>
/// The database stores wire names, the contract publishes wire names, and the C# members are named
/// differently from both. A second hand-written mapping would be a place for them to disagree, and
/// the disagreement would surface as a form silently classified as something it is not.
/// </summary>
public sealed class CatalogWireNameTests
{
    [Theory]
    [InlineData("OFFICIAL_PDF", FormArtifactKind.OfficialPdf)]
    [InlineData("EXTERNAL_WORKFLOW", FormArtifactKind.ExternalWorkflow)]
    [InlineData("PROPRIETARY_FORM", FormArtifactKind.ProprietaryForm)]
    [InlineData("AUTHORED_TEMPLATE", FormArtifactKind.AuthoredTemplate)]
    public void Artifact_kinds_round_trip_from_the_contract_name(string wire, FormArtifactKind expected) =>
        Assert.Equal(expected, CatalogWireNames.Parse<FormArtifactKind>(wire));

    [Theory]
    [InlineData("UNAVAILABLE", FormActivationState.Unavailable)]
    [InlineData("CATALOG_ONLY", FormActivationState.CatalogOnly)]
    [InlineData("ASSISTED", FormActivationState.Assisted)]
    [InlineData("PILOT", FormActivationState.Pilot)]
    public void Activation_states_round_trip_from_the_contract_name(string wire, FormActivationState expected) =>
        Assert.Equal(expected, CatalogWireNames.Parse<FormActivationState>(wire));

    [Fact]
    public void A_value_outside_the_contract_is_not_silently_mapped()
    {
        // The columns carry CHECK constraints holding the same sets. A value outside them means the
        // database and the contract have diverged, which the reader turns into an exception rather
        // than a default — defaulting would classify an unknown artifact as an official PDF.
        Assert.Null(CatalogWireNames.Parse<FormArtifactKind>("SOMETHING_ELSE"));
        Assert.Null(CatalogWireNames.Parse<FormActivationState>("pilot"));
    }

    [Fact]
    public void Every_enum_member_has_a_contract_name()
    {
        // A member added without the attribute would serialise in PascalCase and be unreadable from
        // the database, and neither failure appears until something reads that specific value.
        foreach (var (type, names) in new (Type, IReadOnlyCollection<string>)[]
                 {
                     (typeof(FormArtifactKind), CatalogWireNames.Names<FormArtifactKind>()),
                     (typeof(FormFillCapability), CatalogWireNames.Names<FormFillCapability>()),
                     (typeof(FormActivationState), CatalogWireNames.Names<FormActivationState>()),
                     (typeof(FormEncoding), CatalogWireNames.Names<FormEncoding>()),
                     (typeof(ExtractedFieldValueType), CatalogWireNames.Names<ExtractedFieldValueType>()),
                 })
        {
            Assert.Equal(Enum.GetValues(type).Length, names.Count);
        }
    }
}
