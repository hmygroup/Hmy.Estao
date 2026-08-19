using ZarpaSuite.Controls;

namespace Hmy.Estao.App.Controls.Zarpa;

internal sealed class HarnessCapabilityCard : ZarpaMultiSelectCard
{
    private readonly string _version;

    public HarnessCapabilityCard(string title, string type, string version, string description, string footer)
    {
        _version = version;
        TitleText = title;
        DescriptionText = $"{type}  ·  {description}";
        MetadataText = footer;
        BadgeText = $"v{version}";
        IconKey = IconFor(type);
        Margin = new Padding(7);
        Width = 336;
    }

    public void MarkUpdateAvailable() => UpdateAvailable = true;

    private static string IconFor(string type) => type.ToLowerInvariant() switch
    {
        var value when value.Contains("skill", StringComparison.Ordinal) => "ic_fluent_hat_graduation_24_regular",
        var value when value.Contains("mcp", StringComparison.Ordinal) => "ic_fluent_plug_connected_24_regular",
        var value when value.Contains("agent", StringComparison.Ordinal) => "ic_fluent_person_24_regular",
        var value when value.Contains("instruction", StringComparison.Ordinal) || value.Contains("prompt", StringComparison.Ordinal) => "ic_fluent_document_text_24_regular",
        var value when value.Contains("setting", StringComparison.Ordinal) => "ic_fluent_settings_24_regular",
        _ => "ic_fluent_grid_24_regular"
    };
}
