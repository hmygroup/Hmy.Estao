using ZarpaSuite.Controls;

namespace Hmy.Estao.App.Controls.Zarpa;

internal static class ZarpaThemePreferences
{
    public static IReadOnlyList<ZarpaThemePreset> Available { get; } = Enum
        .GetValues<ZarpaThemePreset>()
        .Where(value => value != ZarpaThemePreset.Custom)
        .ToArray();

    public static IReadOnlyList<ZarpaBackdropStyle> AvailableBackdrops { get; } =
        Enum.GetValues<ZarpaBackdropStyle>();

    public static string DisplayName(ZarpaThemePreset preset) => preset switch
    {
        ZarpaThemePreset.MarlboroGold => "Marlboro Gold",
        _ => preset.ToString()
    };

    public static ZarpaThemePreset Parse(string? value)
    {
        var normalized = value?.Replace(" ", string.Empty, StringComparison.Ordinal);
        return Enum.TryParse<ZarpaThemePreset>(normalized, ignoreCase: true, out var preset) &&
               preset != ZarpaThemePreset.Custom
            ? preset
            : ZarpaThemePreset.Graphite;
    }

    public static ZarpaBackdropStyle ParseBackdrop(string? value)
    {
        return Enum.TryParse<ZarpaBackdropStyle>(value, ignoreCase: true, out var style)
            ? style
            : ZarpaBackdropStyle.None;
    }
}
