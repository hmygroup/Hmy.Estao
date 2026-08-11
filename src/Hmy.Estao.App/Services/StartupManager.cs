using Hmy.Estao.Core;
using Microsoft.Win32;

namespace Hmy.Estao.App.Services;

/// <summary>
/// Manages the current user's Windows startup registration.
/// </summary>
internal static class StartupManager
{
    private const string StartupRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool EnableStartup()
    {
        try
        {
            var executablePath = Application.ExecutablePath;
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, writable: true);
            if (key is null) return false;

            key.SetValue(EstaoConstants.DisplayName, executablePath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool DisableStartup()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath, writable: true);
            if (key is null) return false;

            if (key.GetValue(EstaoConstants.DisplayName) is not null)
                key.DeleteValue(EstaoConstants.DisplayName);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsStartupEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupRegistryPath);
            return key?.GetValue(EstaoConstants.DisplayName) is not null;
        }
        catch
        {
            return false;
        }
    }
}
