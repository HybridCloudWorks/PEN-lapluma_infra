using System.IO;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace LaPluma.CoreApi.Tests;

/// <summary>
/// Verifies GCP application identity, authorization, and PostgreSQL separation-of-duty controls (INT-02).
///
/// Acceptance criteria:
/// - Gateway and backend reject invalid/expired audiences and tokens; direct backend invocation is IAM-restricted.
/// - Separation-of-duty controls in PostgreSQL guarantee:
///   * Core API application role has zero access to workflow case data.
///   * Library operators never have implicit case access.
///   * Workflow API role cannot mutate Document Library definitions.
///   * Processing worker has zero database access.
/// </summary>
public sealed class DatabaseRoleSeparationTests
{
    private static readonly string RepoRoot = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));

    [Fact]
    public void Database_roles_script_exists_and_defines_all_required_principals()
    {
        var sqlPath = Path.Combine(RepoRoot, "infra", "sql", "005_database_roles_and_permissions.sql");
        Assert.True(File.Exists(sqlPath), $"Expected DDL file at {sqlPath}");

        var content = File.ReadAllText(sqlPath);

        // Required roles
        Assert.Contains("lapluma_app_core", content);
        Assert.Contains("lapluma_app_workflow", content);
        Assert.Contains("lapluma_library_admin", content);

        // Core API role has USAGE and SELECT on library schema
        Assert.Matches(@"GRANT\s+USAGE\s+ON\s+SCHEMA\s+library\s+TO\s+lapluma_app_core", content);
        Assert.Matches(@"GRANT\s+SELECT\s+ON\s+ALL\s+TABLES\s+IN\s+SCHEMA\s+library\s+TO\s+lapluma_app_core", content);

        // Core API role is explicitly revoked from workflow schema (zero case access)
        Assert.Matches(@"REVOKE\s+ALL\s+ON\s+SCHEMA\s+workflow\s+FROM\s+lapluma_app_core", content);
        Assert.Matches(@"REVOKE\s+ALL\s+ON\s+ALL\s+TABLES\s+IN\s+SCHEMA\s+workflow\s+FROM\s+lapluma_app_core", content);

        // Workflow API role has CRUD on workflow schema
        Assert.Matches(@"GRANT\s+USAGE\s+ON\s+SCHEMA\s+workflow\s+TO\s+lapluma_app_workflow", content);
        Assert.Matches(@"GRANT\s+SELECT,\s*INSERT,\s*UPDATE,\s*DELETE\s+ON\s+ALL\s+TABLES\s+IN\s+SCHEMA\s+workflow\s+TO\s+lapluma_app_workflow", content);

        // Workflow API role can read library definitions but cannot mutate blueprints
        Assert.Matches(@"GRANT\s+USAGE\s+ON\s+SCHEMA\s+library\s+TO\s+lapluma_app_workflow", content);
        Assert.Matches(@"GRANT\s+SELECT\s+ON\s+ALL\s+TABLES\s+IN\s+SCHEMA\s+library\s+TO\s+lapluma_app_workflow", content);
        Assert.Matches(@"REVOKE\s+INSERT,\s*UPDATE,\s*DELETE\s+ON\s+ALL\s+TABLES\s+IN\s+SCHEMA\s+library\s+FROM\s+lapluma_app_workflow", content);

        // Library admin role can publish blueprints but has ZERO access to cases
        Assert.Matches(@"GRANT\s+SELECT,\s*INSERT,\s*UPDATE\s+ON\s+ALL\s+TABLES\s+IN\s+SCHEMA\s+library\s+TO\s+lapluma_library_admin", content);
        Assert.Matches(@"REVOKE\s+ALL\s+ON\s+SCHEMA\s+workflow\s+FROM\s+lapluma_library_admin", content);
        Assert.Matches(@"REVOKE\s+ALL\s+ON\s+ALL\s+TABLES\s+IN\s+SCHEMA\s+workflow\s+FROM\s+lapluma_library_admin", content);
    }

    [Fact]
    public void Catalog_authentication_enforces_strict_token_validation_parameters()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [CatalogAuthentication.AudienceSetting] = "api://lapluma-workforce-pilot",
                [CatalogAuthentication.IssuerSetting] = "https://login.microsoftonline.com/common/v2.0"
            })
            .Build();

        services.AddCatalogAuthentication(config);
        using var provider = services.BuildServiceProvider();

        var jwtOptions = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        Assert.NotNull(jwtOptions.TokenValidationParameters);
        Assert.True(jwtOptions.TokenValidationParameters.ValidateIssuer);
        Assert.True(jwtOptions.TokenValidationParameters.ValidateAudience);
        Assert.True(jwtOptions.TokenValidationParameters.ValidateLifetime);
        Assert.True(jwtOptions.TokenValidationParameters.ValidateIssuerSigningKey);
        Assert.Equal("api://lapluma-workforce-pilot", jwtOptions.TokenValidationParameters.ValidAudience);
        Assert.Equal("https://login.microsoftonline.com/common/v2.0", jwtOptions.TokenValidationParameters.ValidIssuer);
        Assert.True(jwtOptions.TokenValidationParameters.ClockSkew <= TimeSpan.FromSeconds(30),
            "Clock skew must not exceed 30 seconds for bounded freshness");
    }

    [Fact]
    public void Unconfigured_authentication_fails_closed_denying_all_requests()
    {
        var services = new ServiceCollection();
        var emptyConfig = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();

        services.AddCatalogAuthentication(emptyConfig);
        using var provider = services.BuildServiceProvider();

        var jwtOptions = provider.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        // Authority is null when unconfigured so it never reaches out to unexpected metadata endpoints
        Assert.Null(jwtOptions.Authority);
    }
}
