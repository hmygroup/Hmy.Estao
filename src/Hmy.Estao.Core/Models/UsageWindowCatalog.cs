namespace Hmy.Estao.Core.Models;

/// <summary>
/// Describes the time horizon used to display a provider rate-limit window.
/// Each series is mapped against its own horizon so a five-hour window does
/// not get compressed into the final pixels of a weekly or monthly chart.
/// </summary>
public static class UsageWindowCatalog
{
    public static readonly TimeSpan FiveHours = TimeSpan.FromHours(5);
    public static readonly TimeSpan Weekly = TimeSpan.FromDays(7);
    public static readonly TimeSpan Monthly = TimeSpan.FromDays(30);

    public static TimeSpan DisplayRange(string? provider, string? windowId, string? title = null)
    {
        var id = Normalize(windowId);
        var name = Normalize(title);
        var providerId = Normalize(provider);

        if (ContainsAny(id, name, "session", "primary", "fivehour", "5hour", "5h"))
            return FiveHours;

        if (ContainsAny(id, name, "weekly", "week", "sevenday", "7day", "secondary"))
            return Weekly;

        if (providerId == "copilot" ||
            ContainsAny(id, name, "monthly", "month", "premium", "chat"))
            return Monthly;

        return Weekly;
    }

    public static string DisplayLabel(TimeSpan range)
    {
        if (range <= FiveHours) return $"{Math.Max(1, (int)Math.Round(range.TotalHours))}h";
        return $"{Math.Max(1, (int)Math.Round(range.TotalDays))}d";
    }

    private static string Normalize(string? value) => string.Concat(
        (value ?? string.Empty).Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static bool ContainsAny(string id, string title, params string[] values) =>
        values.Any(value => id.Contains(value, StringComparison.Ordinal) ||
            title.Contains(value, StringComparison.Ordinal));
}
