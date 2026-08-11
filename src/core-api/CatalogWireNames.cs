using System.Reflection;
using System.Text.Json.Serialization;

namespace LaPluma.CoreApi;

/// <summary>
/// Reads the contract's wire names off the enums themselves.
///
/// The database stores `OFFICIAL_PDF`, the JSON contract publishes `OFFICIAL_PDF`, and the C# member
/// is `OfficialPdf`. Writing a second mapping to translate the first into the third would be a
/// place for the two to disagree — and the disagreement would surface as a package silently
/// classified as something it is not. This derives the mapping from
/// <see cref="JsonStringEnumMemberNameAttribute"/>, so there is exactly one declaration of what a
/// wire name means and both the serializer and the database reader use it.
/// </summary>
public static class CatalogWireNames
{
    private static class Cache<T> where T : struct, Enum
    {
        public static readonly Dictionary<string, T> ByWireName = typeof(T)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (
                Name: field.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name,
                Value: (T)field.GetValue(null)!))
            .Where(entry => entry.Name is not null)
            .ToDictionary(entry => entry.Name!, entry => entry.Value, StringComparer.Ordinal);
    }

    /// <summary>The enum member for a contract wire name, or null when the name is not in the contract.</summary>
    public static T? Parse<T>(string wireName) where T : struct, Enum =>
        Cache<T>.ByWireName.TryGetValue(wireName, out var value) ? value : null;

    /// <summary>Every wire name the contract defines for the enum.</summary>
    public static IReadOnlyCollection<string> Names<T>() where T : struct, Enum => Cache<T>.ByWireName.Keys;
}
