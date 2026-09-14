using System.Collections.Concurrent;
using System.Text.Json;

namespace LaPluma.CoreApi;

/// <summary>
/// Authoritative in-memory catalog of source-cited official document guidance (INF-09).
/// Backed by contracts/uscis-official-guidance.json.
/// </summary>
public static class GuidanceCatalog
{
    private static readonly Lazy<ConcurrentDictionary<string, DocumentGuidance>> CachedGuidance = new(LoadGuidance);

    public static DocumentGuidance? FindGuidance(string blueprintNamespace, string blueprintId)
    {
        var map = CachedGuidance.Value;
        var cleanId = blueprintId.Trim().ToLowerInvariant();

        // 1. Try namespace-qualified key: "namespace/id"
        if (map.TryGetValue($"{blueprintNamespace.Trim().ToLowerInvariant()}/{cleanId}", out var match))
        {
            return match;
        }

        // 2. Try cleanId alone
        if (map.TryGetValue(cleanId, out match))
        {
            return match;
        }

        // 3. Try with/without hyphens
        var normalized = cleanId.Replace("-", "");
        if (map.TryGetValue(normalized, out match))
        {
            return match;
        }

        return null;
    }

    public static IReadOnlyList<DocumentGuidance> GetAllGuidance()
    {
        var map = CachedGuidance.Value;
        var list = new List<DocumentGuidance>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in map.Values)
        {
            if (seen.Add(item.FormId))
            {
                list.Add(item);
            }
        }

        return list.AsReadOnly();
    }

    private static ConcurrentDictionary<string, DocumentGuidance> LoadGuidance()
    {
        var dict = new ConcurrentDictionary<string, DocumentGuidance>(StringComparer.OrdinalIgnoreCase);
        var repoRoot = FindRepoRoot();
        if (repoRoot is null)
        {
            return dict;
        }

        var path = Path.Combine(repoRoot, "contracts", "uscis-official-guidance.json");
        if (!File.Exists(path))
        {
            return dict;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.TryGetProperty("guidance", out var guidanceArr) && guidanceArr.ValueKind == JsonValueKind.Array)
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                foreach (var element in guidanceArr.EnumerateArray())
                {
                    var item = JsonSerializer.Deserialize<DocumentGuidance>(element.GetRawText(), options);
                    if (item is null)
                    {
                        continue;
                    }

                    var idLower = item.FormId.Trim().ToLowerInvariant();
                    dict[idLower] = item;
                    dict[$"uscis/{idLower}"] = item;
                    dict[$"official/{idLower}"] = item;
                    dict[idLower.Replace("-", "")] = item;

                    if (!string.IsNullOrWhiteSpace(item.FormNumber))
                    {
                        var numLower = item.FormNumber.Trim().ToLowerInvariant();
                        dict[numLower] = item;
                        dict[$"uscis/{numLower}"] = item;
                        dict[$"official/{numLower}"] = item;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[GuidanceCatalog] Failed to load guidance dataset: {ex.Message}");
        }

        return dict;
    }

    private static string? FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "contracts", "uscis-official-guidance.json")))
            {
                return current.FullName;
            }
            current = current.Parent;
        }
        return null;
    }
}
