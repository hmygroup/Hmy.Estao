using System.Collections.Concurrent;
using System.Reflection;
using Hmy.Estao.Core.Configuration;

namespace Hmy.Estao.Desktop.Tests;

public sealed class SettingsStartupTests
{
    [Fact]
    public async Task InitialStateDoesNotWaitForSynchronousProviderStartupAndPublishesResultsLater()
    {
        var directory = Path.Combine(Path.GetTempPath(), "estao-startup-" + Guid.NewGuid().ToString("N"));
        var store = new ConfigStore(Path.Combine(directory, "config.json"));
        var config = ConfigStore.Normalize(ConfigStore.CreateDefaultConfig());
        config.Refresh.Enabled = false;
        config.TaskbarOverlay.Enabled = false;
        foreach (var provider in config.Providers) provider.Enabled = false;
        await store.SaveAsync(config);
        var initialLanguage = DesktopLocalization.Language;
        using var release = new ManualResetEventSlim();
        var entered = new ConcurrentDictionary<string, int>();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var uiThread = Environment.CurrentManagedThreadId;
                using var context = new TrayApplicationContext(store, (provider, ct) =>
                {
                    entered[provider.Id] = Environment.CurrentManagedThreadId;
                    // Deliberately block before returning a Task, just like Process.Start.
                    if (!release.Wait(TimeSpan.FromSeconds(5), ct)) throw new TimeoutException("Provider startup was not released.");
                    return Task.FromResult(new CodexAccountStatus(true, "Signed in"));
                });
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var type = typeof(TrayApplicationContext);
                var overlay = (ProviderOverlay)type.GetField("_overlay", flags)!.GetValue(context)!;
                using var settings = (DesktopWindow)type.GetMethod("CreateWindow", flags)!.Invoke(context, ["settings"])!;
                type.GetField("_settings", flags)!.SetValue(context, settings);
                using var deadline = new System.Windows.Forms.Timer { Interval = 10_000 };
                deadline.Tick += (_, _) =>
                {
                    failure = new TimeoutException("Settings state waited for provider startup.");
                    release.Set();
                    context.ExitThread();
                };
                deadline.Start();
                overlay.BeginInvoke(new Action(async () =>
                {
                    try
                    {
                        Task<DesktopState> State() => (Task<DesktopState>)type.GetMethod("StateAsync", flags)!.Invoke(context, ["settings"])!;
                        var initial = await State();
                        Assert.False(release.IsSet);
                        Assert.All(initial.Providers, p => Assert.True(p.Authentication?.Checking));
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        while (entered.Count < initial.Providers.Count) await Task.Delay(10, timeout.Token);
                        Assert.All(entered.Values, id => Assert.NotEqual(uiThread, id));
                        release.Set();
                        DesktopState updated;
                        do { await Task.Delay(10, timeout.Token); updated = await State(); }
                        while (updated.Providers.Any(p => p.Authentication?.SignedIn != true));
                        Assert.All(updated.Providers, p => Assert.False(p.Authentication!.Checking));
                    }
                    catch (Exception exception) { failure = exception; }
                    finally { release.Set(); deadline.Stop(); context.ExitThread(); }
                }));
                System.Windows.Forms.Application.Run(context);
            }
            catch (Exception exception) { failure = exception; }
            finally { release.Set(); DesktopLocalization.SetLanguage(initialLanguage); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            Assert.True(thread.Join(TimeSpan.FromSeconds(15)));
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally
        {
            release.Set();
            if (!thread.IsAlive && Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
