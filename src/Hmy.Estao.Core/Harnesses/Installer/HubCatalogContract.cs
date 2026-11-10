using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Hmy.Estao.Core.Harnesses.Installer;

public sealed class HubCatalog
{
    [JsonRequired]
    public int SchemaVersion { get; set; }
    [JsonRequired]
    public string DefaultLanguage { get; set; } = "";
    [JsonRequired]
    public Dictionary<string, Dictionary<string, string>> Categories { get; set; } = [];
    [JsonRequired]
    public List<HubCatalogItem> Items { get; set; } = [];
}

public sealed class HubCatalogItem
{
    [JsonRequired]
    public string Id { get; set; } = "";
    [JsonRequired]
    public string Type { get; set; } = "";
    [JsonRequired]
    public string Path { get; set; } = "";
    [JsonRequired]
    public Dictionary<string, string> Description { get; set; } = [];
    [JsonRequired]
    public string Categories { get; set; } = "";
    public List<string> Dependencies { get; set; } = [];
}

internal sealed record ValidatedCatalog(HubCatalog Catalog,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Categories,
    IReadOnlyDictionary<string, HubCatalogItem> ById,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ItemCategories);

internal static partial class HubCatalogContract
{
    private const int MaxItems = 500;
    private const int MaxCategories = 100;
    private const int MaxTranslations = 20;
    private static readonly HashSet<string> IsoLanguages = CultureInfo.GetCultures(CultureTypes.AllCultures)
        .Where(culture => culture.Name.Length > 0 && culture.TwoLetterISOLanguageName.Length == 2)
        .Select(culture => culture.TwoLetterISOLanguageName).ToHashSet(StringComparer.Ordinal);

    public static ValidatedCatalog Read(byte[] bytes, IReadOnlyList<SourceEntry> entries)
    {
        var catalog = InstallerJson.Read<HubCatalog>(bytes);
        return Validate(catalog, entries);
    }

    public static ValidatedCatalog Validate(HubCatalog catalog, IReadOnlyList<SourceEntry> entries)
    {
        if (catalog.SchemaVersion != 1 || catalog.DefaultLanguage is null || !Language().IsMatch(catalog.DefaultLanguage) ||
            !IsIso639TwoLetter(catalog.DefaultLanguage) ||
            catalog.Categories is null || catalog.Items is null || catalog.Categories.Count > MaxCategories ||
            catalog.Items.Count > MaxItems)
            throw new InvalidDataException("Unsupported or unsafe hub catalog.");
        var categoryLabels = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var (key, labels) in catalog.Categories)
        {
            if (!ValidId(key) || labels is null || !labels.ContainsKey(catalog.DefaultLanguage) ||
                labels.Count is < 1 or > MaxTranslations)
                throw new InvalidDataException("Invalid catalog category.");
            ValidateTranslations(labels, catalog.DefaultLanguage);
            categoryLabels.Add(key, new Dictionary<string, string>(labels, StringComparer.Ordinal));
        }

        var ids = new Dictionary<string, HubCatalogItem>(StringComparer.OrdinalIgnoreCase);
        var paths = new List<string>();
        var normalizedCategories = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in catalog.Items)
        {
            if (item is null || item.Id is null || item.Type is null || item.Path is null || item.Categories is null ||
                !ValidId(item.Id) || !ids.TryAdd(item.Id, item) || item.Type is not ("skill" or "instruction" or "file") || item.Description is null ||
                !item.Description.ContainsKey(catalog.DefaultLanguage) || item.Dependencies is null || item.Dependencies.Count > 50 ||
                (item.Type != "skill" && item.Dependencies.Count > 0))
                throw new InvalidDataException("Invalid catalog item identity or fields.");
            if (item.Categories.Length > 4096) throw new InvalidDataException("Catalog categories exceed the safe limit.");
            InstallerPaths.Relative(item.Path);
            if (paths.Any(path => path.Equals(item.Path, StringComparison.OrdinalIgnoreCase) ||
                                  path.StartsWith(item.Path + "/", StringComparison.OrdinalIgnoreCase) ||
                                  item.Path.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("Catalog paths must be unique and non-overlapping.");
            paths.Add(item.Path);
            ValidateTranslations(item.Description, catalog.DefaultLanguage);
            var categories = item.Categories.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.Ordinal).ToArray();
            if (categories.Any(category => !categoryLabels.ContainsKey(category)))
                throw new InvalidDataException("Catalog item refers to an unknown category.");
            normalizedCategories.Add(item.Id, categories);
            if (item.Type == "skill")
            {
                if (!entries.Any(entry => entry.Path == item.Path + "/SKILL.md"))
                    throw new InvalidDataException("Curated skill path must identify an existing SKILL.md file.");
            }
            else if (!entries.Any(entry => entry.Path == item.Path))
                throw new InvalidDataException("Curated file does not exist in the source tree.");
        }
        foreach (var item in catalog.Items)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dependency in item.Dependencies)
                if (!ValidId(dependency) || !ids.TryGetValue(dependency, out var required) || required.Type != "skill" ||
                    dependency.Equals(item.Id, StringComparison.OrdinalIgnoreCase) || !seen.Add(dependency))
                    throw new InvalidDataException("Invalid catalog dependency.");
        }
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(HubCatalogItem item)
        {
            if (visited.Contains(item.Id)) return;
            if (!visiting.Add(item.Id)) throw new InvalidDataException("Cyclic catalog dependency.");
            foreach (var dependency in item.Dependencies) Visit(ids[dependency]);
            visiting.Remove(item.Id);
            visited.Add(item.Id);
        }
        foreach (var item in catalog.Items) Visit(item);
        return new ValidatedCatalog(catalog, categoryLabels, ids, normalizedCategories);
    }

    private static void ValidateTranslations(IReadOnlyDictionary<string, string> translations, string fallback)
    {
        if (translations.Count is < 1 or > MaxTranslations || !translations.ContainsKey(fallback) ||
            translations.Any(pair => pair.Key is null || !Language().IsMatch(pair.Key) || !IsIso639TwoLetter(pair.Key) ||
                                     string.IsNullOrWhiteSpace(pair.Value) || pair.Value.Length > 1024))
            throw new InvalidDataException("Invalid localized catalog text.");
    }

    private static bool IsIso639TwoLetter(string code) => IsoLanguages.Contains(code);

    private static bool ValidId(string? id) => id is { Length: <= 80 } && Id().IsMatch(id);

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex Id();
    [GeneratedRegex("^[a-z]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex Language();
}
