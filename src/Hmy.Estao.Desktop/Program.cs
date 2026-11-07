using Hmy.Estao.Core.Configuration;

namespace Hmy.Estao.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            using var context = new TrayApplicationContext(new ConfigStore());
            if (args.Contains("--settings", StringComparer.OrdinalIgnoreCase)) context.ShowSettings();
            Application.Run(context);
        }
        catch (Exception exception)
        {
            MessageBox.Show(DesktopLocalization.Error(exception), DesktopLocalization.Text("native.start_failed"), MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
