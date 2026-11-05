using Hmy.Estao.Core.Configuration;
using ZarpaSuite.Controls;

namespace Hmy.Estao.App.Controls.Zarpa;

/// <summary>Single entry point. Legacy settings remain serialized, but cannot trigger the old installer.</summary>
internal sealed class HarnessManagerSettingsPanel : ZarpaSettingsSection
{
    public event EventHandler? OpenHubRequested;
    public HarnessManagerSettingsPanel() : base("Harness Hub", "Instala skills compatibles desde la biblioteca GitHub de HMY sin reemplazar tu configuración personal.")
    {
        var open = new ZarpaButton { Text = "Abrir Harness Hub", Width = 180, ButtonStyle = ZarpaButtonStyle.Primary };
        open.Click += (_, _) => OpenHubRequested?.Invoke(this, EventArgs.Empty);
        AddRow("Configuración personal", "Cuatro harnesses · instalación aditiva · conflictos conservados", open, 200);
    }
    public void LoadConfig(EstaoConfig config) { }
    public void Apply(HarnessManagerConfig manager) { }
}
