namespace Hmy.Estao.Desktop;

internal static class UsagePresentation
{
    public static string Reset(DateTimeOffset? reset)
    {
        if (reset is null) return "Reset —";
        var remaining = reset.Value - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return "Reset due";
        return remaining.TotalDays >= 1 ? $"{(int)remaining.TotalDays}d {remaining.Hours}h"
            : remaining.TotalHours >= 1 ? $"{(int)remaining.TotalHours}h {remaining.Minutes}m"
            : $"{Math.Max(1, (int)remaining.TotalMinutes)}m";
    }
}
