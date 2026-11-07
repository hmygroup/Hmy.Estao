using System.Text.Json;
using Hmy.Estao.Core.Models;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Desktop;

namespace Hmy.Estao.Desktop.Tests;

[CollectionDefinition("Localization", DisableParallelization = true)]
public sealed class LocalizationCollection;

[Collection("Localization")]
public sealed class LocalizationTests
{
    [Theory]
    [InlineData("system", "es-MX", "es")]
    [InlineData("en", "es-ES", "en")]
    [InlineData("de", "es-ES", "en")]
    [InlineData("system", "fr-CA", "fr")]
    [InlineData("fr-FR", "en-US", "fr")]
    public void LanguagesResolveWithEnglishFallback(string requested, string system, string expected)
        => Assert.Equal(expected, DesktopLocalization.Resolve(requested, system));

    [Fact]
    public void NativeAndWebUseTheSameCatalogsWithMatchingPlaceholders()
    {
        var assembly = typeof(DesktopWindow).Assembly;
        using var en = assembly.GetManifestResourceStream("Localization.en.json");
        Assert.NotNull(en);
        var english = JsonSerializer.Deserialize<Dictionary<string, string>>(en)!;
        foreach (var language in new[] { "es", "fr" })
        {
            using var stream = assembly.GetManifestResourceStream($"Localization.{language}.json");
            Assert.NotNull(stream);
            var translated = JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
            Assert.Equal(english.Keys.Order(), translated.Keys.Order());
        }
    }

    [Fact]
    public void NativeFormattingSupportsLiveSwitchingBrandingAndNestedMessages()
    {
        var initial = DesktopLocalization.Language;
        try
        {
            DesktopLocalization.SetLanguage("es");
            Assert.Equal("Configuración", DesktopLocalization.Text("settings"));
            Assert.Contains(DesktopBranding.DisplayName, DesktopLocalization.Text("native.overlay_title"));
            Assert.Contains("401", DesktopLocalization.Format(new("error.http", "Claude", 401)));
            Assert.DoesNotContain("secret", DesktopLocalization.Error(new Exception("secret")));
            DesktopLocalization.SetLanguage("fr");
            Assert.True(DesktopLocalization.IsSupported("fr"));
            Assert.Equal("Paramètres", DesktopLocalization.Text("settings"));
            Assert.Equal("Actualiser maintenant", DesktopLocalization.Text("tray.refresh"));
            Assert.Contains(DesktopBranding.DisplayName, DesktopLocalization.Text("native.overlay_title"));
            Assert.Equal("Claude a renvoyé le code HTTP 401.", DesktopLocalization.Format(new("error.http", "Claude", 401)));
            DesktopLocalization.SetLanguage("en");
            Assert.Equal("Settings", DesktopLocalization.Text("settings"));
        }
        finally { DesktopLocalization.SetLanguage(initial); }
    }

    [Fact]
    public void OverlayTitlesAndAccessibilityUpdateWithoutCreatingAWebView()
    {
        var initial = DesktopLocalization.Language;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                DesktopLocalization.SetLanguage("es");
                using var overlay = new ProviderOverlay();
                var config = ConfigStore.Normalize(ConfigStore.CreateDefaultConfig());
                config.TaskbarOverlay.Enabled = false;
                overlay.UpdateUsage(config, []);
                Assert.Contains(DesktopBranding.DisplayName, overlay.Text);
                Assert.Contains("utilizado", overlay.AccessibleDescription);
                DesktopLocalization.SetLanguage("en");
                overlay.UpdateUsage(config, []);
                Assert.Equal($"{DesktopBranding.DisplayName} taskbar usage", overlay.Text);
                Assert.Contains("used", overlay.AccessibleDescription);
            }
            catch (Exception exception) { failure = exception; }
            finally { DesktopLocalization.SetLanguage(initial); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)));
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Theory]
    [InlineData("es", "Actualizar ahora", "Configuración", "barra de tareas")]
    [InlineData("fr", "Actualiser maintenant", "Paramètres", "barre des tâches")]
    public async Task SavingLanguageUpdatesTheNativeTrayAndPersistsOnlyTheFixtureConfiguration(string language, string refresh, string settings, string overlayTitle)
    {
        var initial = DesktopLocalization.Language;
        var directory = Path.Combine(Path.GetTempPath(), "estao-native-language-" + Guid.NewGuid().ToString("N"));
        var store = new ConfigStore(Path.Combine(directory, "config.json"));
        var config = ConfigStore.Normalize(ConfigStore.CreateDefaultConfig());
        config.Language = "en";
        config.Refresh.Enabled = false;
        config.TaskbarOverlay.Enabled = false;
        foreach (var provider in config.Providers) provider.Enabled = false; // Never contact actual provider accounts.
        await store.SaveAsync(config);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var context = new TrayApplicationContext(store);
                var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var overlay = (ProviderOverlay)typeof(TrayApplicationContext).GetField("_overlay", flags)!.GetValue(context)!;
                using var deadline = new System.Windows.Forms.Timer { Interval = 10_000 };
                deadline.Tick += (_, _) => { failure = new TimeoutException("Language save did not finish."); context.ExitThread(); };
                deadline.Start();
                overlay.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        var update = new SettingsUpdate([], config.UsageAlerts!, config.Refresh, false, [], StartupManager.Enabled,
                            DesktopThemeCatalog.Resolve(config.Theme).Id, language);
                        await (Task)typeof(TrayApplicationContext).GetMethod("SaveSettingsAsync", flags)!.Invoke(context, [update])!;
                        var tray = (System.Windows.Forms.NotifyIcon)typeof(TrayApplicationContext).GetField("_tray", flags)!.GetValue(context)!;
                        Assert.Equal(refresh, tray.ContextMenuStrip!.Items[1].Text);
                        Assert.Contains(settings, tray.ContextMenuStrip.Items[2].Text);
                        Assert.Equal(language, (await store.LoadAsync()).Language);
                        Assert.Contains(overlayTitle, overlay.Text);
                    }
                    catch (Exception exception) { failure = exception; }
                    finally { deadline.Stop(); context.ExitThread(); }
                }));
                System.Windows.Forms.Application.Run(context);
            }
            catch (Exception exception) { failure = exception; }
            finally { DesktopLocalization.SetLanguage(initial); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally { if (!thread.IsAlive && Directory.Exists(directory)) Directory.Delete(directory, true); }
    }
}
