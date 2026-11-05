using System.Text.Json;

namespace Hmy.Estao.Desktop;

// The frontend imports this same embedded JSON: adding a theme needs no new
// native enum, validation list, stylesheet, or component branches.
internal static class DesktopThemeCatalog
{
    public static IReadOnlyList<DesktopThemeDefinition> All { get; } = Load();
    public static string DefaultId => All[0].Id;

    public static DesktopThemeDefinition Resolve(string? value)
    {
        var id = value?.Trim();
        return All.FirstOrDefault(theme => string.Equals(theme.Id, id, StringComparison.OrdinalIgnoreCase)
            || theme.Aliases.Contains(id ?? string.Empty, StringComparer.OrdinalIgnoreCase)) ?? All[0];
    }

    public static bool IsSupported(string? id) => All.Any(theme => string.Equals(theme.Id, id, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<DesktopThemeDefinition> Load()
    {
        using var stream = typeof(DesktopThemeCatalog).Assembly.GetManifestResourceStream("Hmy.Estao.Desktop.Themes.json")
            ?? throw new InvalidOperationException("The desktop theme catalog is missing.");
        var themes = JsonSerializer.Deserialize<List<DesktopThemeDefinition>>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidOperationException("The desktop theme catalog is empty.");
        if (themes.Count == 0) throw new InvalidOperationException("At least one desktop theme is required.");
        return themes.AsReadOnly();
    }
}

internal sealed record DesktopThemeDefinition(string Id, string Name, string Mode, string Description,
    string[] Aliases, Dictionary<string, string> Colors);
