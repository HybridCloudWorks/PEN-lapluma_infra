using System.Text.RegularExpressions;
using Xunit;

namespace LaPluma.WorkflowApi.Tests;

/// <summary>
/// Pins the implementation to the published contracts under <c>contracts/openapi/</c>. The
/// contracts are YAML and the repository is deliberately dependency-light, so these read the
/// documents as text the same way <c>tools/validate_foundation.py</c> does.
/// </summary>
public sealed class WorkflowContractTests
{
    [Theory]
    [InlineData("workforce-workflow.yaml")]
    [InlineData("documents-upload.yaml")]
    public void The_service_version_matches_the_contract(string contractFile)
    {
        var text = ContractText(contractFile);
        var match = Regex.Match(text, @"^\s*version:\s*(?<version>\S+)\s*$", RegexOptions.Multiline);

        Assert.True(match.Success, $"{contractFile} declares no version");
        Assert.Equal(ServiceMetadata.Version, match.Groups["version"].Value);
    }

    [Fact]
    public void The_upload_limits_match_the_published_contract()
    {
        // 104857600 and 255 are the app's capture limits, mirrored server-side. The contract and
        // the store must carry the same numbers or one of them is lying about what it accepts.
        var text = ContractText("documents-upload.yaml");

        Assert.Contains($"maximum: {UploadSessionStore.MaximumSizeBytes}", text, StringComparison.Ordinal);
        Assert.Contains(
            $"maxLength: {UploadSessionStore.MaximumOriginalNameLength}", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_digest_pattern_matches_the_published_contract()
    {
        var text = ContractText("documents-upload.yaml");

        Assert.Contains($"pattern: '{UploadSessionStore.ContentSha256()}'", text, StringComparison.Ordinal);
    }

    [Fact]
    public void The_contract_mirror_and_the_authored_contract_share_the_placeholder_host()
    {
        // No real hostname exists yet (REVIEW R-07 owns that decision); both contracts must keep
        // the reserved-by-RFC example.invalid placeholder until one does.
        foreach (var file in new[] { "workforce-workflow.yaml", "documents-upload.yaml" })
        {
            Assert.Contains(
                "https://api.example.invalid/v1", ContractText(file), StringComparison.Ordinal);
        }
    }

    private static string ContractText(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "contracts", "openapi", fileName)))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return File.ReadAllText(Path.Combine(directory!.FullName, "contracts", "openapi", fileName));
    }
}
