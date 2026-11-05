using System.Drawing;
using System.Runtime.ExceptionServices;
using System.Windows.Forms;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Models;
using Hmy.Estao.Desktop;

namespace Hmy.Estao.Desktop.Tests;

public sealed class OverlayTests
{
    [Fact]
    public void WebWindowsUseTheTemplatesCustomChromeHost()
    {
        Assert.True(typeof(Shofia.WebHost.CustomChromeWebViewHostForm).IsAssignableFrom(typeof(DesktopWindow)));
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    [InlineData("copilot")]
    [InlineData("opencode")]
    [InlineData("zai")]
    public void LogoOnlyProviderIdentityHasAnEmbeddedLogo(string provider)
    {
        using var logo = typeof(ProviderOverlay).Assembly.GetManifestResourceStream($"Hmy.Estao.ProviderIcons.{provider}.svg");
        Assert.NotNull(logo);
        Assert.True(logo.Length > 0);
    }

    [Fact]
    public void LightAndDarkPalettesKeepOriginalWindowsColors()
    {
        var light = WindowsTaskbarPalette.ForAppearance(true);
        var dark = WindowsTaskbarPalette.ForAppearance(false);
        Assert.Equal(Color.FromArgb(248, 248, 248), light.Surface);
        Assert.Equal(Color.FromArgb(210, 210, 210), light.Border);
        Assert.Equal(Color.FromArgb(32, 32, 32), dark.Surface);
        Assert.Equal(Color.FromArgb(72, 72, 72), dark.Border);
        Assert.Equal(Color.FromArgb(245, 245, 245), dark.Text);
        Assert.Equal(Color.FromArgb(185, 185, 185), dark.TextMuted);
    }

    [Fact]
    public void HighContrastUsesSystemColorsRegardlessOfLightDarkPreference()
    {
        var light = WindowsTaskbarPalette.ForAppearance(true, true);
        var dark = WindowsTaskbarPalette.ForAppearance(false, true);
        Assert.Equal(light, dark);
        Assert.Equal(SystemColors.Window, light.Surface);
        Assert.Equal(SystemColors.WindowText, light.Text);
        Assert.Equal(SystemColors.Highlight, light.Accent);
    }

    [Fact]
    public void OverlayIsNativeAndIgnoresLegacyDisplayOptions() => OnSta(() =>
    {
        using var overlay = new ProviderOverlay();
        var config = ConfigStore.Normalize(ConfigStore.CreateDefaultConfig());
        config.TaskbarOverlay.Enabled = false; // Avoid showing a window in CI.
        config.TaskbarOverlay.Controls = ["chart", "bar", "usedTotal"];
        config.TaskbarOverlay.DisplayMode = "icon";
        config.TaskbarOverlay.Size = "spacious";
        var snapshot = new UsageSnapshot("codex", "Codex", "test", DateTimeOffset.UtcNow,
            [new RateWindow("weekly", "Weekly", .42, DateTimeOffset.UtcNow.AddHours(2))]);
        overlay.UpdateUsage(config, [snapshot]);
        Assert.Empty(overlay.Controls.Cast<Control>());
        Assert.False(overlay.ShowInTaskbar);
        Assert.Equal(FormBorderStyle.None, overlay.FormBorderStyle);
        Assert.Contains("42", overlay.AccessibleDescription);
        Assert.Contains("Codex", overlay.AccessibleDescription);
        Assert.Contains("1h", overlay.AccessibleDescription); // Remaining duration is rounded down.
        Assert.False(overlay.Visible);
        Assert.Equal(38, overlay.ClientSize.Height);
    });

    [Fact]
    public void DisabledProvidersNeverAppearInOverlay() => OnSta(() =>
    {
        using var overlay = new ProviderOverlay();
        var config = ConfigStore.Normalize(ConfigStore.CreateDefaultConfig());
        config.TaskbarOverlay.Enabled = false;
        config.TaskbarOverlay.ProviderIds = ["claude", "codex"];
        overlay.UpdateUsage(config, []);
        Assert.Contains("Codex", overlay.AccessibleDescription);
        Assert.DoesNotContain("Claude", overlay.AccessibleDescription);
    });

    private static void OnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception e) { failure = e; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
