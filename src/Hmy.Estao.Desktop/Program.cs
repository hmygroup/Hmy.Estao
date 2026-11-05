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
            MessageBox.Show(exception.Message, "Estao could not start", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
