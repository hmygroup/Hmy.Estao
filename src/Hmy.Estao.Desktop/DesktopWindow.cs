using Shofia.WebHost;
using Shofia.WebHost.Frontend;
using System.Text.Json;
using Hmy.Estao.Desktop.HarnessHub;
using Hmy.Estao.Core.Models;

namespace Hmy.Estao.Desktop;

internal sealed class DesktopWindow : CustomChromeWebViewHostForm
{
    private readonly List<IDisposable> _handlers = [];
    private readonly string _initialTheme;
    private bool _initializationStarted;
    private readonly HarnessHubSession? _hub;
    private readonly string _view;

    public DesktopWindow(string view, Func<Task<DesktopState>> state,
        Func<SettingsUpdate, Task> save, Func<DesktopCommand, Task> command, string? theme = null, string? configPath = null,
        Func<ProviderSettings, Task>? providerSignIn = null)
        : base(new WebViewHostOptions(new Uri(LocalFrontendContent.Origin, $"/?view={view}"), LocalFrontendContent.Origin)
        {
            LocalFrontend = new LocalFrontendContent(typeof(DesktopWindow).Assembly),
            UserDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hmy.Estao", "WebView2"),
            AllowSingleSignOnUsingOSPrimaryAccount = false
        })
    {
        var appearance = DesktopThemeCatalog.Resolve(theme);
        _initialTheme = appearance.Id;
        BackColor = ColorTranslator.FromHtml(appearance.Colors["canvas"]);
        WebView.DefaultBackgroundColor = BackColor;
        _view = view;
        UpdateLanguage();
        ShowInTaskbar = view == "settings";
        MinimizeBox = view == "settings";
        MaximizeBox = view == "settings";
        StartPosition = FormStartPosition.Manual;
        Size = view == "settings" ? new Size(1080, 800) : new Size(500, 700);
        MinimumSize = view == "settings" ? new Size(780, 580) : new Size(400, 400);
        TopMost = view != "settings";
        _handlers.Add(Messages.Handle(DesktopContracts.State, async (_, ct) =>
            await OnUiAsync(state, ct)));
        if (view == "settings")
        {
            if (providerSignIn is not null)
                _handlers.Add(Messages.Handle(DesktopContracts.ProviderSignIn, async (request, ct) =>
                    await OnUiCommandAsync(() => providerSignIn(request), ct)));
            _hub = new HarnessHubSession(configPath ?? new Hmy.Estao.Core.Configuration.ConfigStore().Path);
            _handlers.Add(Messages.Handle(HubContracts.State, (_, _) => ValueTask.FromResult(_hub.State)));
            _handlers.Add(Messages.Handle(HubContracts.Command, async (request, ct) =>
                await OnUiCommandAsync(() => { _hub.Command(request); return Task.CompletedTask; }, ct)));
            _handlers.Add(Messages.Handle(DesktopContracts.Save, async (update, ct) =>
            {
                return await OnUiCommandAsync(() => save(update), ct);
            }));
        }
        _handlers.Add(Messages.Handle(DesktopContracts.Command, async (request, ct) =>
        {
            return await OnUiCommandAsync(() => command(request), ct);
        }));
        BrowserInitializationFailed += (_, _) =>
        {
            MessageBox.Show(this, DesktopLocalization.Text("native.webview_required"),
                DesktopBranding.DisplayName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        };
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    public void UpdateLanguage() => Text = $"{DesktopBranding.DisplayName} · {DesktopLocalization.Text(_view == "settings" ? "settings" : "usage")}";

    private Task<EmptyRequest> OnUiCommandAsync(Func<Task> action, CancellationToken ct) => OnUiAsync(async () =>
    {
        try { await action(); return new EmptyRequest(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception) { return new EmptyRequest(UserMessage.FromException(exception)); }
    }, ct);

    public void BeginInitialization()
    {
        _ = Handle;
        OnLoad(EventArgs.Empty);
    }

    protected override void OnLoad(EventArgs e)
    {
        // WebHost initializes on Load. Allow that work to start while hidden,
        // and do not repeat it when WinForms subsequently shows this window.
        if (_initializationStarted) return;
        _initializationStarted = true;
        base.OnLoad(e);
    }

    protected override async ValueTask OnBrowserInitializedAsync(CancellationToken cancellationToken)
    {
        await base.OnBrowserInitializedAsync(cancellationToken);
        // Supply just the validated theme ID before the page executes, avoiding
        // a light/dark flash while React waits for the first native state reply.
        await WebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
            $"window.__estaoInitialTheme = {JsonSerializer.Serialize(_initialTheme)};");
    }

    private async Task<T> OnUiAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        Task<T>? pending = null;
        await InvokeAsync(() => { pending = action(); }, ct);
        return await pending!;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hub?.Dispose();
            foreach (var handler in _handlers) handler.Dispose();
        }
        base.Dispose(disposing);
    }
}
