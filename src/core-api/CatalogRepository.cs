namespace LaPluma.CoreApi;

// The in-memory fixture. It is no longer what production serves — SqlCatalogSource is — but it
// remains the contract tests' catalog, and tools/validate_foundation.py reads this file as text for
// the Alpha 0.2 priority forms and package composition. Moving these literals breaks that check.
public sealed class CatalogRepository : ICatalogSource
{
    private static readonly CatalogCategory Federal = new("FEDERAL", "Federal", 10);
    private static readonly CatalogCategory Education = new("EDUCATION", "Education", 20);

    private static readonly CatalogSubcategory Immigration =
        new("IMMIGRATION", Federal.Code, "Immigration", 10);
    private static readonly CatalogSubcategory Passport =
        new("PASSPORT", Federal.Code, "Passport", 20);
    private static readonly CatalogSubcategory FinancialAid =
        new("FINANCIAL_AID", Education.Code, "Financial aid", 10);

    private static readonly Uri PlaceholderSource = new("https://example.invalid/official-source");
    private static readonly DateTimeOffset FixtureVerifiedAt =
        new(2026, 8, 2, 0, 0, 0, TimeSpan.Zero);

    // Sprint 2 is deliberately fail-closed. These are priority fixtures, not claims that an
    // edition has been acquired or approved. Acquisition creates a new immutable edition record.
    private static readonly IReadOnlyList<FormPackage> Packages =
    [
        Package(
            "FAMILY_I130", "Petition for Alien Relative", Federal, Immigration, "USCIS",
            Form("I-130", "Petition for Alien Relative", FormArtifactKind.OfficialPdf,
                FormFillCapability.AutomaticFill, FormActivationState.CatalogOnly),
            Form("I-130A", "Supplemental Information for Spouse Beneficiary",
                FormArtifactKind.OfficialPdf, FormFillCapability.AutomaticFill,
                FormActivationState.CatalogOnly)),
        Package(
            "ADJUSTMENT_I485_I864", "Adjustment of Status with Affidavit of Support",
            Federal, Immigration, "USCIS",
            Form("I-485", "Application to Register Permanent Residence or Adjust Status",
                FormArtifactKind.OfficialPdf, FormFillCapability.AutomaticFill,
                FormActivationState.CatalogOnly),
            Form("I-864", "Affidavit of Support Under Section 213A of the INA",
                FormArtifactKind.OfficialPdf, FormFillCapability.AutomaticFill,
                FormActivationState.CatalogOnly)),
        Package(
            "NATURALIZATION_N400", "Application for Naturalization", Federal, Immigration, "USCIS",
            Form("N-400", "Application for Naturalization", FormArtifactKind.OfficialPdf,
                FormFillCapability.AutomaticFill, FormActivationState.CatalogOnly)),
        Package(
            "EAD_I765", "Application for Employment Authorization", Federal, Immigration, "USCIS",
            Form("I-765", "Application for Employment Authorization", FormArtifactKind.OfficialPdf,
                FormFillCapability.AutomaticFill, FormActivationState.Unavailable)),
        Package(
            "TRAVEL_I131",
            "Application for Travel Documents, Parole Documents, and Arrival/Departure Records",
            Federal, Immigration, "USCIS",
            Form("I-131",
                "Application for Travel Documents, Parole Documents, and Arrival/Departure Records",
                FormArtifactKind.OfficialPdf, FormFillCapability.AutomaticFill,
                FormActivationState.CatalogOnly)),
        Package(
            "PASSPORT_DS11", "DS-11", Federal, Passport, "U.S. Department of State",
            Form("DS-11", "Application for a U.S. Passport", FormArtifactKind.OfficialPdf,
                FormFillCapability.AutomaticFill, FormActivationState.CatalogOnly)),
        Package(
            "FINANCIAL_AID_FAFSA", "FAFSA", Education, FinancialAid,
            "U.S. Department of Education",
            Form("FAFSA", "Free Application for Federal Student Aid",
                FormArtifactKind.ExternalWorkflow, FormFillCapability.ReferenceOnly,
                FormActivationState.Unavailable))
    ];

    public IReadOnlyList<CatalogCategoryNode> GetHierarchy() =>
    [
        new(Federal, [Immigration, Passport]),
        new(Education, [FinancialAid])
    ];

    static CatalogRepository()
    {
        // Codes are compared ordinally, so two codes differing only by case would both be
        // reachable and the lookup would be ambiguous. Fail at startup rather than on a request.
        var duplicates = Packages
            .GroupBy(package => package.PackageCode, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException(
                $"Duplicate catalog package codes: {string.Join(", ", duplicates)}");
        }
    }

    public IReadOnlyList<FormPackage> ListPackages(
        string? categoryCode,
        string? subcategoryCode,
        FormActivationState? activationState) =>
        Packages
            .Where(package => categoryCode is null ||
                package.Category.Code.Equals(categoryCode, StringComparison.Ordinal))
            .Where(package => subcategoryCode is null ||
                package.Subcategory.Code.Equals(subcategoryCode, StringComparison.Ordinal))
            .Where(package => activationState is null || package.ActivationState == activationState)
            .OrderBy(package => package.Category.SortOrder)
            .ThenBy(package => package.Subcategory.SortOrder)
            .ThenBy(package => package.PackageCode, StringComparer.Ordinal)
            .ToArray();

    // Codes are validated against the contract's pattern before reaching here, so the comparison is
    // ordinal. FirstOrDefault rather than SingleOrDefault: uniqueness is asserted at startup, and a
    // lookup should not be the thing that throws.
    public FormPackage? GetPackage(string packageCode) =>
        Packages.FirstOrDefault(package =>
            package.PackageCode.Equals(packageCode, StringComparison.Ordinal));

    public ExtractedFormSchema? GetSchema(
        string authority,
        string formId,
        DateOnly editionDate,
        string schemaVersion)
    {
        // No schema is activated until catalog acquisition, immutable edition metadata, and the
        // two-person field-map approval gate exist. Returning 404 is the fail-closed contract.
        _ = authority;
        _ = formId;
        _ = editionDate;
        _ = schemaVersion;
        return null;
    }

    private static FormPackage Package(
        string code,
        string title,
        CatalogCategory category,
        CatalogSubcategory subcategory,
        string agency,
        params CatalogForm[] forms) =>
        new(
            code,
            title,
            category,
            subcategory,
            agency,
            null,
            forms,
            null,
            null,
            PlaceholderSource,
            FixtureVerifiedAt);

    private static CatalogForm Form(
        string formNumber,
        string title,
        FormArtifactKind artifactKind,
        FormFillCapability fillCapability,
        FormActivationState activationState)
    {
        var authority = formNumber switch
        {
            "I-130" or "I-130A" or "I-485" or "I-864" or "N-400" or "I-765" or "I-131" => "USCIS",
            "DS-11" => "U.S. Department of State",
            "FAFSA" => "Federal Student Aid",
            _ => throw new InvalidOperationException("Form is not in the lapluma-app-0.2 catalog")
        };
        // The agency publishes I-131 as an XFA document, not an AcroForm; deriving its encoding
        // from the fill capability would misstate an artifact property the package worker keys on.
        var encoding = formNumber == "I-131"
            ? FormEncoding.Xfa
            : fillCapability == FormFillCapability.AutomaticFill
                ? FormEncoding.AcroForm
                : FormEncoding.Flat;
        return new(
            formNumber,
            title,
            new DateOnly(1970, 1, 1),
            encoding,
            0,
            artifactKind,
            fillCapability,
            activationState,
            new FormSourceMetadata(
                authority,
                PlaceholderSource,
                null,
                "example.invalid",
                null,
                FixtureVerifiedAt));
    }

    Task<IReadOnlyList<CatalogCategoryNode>> ICatalogSource.GetHierarchyAsync(CancellationToken _) =>
        Task.FromResult(GetHierarchy());

    Task<IReadOnlyList<FormPackage>> ICatalogSource.ListPackagesAsync(
        string? categoryCode,
        string? subcategoryCode,
        FormActivationState? activationState,
        CancellationToken _) =>
        Task.FromResult(ListPackages(categoryCode, subcategoryCode, activationState));

    Task<FormPackage?> ICatalogSource.GetPackageAsync(string packageCode, CancellationToken _) =>
        Task.FromResult(GetPackage(packageCode));

    Task<ExtractedFormSchema?> ICatalogSource.GetSchemaAsync(
        string authority,
        string formId,
        DateOnly editionDate,
        string schemaVersion,
        CancellationToken _) =>
        Task.FromResult(GetSchema(authority, formId, editionDate, schemaVersion));
}
