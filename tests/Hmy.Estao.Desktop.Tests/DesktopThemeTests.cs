using System.Drawing;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Desktop;

namespace Hmy.Estao.Desktop.Tests;

public sealed class DesktopThemeTests
{
    [Fact]
    public void CatalogHasThreeLightAndThreeDarkThemesWithTheSameColorContract()
    {
        Assert.Equal(3, DesktopThemeCatalog.All.Count(t => t.Mode == "light"));
        Assert.Equal(3, DesktopThemeCatalog.All.Count(t => t.Mode == "dark"));
        Assert.Equal(DesktopThemeCatalog.All.Count, DesktopThemeCatalog.All.Select(t => t.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var expected = new[] { "canvas", "canvas-end", "text", "muted", "accent", "primary", "primary-light", "primary-hover", "glow-a", "glow-b", "glow-c" }.Order().ToArray();
        foreach (var theme in DesktopThemeCatalog.All)
        {
            Assert.Equal(expected, theme.Colors.Keys.Order());
            Assert.NotEmpty(theme.Name);
            Assert.NotEmpty(theme.Description);
            foreach (var color in theme.Colors.Values) Assert.Matches("^#[0-9A-Fa-f]{6}$", color);
            foreach (var alias in theme.Aliases) Assert.Equal(theme.Id, DesktopThemeCatalog.Resolve(alias).Id);
        }
    }

    [Theory]
    [InlineData("Graphite", "graphite")]
    [InlineData(" Liquid Glass ", "pearl")]
    [InlineData("MIDNIGHT", "midnight")]
    [InlineData("unknown-old-theme", "pearl")]
    [InlineData(null, "pearl")]
    public void LegacyNamesAndInvalidIdsResolvePredictably(string? value, string expected)
    {
        Assert.Equal(expected, DesktopThemeCatalog.Resolve(value).Id);
    }

    [Fact]
    public void CatalogIdsAreValidatedWithoutAnAdditionalAllowList()
    {
        foreach (var theme in DesktopThemeCatalog.All) Assert.True(DesktopThemeCatalog.IsSupported(theme.Id));
        Assert.False(DesktopThemeCatalog.IsSupported("not-a-theme"));
        Assert.False(DesktopThemeCatalog.IsSupported(null));
    }

    [Fact]
    public void AllThemesHaveReadableTextAndMutedTextOnTheirBaseCanvas()
    {
        foreach (var theme in DesktopThemeCatalog.All)
        {
            var canvas = ColorTranslator.FromHtml(theme.Colors["canvas"]);
            Assert.True(Contrast(ColorTranslator.FromHtml(theme.Colors["text"]), canvas) >= 7, theme.Id + " text contrast");
            Assert.True(Contrast(ColorTranslator.FromHtml(theme.Colors["muted"]), canvas) >= 4.5, theme.Id + " muted text contrast");
        }
    }

    [Fact]
    public async Task SavedThemeRoundTripsWithoutChangingNativeOverlayOrUsageAlerts()
    {
        var path = Path.Combine(Path.GetTempPath(), $"estao-theme-{Guid.NewGuid():N}.json");
        try
        {
            var store = new ConfigStore(path);
            var config = ConfigStore.Normalize(ConfigStore.CreateDefaultConfig());
            config.TaskbarOverlay.PositionX = 111;
            config.TaskbarOverlay.PositionY = 222;
            config.UsageAlerts!.Enabled = true;
            foreach (var theme in DesktopThemeCatalog.All)
            {
                config.Theme = theme.Id;
                await store.SaveAsync(config);
                var loaded = await store.LoadAsync();
                Assert.Equal(theme.Id, loaded.Theme);
                Assert.Equal(111, loaded.TaskbarOverlay.PositionX);
                Assert.Equal(222, loaded.TaskbarOverlay.PositionY);
                Assert.Equal(UsageColorCatalog.DefaultWarningColor, loaded.UsageAlerts!.WarningColor);
                Assert.True(loaded.UsageAlerts.Enabled);
            }
        }
        finally { File.Delete(path); }
    }

    private static double Contrast(Color first, Color second)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte channel) => channel / 255D <= .04045 ? channel / 255D / 12.92 : Math.Pow((channel / 255D + .055) / 1.055, 2.4);
            return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        }
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}
