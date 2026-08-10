using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace LaPluma.CoreApi.Tests;

/// <summary>
/// Tests that hold the implementation to the published contract and to the derivation rules,
/// without going through HTTP.
/// </summary>
public sealed class CatalogContractTests
{
    [Fact]
    public void A_package_with_no_forms_is_unavailable_rather_than_pilot()
    {
        // The derivation reads "as activated as its weakest form". With no forms every branch is
        // false, so without an explicit empty case it falls through to PILOT — the most permissive
        // state, and one that permits case creation.
        var package = PackageWith(Array.Empty<CatalogForm>());

        Assert.Equal(FormActivationState.Unavailable, package.ActivationState);
    }

    [Theory]
    [InlineData(FormActivationState.Unavailable, FormActivationState.Unavailable)]
    [InlineData(FormActivationState.CatalogOnly, FormActivationState.CatalogOnly)]
    [InlineData(FormActivationState.Assisted, FormActivationState.Assisted)]
    [InlineData(FormActivationState.Pilot, FormActivationState.Pilot)]
    public void A_single_form_package_takes_that_form_state(
        FormActivationState formState, FormActivationState expected)
    {
        var package = PackageWith(new[] { FormWith(formState) });

        Assert.Equal(expected, package.ActivationState);
    }

    [Fact]
    public void A_package_is_only_as_activated_as_its_weakest_form()
    {
        var package = PackageWith(new[]
        {
            FormWith(FormActivationState.Pilot),
            FormWith(FormActivationState.CatalogOnly),
            FormWith(FormActivationState.Assisted),
        });

        Assert.Equal(FormActivationState.CatalogOnly, package.ActivationState);
    }

    [Theory]
    [InlineData(typeof(FormArtifactKind), "FormArtifactKind")]
    [InlineData(typeof(FormFillCapability), "FormFillCapability")]
    [InlineData(typeof(FormActivationState), "FormActivationState")]
    [InlineData(typeof(FormEncoding), "FormEncoding")]
    public void Enum_wire_names_match_the_published_contract(Type enumType, string schemaName)
    {
        // Three copies of each enum exist — the C# type, the OpenAPI schema, and the iOS client.
        // This pins the two that live in this repository to each other.
        var declared = enumType.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => field.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name
                             ?? field.Name)
            .ToArray();

        var published = OpenApiSchemas().GetProperty(schemaName).GetProperty("enum")
            .EnumerateArray().Select(value => value.GetString()).ToArray();

        Assert.Equal(published, declared);
    }

    [Fact]
    public void Every_enum_member_declares_an_explicit_wire_name()
    {
        // A member without the attribute would serialise in PascalCase and break the client
        // silently, so require the attribute rather than inferring a name.
        var missing = new[]
            {
                typeof(FormArtifactKind), typeof(FormFillCapability),
                typeof(FormActivationState), typeof(FormEncoding),
                typeof(ExtractedFieldValueType),
            }
            .SelectMany(type => type.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(field => field.GetCustomAttribute<JsonStringEnumMemberNameAttribute>() is null)
            .Select(field => $"{field.DeclaringType!.Name}.{field.Name}")
            .ToArray();

        Assert.Empty(missing);
    }

    private static JsonElement OpenApiSchemas()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "contracts", "catalog.openapi.json")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        var path = Path.Combine(directory!.FullName, "contracts", "catalog.openapi.json");
        return JsonDocument.Parse(File.ReadAllText(path))
            .RootElement.GetProperty("components").GetProperty("schemas");
    }

    private static FormPackage PackageWith(IReadOnlyList<CatalogForm> forms) =>
        new(
            "TEST_PACKAGE",
            "Test package",
            new CatalogCategory("FEDERAL", "Federal", 10),
            new CatalogSubcategory("IMMIGRATION", "FEDERAL", "Immigration", 10),
            "USCIS",
            null,
            forms,
            null,
            null,
            new Uri("https://example.invalid/official-source"),
            DateTimeOffset.UnixEpoch);

    private static CatalogForm FormWith(FormActivationState state) =>
        new(
            "I-130",
            "Petition for Alien Relative",
            new DateOnly(1970, 1, 1),
            FormEncoding.AcroForm,
            0,
            FormArtifactKind.OfficialPdf,
            FormFillCapability.AutomaticFill,
            state,
            null);
}
