namespace Hmy.Estao.Desktop;

internal static class UsagePresentation
{
    public static string Reset(DateTimeOffset? reset)
    {
        if (reset is null) return DesktopLocalization.Text("reset.short_unknown");
        var remaining = reset.Value - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero) return DesktopLocalization.Text("reset.due");
        return remaining.TotalDays >= 1 ? DesktopLocalization.Text("duration.days", (int)remaining.TotalDays, remaining.Hours)
            : remaining.TotalHours >= 1 ? DesktopLocalization.Text("duration.hours", (int)remaining.TotalHours, remaining.Minutes)
            : DesktopLocalization.Text("duration.minutes", Math.Max(1, (int)remaining.TotalMinutes));
    }
}
