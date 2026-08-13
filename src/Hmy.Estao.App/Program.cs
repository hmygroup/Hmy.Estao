using Hmy.Estao.Core;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Refresh;

namespace Hmy.Estao.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetHighDpiMode(HighDpiMode.SystemAware);

        if (args.Contains("--settings", StringComparer.OrdinalIgnoreCase))
        {
            using var settings = new SettingsForm(new ConfigStore());
            Application.Run(settings);
            return;
        }

        using var context = new TrayApplicationContext(
            new ConfigStore(),
            serviceFactory: store => new UsageRefreshService(store));
        Application.Run(context);
    }
}
