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
    public List<HubSkillEntry> Skills { get; set; } = [];
    public List<HubInstructionEntry> Instructions { get; set; } = [];
    public List<HubFileEntry> Files { get; set; } = [];
    public List<HubMcpEntry> Mcps { get; set; } = [];
    public List<HubCliEntry> Clis { get; set; } = [];
}

public abstract class HubCatalogEntry
{
    [JsonRequired]
    public string Id { get; set; } = "";
    [JsonRequired]
    public string Path { get; set; } = "";
    [JsonRequired]
    public string Categories { get; set; } = "";
}

public sealed class HubSkillEntry : HubCatalogEntry
{
    public List<string> Dependencies { get; set; } = [];
}

public sealed class HubInstructionEntry : HubCatalogEntry;
public sealed class HubFileEntry : HubCatalogEntry;
public abstract class HubNetworkEntry : HubCatalogEntry
{
    [JsonRequired]
    public string Description { get; set; } = "";
}
public sealed class HubMcpEntry : HubNetworkEntry;
public sealed class HubCliEntry : HubNetworkEntry;

// Only the validated, normalized catalog reaches discovery and installation.
internal sealed class HubCatalogItem
{
    public string Id { get; init; } = "";
    public string Type { get; init; } = "";
    public string Path { get; init; } = "";
    public string Categories { get; init; } = "";
    public string Description { get; init; } = "";
    public List<string> Dependencies { get; init; } = [];
}

internal sealed record TypedHubCatalog(string DefaultLanguage, IReadOnlyList<HubCatalogItem> Items);

internal sealed record ValidatedCatalog(TypedHubCatalog Catalog,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Categories,
    IReadOnlyDictionary<string, HubCatalogItem> ById,
    IReadOnlyDictionary<string, IReadOnlyList<string>> ItemCategories,
    IReadOnlySet<string> UnavailableSkills);

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
        if (catalog.SchemaVersion != 2)
            throw new InvalidDataException("Unsupported hub catalog schema version. Expected schemaVersion 2.");
        if (catalog.DefaultLanguage is null || !Language().IsMatch(catalog.DefaultLanguage) ||
            !IsIso639TwoLetter(catalog.DefaultLanguage) ||
            catalog.Categories is null || catalog.Categories.Count > MaxCategories ||
            catalog.Skills is null || catalog.Instructions is null || catalog.Files is null ||
            catalog.Mcps is null || catalog.Clis is null)
            throw new InvalidDataException("Unsupported or unsafe hub catalog.");
        var items = new List<HubCatalogItem>();
        void Add<T>(IEnumerable<T> section, string type) where T : HubCatalogEntry
        {
            foreach (var entry in section)
            {
                if (entry is null) throw new InvalidDataException($"Invalid {type} catalog entry.");
                items.Add(new HubCatalogItem { Id = entry.Id, Type = type, Path = entry.Path,
                    Categories = entry.Categories, Description = entry is HubNetworkEntry network ? network.Description : "",
                    Dependencies = entry is HubSkillEntry skill ? skill.Dependencies : [] });
            }
        }
        Add(catalog.Skills, "skill");
        Add(catalog.Instructions, "instruction");
        Add(catalog.Files, "file");
        Add(catalog.Mcps, "mcp");
        Add(catalog.Clis, "cli");
        if (items.Count > MaxItems) throw new InvalidDataException("Catalog items exceed the safe limit.");
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
        var missingItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            if (item is null || item.Id is null || item.Type is null || item.Path is null || item.Categories is null ||
                !ValidId(item.Id) || !ids.TryAdd(item.Id, item) ||
                item.Dependencies is null || item.Dependencies.Count > 50 ||
                (item.Type != "skill" && item.Dependencies.Count > 0))
                throw new InvalidDataException("Invalid catalog item identity or fields.");
            if (item.Categories.Length > 4096) throw new InvalidDataException("Catalog categories exceed the safe limit.");
            var networkItem = item.Type is "mcp" or "cli";
            if (networkItem)
            {
                if (string.IsNullOrWhiteSpace(item.Description) || item.Description.Length > 1024)
                    throw new InvalidDataException("MCP and CLI entries require a nonblank English description of at most 1024 characters.");
                ValidateNetworkPath(item.Path);
            }
            else
            {
                InstallerPaths.Relative(item.Path);
                if (paths.Any(path => path.Equals(item.Path, StringComparison.OrdinalIgnoreCase) ||
                                      path.StartsWith(item.Path + "/", StringComparison.OrdinalIgnoreCase) ||
                                      item.Path.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("Catalog paths must be unique and non-overlapping.");
                paths.Add(item.Path);
            }
            var categories = item.Categories.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.Ordinal).ToArray();
            if (categories.Any(category => !categoryLabels.ContainsKey(category)))
                throw new InvalidDataException("Catalog item refers to an unknown category.");
            normalizedCategories.Add(item.Id, categories);
            if (item.Type == "skill")
            {
                if (!entries.Any(entry => entry.Path == item.Path + "/SKILL.md"))
                    missingItems.Add(item.Id);
            }
            else if (!networkItem && !entries.Any(entry => entry.Path == item.Path))
                missingItems.Add(item.Id);
        }
        foreach (var item in items)
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
        foreach (var item in items) Visit(item);
        var unavailableSkills = items.Where(item => item.Type == "skill" && missingItems.Contains(item.Id))
            .Select(item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool changed;
        do
        {
            changed = false;
            foreach (var item in items.Where(item => item.Type == "skill"))
                if (item.Dependencies.Any(unavailableSkills.Contains)) changed |= unavailableSkills.Add(item.Id);
        } while (changed);
        var availableCatalog = new TypedHubCatalog(catalog.DefaultLanguage,
            items.Where(item => !missingItems.Contains(item.Id)).ToList());
        return new ValidatedCatalog(availableCatalog, categoryLabels, ids, normalizedCategories, unavailableSkills);
    }

    private static void ValidateNetworkPath(string path)
    {
        // Syntax only: discovery must never contact a network share.
        var parts = path.StartsWith("\\\\", StringComparison.Ordinal) ? path[2..].TrimEnd('\\').Split('\\') : [];
        if (path.Length > 32767 || parts.Length < 2 || parts.Any(part =>
            string.IsNullOrWhiteSpace(part) || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
            part.Any(c => c < 32 || "<>:\"/|?*".Contains(c))))
            throw new InvalidDataException("MCP and CLI paths must be UNC network-folder paths (\\\\server\\share\\folder).");
    }

    private static void ValidateTranslations(IReadOnlyDictionary<string, string> translations, string fallback)
    {
        if (translations.Count is < 1 or > MaxTranslations || !translations.ContainsKey(fallback) ||
            translations.Any(pair => pair.Key is null || !Language().IsMatch(pair.Key) || !IsIso639TwoLetter(pair.Key) ||
                                     string.IsNullOrWhiteSpace(pair.Value) || pair.Value.Length > 1024))
            throw new InvalidDataException("Invalid localized catalog text.");
    }

    private static bool IsIso639TwoLetter(string code) => IsoLanguages.Contains(code);

    internal static bool IsLanguage(string code) => Language().IsMatch(code) && IsIso639TwoLetter(code);

    private static bool ValidId(string? id) => id is { Length: <= 80 } && Id().IsMatch(id);

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex Id();
    [GeneratedRegex("^[a-z]{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex Language();
}
