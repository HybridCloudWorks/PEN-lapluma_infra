using System.Collections.Concurrent;
using System.Text.Json;

namespace LaPluma.CoreApi;

/// <summary>
/// High-performance, memory-backed and database-aligned implementation of ILibraryAccessService.
/// Pre-seeded with baseline official Blueprints and synthetic multi-tenant fixtures.
/// Supports zero-deployment runtime additions of Collections and Blueprints.
/// </summary>
public sealed class LibraryAccessService : ILibraryAccessService
{
    private static readonly string[] SharedOfficialNamespaces = ["uscis", "official", "dos", "irs"];

    private readonly ConcurrentDictionary<string, InstitutionTenant> _tenants = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Namespace, string BlueprintId, int Revision), DocumentBlueprint> _blueprints = new();
    private readonly ConcurrentDictionary<(string Namespace, string CollectionId, int Revision), DocumentCollection> _collections = new();
    private readonly ConcurrentDictionary<(string TenantId, string CollectionNamespace, string CollectionId, int CollectionRevision), bool> _collectionAssignments = new();
    private readonly ConcurrentDictionary<(string TenantId, string BlueprintNamespace, string BlueprintId), bool> _blueprintGrants = new();

    public LibraryAccessService()
    {
        SeedDefaults();
    }

    public Task<IReadOnlyList<DocumentCollection>> GetAssignedCollectionsAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var assigned = new List<DocumentCollection>();
        foreach (var assignment in _collectionAssignments)
        {
            if (assignment.Key.TenantId == tenantId && assignment.Value)
            {
                var collectionKey = (assignment.Key.CollectionNamespace, assignment.Key.CollectionId, assignment.Key.CollectionRevision);
                if (_collections.TryGetValue(collectionKey, out var col) && col.PublicationState == PublicationState.Published)
                {
                    assigned.Add(col);
                }
            }
        }

        return Task.FromResult<IReadOnlyList<DocumentCollection>>(assigned.AsReadOnly());
    }

    public Task<DocumentCollection?> GetCollectionAsync(
        string tenantId,
        string collectionNamespace,
        string collectionId,
        int? revision = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 1. Check if the tenant has an active assignment for this collection
        DocumentCollection? matched = null;
        if (revision.HasValue)
        {
            var assignmentKey = (tenantId, collectionNamespace, collectionId, revision.Value);
            if (_collectionAssignments.TryGetValue(assignmentKey, out var active) && active)
            {
                _collections.TryGetValue((collectionNamespace, collectionId, revision.Value), out matched);
            }
        }
        else
        {
            // Pick latest assigned revision
            int highestRev = -1;
            foreach (var kvp in _collectionAssignments)
            {
                if (kvp.Key.TenantId == tenantId &&
                    kvp.Key.CollectionNamespace == collectionNamespace &&
                    kvp.Key.CollectionId == collectionId &&
                    kvp.Value)
                {
                    if (kvp.Key.CollectionRevision > highestRev)
                    {
                        highestRev = kvp.Key.CollectionRevision;
                    }
                }
            }

            if (highestRev > 0)
            {
                _collections.TryGetValue((collectionNamespace, collectionId, highestRev), out matched);
            }
        }

        if (matched != null && matched.PublicationState != PublicationState.Published)
        {
            matched = null;
        }

        return Task.FromResult(matched);
    }

    public async Task<IReadOnlyList<DocumentBlueprint>> ListEffectiveBlueprintsAsync(
        string tenantId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var assignedCollections = await GetAssignedCollectionsAsync(tenantId, cancellationToken);
        var memberBlueprintKeys = new HashSet<(string Namespace, string BlueprintId, int Revision)>();

        foreach (var col in assignedCollections)
        {
            foreach (var member in col.Members)
            {
                memberBlueprintKeys.Add((member.BlueprintNamespace, member.BlueprintId, member.BlueprintRevision));
            }
        }

        var results = new Dictionary<(string Namespace, string BlueprintId), DocumentBlueprint>();

        foreach (var bp in _blueprints.Values)
        {
            if (bp.PublicationState != PublicationState.Published)
            {
                continue;
            }

            bool isAccessible = false;

            // (a) Is a member of an assigned collection
            if (memberBlueprintKeys.Contains((bp.Namespace, bp.BlueprintId, bp.Revision)))
            {
                isAccessible = true;
            }
            // (b) Is a shared official published blueprint
            else if (SharedOfficialNamespaces.Contains(bp.Namespace, StringComparer.OrdinalIgnoreCase))
            {
                isAccessible = true;
            }
            // (c) Is a private blueprint owned by the caller's tenant
            else if (string.Equals(bp.Namespace, tenantId, StringComparison.Ordinal))
            {
                isAccessible = true;
            }
            // (d) Is explicitly granted to caller's tenant
            else if (_blueprintGrants.TryGetValue((tenantId, bp.Namespace, bp.BlueprintId), out var granted) && granted)
            {
                isAccessible = true;
            }

            if (isAccessible)
            {
                var key = (bp.Namespace, bp.BlueprintId);
                if (!results.TryGetValue(key, out var existing) || bp.Revision > existing.Revision)
                {
                    results[key] = bp;
                }
            }
        }

        return results.Values.OrderBy(b => b.Namespace).ThenBy(b => b.BlueprintId).ToList().AsReadOnly();
    }

    public async Task<DocumentBlueprint?> GetBlueprintAsync(
        string tenantId,
        string blueprintNamespace,
        string blueprintId,
        int? revision = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        bool canAccess = await CanAccessBlueprintAsync(tenantId, blueprintNamespace, blueprintId, cancellationToken);
        if (!canAccess)
        {
            return null;
        }

        if (revision.HasValue)
        {
            if (_blueprints.TryGetValue((blueprintNamespace, blueprintId, revision.Value), out var bp) &&
                bp.PublicationState == PublicationState.Published)
            {
                return bp;
            }
            return null;
        }

        // Return latest revision
        DocumentBlueprint? latest = null;
        foreach (var bp in _blueprints.Values)
        {
            if (bp.Namespace == blueprintNamespace &&
                bp.BlueprintId == blueprintId &&
                bp.PublicationState == PublicationState.Published)
            {
                if (latest == null || bp.Revision > latest.Revision)
                {
                    latest = bp;
                }
            }
        }

        return latest;
    }

    public async Task<bool> CanAccessBlueprintAsync(
        string tenantId,
        string blueprintNamespace,
        string blueprintId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // 1. Shared official namespace: accessible to all active tenants
        if (SharedOfficialNamespaces.Contains(blueprintNamespace, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        // 2. Private blueprint owned by this tenant
        if (string.Equals(blueprintNamespace, tenantId, StringComparison.Ordinal))
        {
            return true;
        }

        // 3. Explicit grant
        if (_blueprintGrants.TryGetValue((tenantId, blueprintNamespace, blueprintId), out var granted) && granted)
        {
            return true;
        }

        // 4. Assigned collection member
        var collections = await GetAssignedCollectionsAsync(tenantId, cancellationToken);
        foreach (var col in collections)
        {
            foreach (var member in col.Members)
            {
                if (string.Equals(member.BlueprintNamespace, blueprintNamespace, StringComparison.Ordinal) &&
                    string.Equals(member.BlueprintId, blueprintId, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    public void RegisterTenant(InstitutionTenant tenant) => _tenants[tenant.TenantId] = tenant;

    public void RegisterBlueprint(DocumentBlueprint blueprint) =>
        _blueprints[(blueprint.Namespace, blueprint.BlueprintId, blueprint.Revision)] = blueprint;

    public void RegisterCollection(DocumentCollection collection) =>
        _collections[(collection.Namespace, collection.CollectionId, collection.Revision)] = collection;

    public void AssignCollectionToTenant(string tenantId, string collectionNamespace, string collectionId, int revision) =>
        _collectionAssignments[(tenantId, collectionNamespace, collectionId, revision)] = true;

    public void GrantBlueprintToTenant(string tenantId, string blueprintNamespace, string blueprintId) =>
        _blueprintGrants[(tenantId, blueprintNamespace, blueprintId)] = true;

    private void SeedDefaults()
    {
        // 1. Tenants
        RegisterTenant(new InstitutionTenant("tenant_clinic_alpha", "East Bay Community Legal Clinic", true, DateTimeOffset.UtcNow));
        RegisterTenant(new InstitutionTenant("tenant_firm_beta", "Sterling Immigration Partners", true, DateTimeOffset.UtcNow));

        var emptyDoc = JsonDocument.Parse("{}");
        var emptyArray = JsonDocument.Parse("[]");

        // 2. Shared Official Blueprint (USCIS I-130)
        var i130 = new DocumentBlueprint(
            Namespace: "uscis",
            BlueprintId: "i-130",
            Revision: 1,
            Title: "Petition for Alien Relative",
            Issuer: "U.S. Citizenship and Immigration Services",
            OfficialEditionDate: new DateOnly(2024, 4, 1),
            PreparationMode: PreparationMode.FillablePdf,
            ArtifactType: BlueprintArtifactType.OfficialPdf,
            SourceUrl: new Uri("https://www.uscis.gov/sites/default/files/document/forms/i-130.pdf"),
            SourceSha256: "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            FieldsSchema: emptyDoc,
            ValidationRules: emptyArray,
            EvidenceRequirements: emptyArray,
            PublicationState: PublicationState.Published,
            ReviewedBy: "reviewer@lapluma.internal",
            ReviewedAt: DateTimeOffset.UtcNow,
            PublishedAt: DateTimeOffset.UtcNow,
            IsLatest: true,
            CreatedAt: DateTimeOffset.UtcNow);
        RegisterBlueprint(i130);

        // 3. Private Blueprint for Clinic Alpha (Intake)
        var intake = new DocumentBlueprint(
            Namespace: "tenant_clinic_alpha",
            BlueprintId: "intake",
            Revision: 1,
            Title: "Clinic Client Intake Sheet",
            Issuer: "East Bay Community Legal Clinic",
            OfficialEditionDate: new DateOnly(2026, 1, 1),
            PreparationMode: PreparationMode.StaticAssisted,
            ArtifactType: BlueprintArtifactType.AuthoredTemplate,
            SourceUrl: new Uri("https://partner.lapluma.io/templates/intake-2026.pdf"),
            SourceSha256: "01ba4719c80b6fe911b091a7c05124b64eeece964e09c058ef8f9805daca546b",
            FieldsSchema: emptyDoc,
            ValidationRules: emptyArray,
            EvidenceRequirements: emptyArray,
            PublicationState: PublicationState.Published,
            ReviewedBy: "reviewer@lapluma.internal",
            ReviewedAt: DateTimeOffset.UtcNow,
            PublishedAt: DateTimeOffset.UtcNow,
            IsLatest: true,
            CreatedAt: DateTimeOffset.UtcNow);
        RegisterBlueprint(intake);

        // 4. Private Blueprint for Firm Beta (Retainer)
        var retainer = new DocumentBlueprint(
            Namespace: "tenant_firm_beta",
            BlueprintId: "special_retainer",
            Revision: 1,
            Title: "Immigration Legal Retainer",
            Issuer: "Sterling Immigration Partners",
            OfficialEditionDate: new DateOnly(2026, 1, 1),
            PreparationMode: PreparationMode.StaticAssisted,
            ArtifactType: BlueprintArtifactType.AuthoredTemplate,
            SourceUrl: new Uri("https://sterling.example.com/retainer-v1.pdf"),
            SourceSha256: "cf83e1357eefb8bdf1542850d66d8007d620e4050b5715dc83f4a921d36ce9ce",
            FieldsSchema: emptyDoc,
            ValidationRules: emptyArray,
            EvidenceRequirements: emptyArray,
            PublicationState: PublicationState.Published,
            ReviewedBy: "reviewer@lapluma.internal",
            ReviewedAt: DateTimeOffset.UtcNow,
            PublishedAt: DateTimeOffset.UtcNow,
            IsLatest: true,
            CreatedAt: DateTimeOffset.UtcNow);
        RegisterBlueprint(retainer);

        // 5. Official Collection (Family Reunification)
        var familyPkg = new DocumentCollection(
            Namespace: "official",
            CollectionId: "family_reunification",
            Revision: 1,
            Title: "Family Reunification Package",
            Description: "Standard family-based immigration workflow containing USCIS Form I-130.",
            WorkflowSettings: JsonDocument.Parse("{\"theme\": \"official-standard\", \"displayOrder\": 10}"),
            PublicationState: PublicationState.Published,
            IsLatest: true,
            CreatedAt: DateTimeOffset.UtcNow,
            Members: [new CollectionMember("uscis", "i-130", 1, 1, true)]);
        RegisterCollection(familyPkg);

        // 6. Private Collection for Clinic Alpha
        var clinicPkg = new DocumentCollection(
            Namespace: "tenant_clinic_alpha",
            CollectionId: "clinic_intake_pkg",
            Revision: 1,
            Title: "East Bay Legal Clinic Intake & Petition",
            Description: "Custom clinic intake package linking private intake sheet with official I-130.",
            WorkflowSettings: JsonDocument.Parse("{\"theme\": \"clinic-pastel\", \"displayOrder\": 1}"),
            PublicationState: PublicationState.Published,
            IsLatest: true,
            CreatedAt: DateTimeOffset.UtcNow,
            Members: [
                new CollectionMember("tenant_clinic_alpha", "intake", 1, 1, true),
                new CollectionMember("uscis", "i-130", 1, 2, true)
            ]);
        RegisterCollection(clinicPkg);

        // 7. Assignments:
        // Both get official Family Reunification
        AssignCollectionToTenant("tenant_clinic_alpha", "official", "family_reunification", 1);
        AssignCollectionToTenant("tenant_firm_beta", "official", "family_reunification", 1);
        // Only Clinic Alpha gets its private package
        AssignCollectionToTenant("tenant_clinic_alpha", "tenant_clinic_alpha", "clinic_intake_pkg", 1);
    }
}
