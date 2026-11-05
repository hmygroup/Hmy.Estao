using Shofia.WebHost;
using Shofia.WebHost.Frontend;

namespace Hmy.Estao.Desktop;

internal sealed class DesktopWindow : CustomChromeWebViewHostForm
{
    private readonly List<IDisposable> _handlers = [];

    public DesktopWindow(string view, Func<Task<DesktopState>> state,
        Func<SettingsUpdate, Task> save, Func<DesktopCommand, Task> command)
        : base(new WebViewHostOptions(new Uri(LocalFrontendContent.Origin, $"/?view={view}"), LocalFrontendContent.Origin)
        {
            LocalFrontend = new LocalFrontendContent(typeof(DesktopWindow).Assembly),
            UserDataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hmy.Estao", "WebView2"),
            AllowSingleSignOnUsingOSPrimaryAccount = false
        })
    {
        Text = view == "settings" ? "Estao · Settings" : "Estao · Usage";
        ShowInTaskbar = view == "settings";
        StartPosition = FormStartPosition.Manual;
        Size = view == "settings" ? new Size(1080, 800) : new Size(500, 700);
        MinimumSize = view == "settings" ? new Size(780, 580) : new Size(400, 400);
        TopMost = view != "settings";
        _handlers.Add(Messages.Handle(DesktopContracts.State, async (_, ct) =>
            await OnUiAsync(state, ct)));
        if (view == "settings")
            _handlers.Add(Messages.Handle(DesktopContracts.Save, async (update, ct) =>
            {
                await OnUiAsync(async () => { await save(update); return new EmptyRequest(); }, ct);
                return new EmptyRequest();
            }));
        _handlers.Add(Messages.Handle(DesktopContracts.Command, async (request, ct) =>
        {
            await OnUiAsync(async () => { await command(request); return new EmptyRequest(); }, ct);
            return new EmptyRequest();
        }));
        BrowserInitializationFailed += (_, _) =>
        {
            MessageBox.Show(this, "The settings and usage windows require the Microsoft Edge WebView2 Runtime. The native overlay will continue running.",
                "Estao", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
        };
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
    }

    private async Task<T> OnUiAsync<T>(Func<Task<T>> action, CancellationToken ct)
    {
        Task<T>? pending = null;
        await InvokeAsync(() => { pending = action(); }, ct);
        return await pending!;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) foreach (var handler in _handlers) handler.Dispose();
        base.Dispose(disposing);
    }
}
