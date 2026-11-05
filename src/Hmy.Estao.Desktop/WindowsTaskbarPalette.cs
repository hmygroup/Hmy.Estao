using Microsoft.Win32;

namespace Hmy.Estao.Desktop;

// Same Windows-personalization palette as the old taskbar overlay, independent
// of the fixed appearance of the settings and popover.
internal readonly record struct WindowsTaskbarPalette(Color Surface, Color Border, Color Text, Color TextMuted, Color Accent)
{
    public static WindowsTaskbarPalette Read()
    {
        var light = true;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("AppsUseLightTheme") is int value) light = value != 0;
        }
        catch (System.Security.SecurityException) { }
        catch (UnauthorizedAccessException) { }
        return ForAppearance(light, SystemInformation.HighContrast);
    }

    internal static WindowsTaskbarPalette ForAppearance(bool light, bool highContrast = false)
    {
        if (highContrast) return new(SystemColors.Window, SystemColors.WindowText, SystemColors.WindowText,
            SystemColors.WindowText, SystemColors.Highlight);
        var accent = SystemColors.Highlight;
        if (accent == Color.Black || accent == Color.White)
            accent = light ? Color.FromArgb(0, 103, 192) : Color.FromArgb(96, 165, 250);
        return light
            ? new(Color.FromArgb(248, 248, 248), Color.FromArgb(210, 210, 210), SystemColors.ControlText, SystemColors.GrayText, accent)
            : new(Color.FromArgb(32, 32, 32), Color.FromArgb(72, 72, 72), Color.FromArgb(245, 245, 245), Color.FromArgb(185, 185, 185), accent);
    }
}
