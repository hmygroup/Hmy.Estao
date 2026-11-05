using Microsoft.Win32;

namespace Hmy.Estao.Desktop;

internal static class StartupManager
{
    private const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public static bool Enabled
    {
        get { using var key = Registry.CurrentUser.OpenSubKey(Key); return key?.GetValue("Estao") is not null; }
    }
    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(Key, writable: true);
        if (enabled) key.SetValue("Estao", $"\"{Application.ExecutablePath}\"");
        else key.DeleteValue("Estao", throwOnMissingValue: false);
    }
}
