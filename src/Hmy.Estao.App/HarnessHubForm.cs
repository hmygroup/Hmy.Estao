using System.Diagnostics;
using System.Net;
using Hmy.Estao.App.Controls.Zarpa;
using Hmy.Estao.App.Services;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Harnesses.Installer;
using ZarpaSuite.Controls;

namespace Hmy.Estao.App;

public sealed class HarnessHubForm : ZarpaModernForm
{
    private readonly ConfigStore _configStore;
    private readonly ZarpaThemeManager _theme = new() { Density = ZarpaDensity.Comfortable };
    private readonly HttpClient _client = GitHubRepositorySource.CreateClient();
    private readonly HarnessGitHubAuthorization _authorization;
    private readonly HarnessInstallationService _installer;
    private readonly HubOperation _operation = new();
    private readonly Dictionary<string, (Label Status, ZarpaButton Action)> _cards = [];
    private readonly Label _connection = new() { Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, AccessibleName = "Estado de acceso al repositorio de GitHub", Text = "GitHub · comprobando acceso al repositorio…" };
    private readonly Label _progress = new() { Dock = DockStyle.Fill, AutoEllipsis = true, Text = "Las configuraciones personales se conservan. Los conflictos nunca se sobrescriben." };
    private readonly ZarpaButton _connect = new() { Text = "Autorizar GitHub", Width = 160, ButtonStyle = ZarpaButtonStyle.Secondary };
    private readonly ZarpaButton _detailsButton = new() { Text = "Ver detalles", Width = 110, ButtonStyle = ZarpaButtonStyle.Subtle };
    private readonly ZarpaButton _cancel = new() { Text = "Cancelar", Width = 100, Enabled = false, ButtonStyle = ZarpaButtonStyle.Secondary };
    private readonly TextBox _details = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, WordWrap = true, BorderStyle = BorderStyle.FixedSingle, Dock = DockStyle.Fill, Visible = false, AccessibleName = "Detalles de instalación", Text = "Selecciona un harness para ver la cobertura del snapshot, sus requisitos y los destinos antes de aplicar cambios." };
    private CancellationTokenSource? _operationCancellation;
    private string? _token;
    private bool _closeWhenFinished;

    public HarnessHubForm(ConfigStore configStore)
    {
        _configStore = configStore;
        var data = Path.GetDirectoryName(Path.GetFullPath(configStore.Path))!;
        _authorization = new(data);
        _installer = new(data);
        Text = "Harness Hub";
        ContextText = "Configuraciones de desarrollo";
        TitleIconKey = "ic_fluent_apps_list_detail_24_regular";
        MinimumSize = new Size(780, 650);
        Size = new Size(940, 780);
        StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(24) };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 72));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 306));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var header = new ZarpaSectionHeader { TitleText = "Instala tu configuración de desarrollo", DescriptionText = "Skills compartidas de HMY · ámbito personal · sin ejecutar scripts", Dock = DockStyle.Fill };
        var connectionBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        connectionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 172));
        connectionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        connectionBar.Controls.Add(_connect, 0, 0);
        connectionBar.Controls.Add(_connection, 1, 0);
        var cards = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        cards.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        cards.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        cards.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        foreach (var id in HarnessAdapter.Ids) cards.Controls.Add(BuildCard(id));
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
        footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 120));
        footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        footer.Controls.Add(_progress, 0, 0);
        footer.Controls.Add(_detailsButton, 1, 0);
        footer.Controls.Add(_cancel, 2, 0);
        layout.Controls.Add(header);
        layout.Controls.Add(connectionBar);
        layout.Controls.Add(cards);
        layout.Controls.Add(footer);
        layout.Controls.Add(_details);
        Controls.Add(layout);
        _detailsButton.Click += (_, _) => _details.Visible = !_details.Visible;
        _connect.Click += async (_, _) => await ConnectAsync();
        _cancel.Click += (_, _) => _operationCancellation?.Cancel();
        Load += async (_, _) => await InitializeAsync();
        FormClosing += (_, e) =>
        {
            if (!_operation.IsBusy) return;
            e.Cancel = true;
            _closeWhenFinished = true;
            _operationCancellation?.Cancel();
            _progress.Text = "Cancelando de forma segura…";
        };
        _theme.Attach(this);
    }

    private Control BuildCard(string id)
    {
        var names = new Dictionary<string, string> { ["codex"] = "OpenAI Codex", ["claude"] = "Claude Code", ["copilot"] = "GitHub Copilot CLI", ["opencode"] = "OpenCode" };
        var card = new HarnessInstallCard(id, names[id]) { Dock = DockStyle.Fill, Margin = new Padding(6) };
        card.Action.Click += async (_, _) => await InstallAsync(id);
        _cards[id] = (card.Status, card.Action);
        return card;
    }

    private async Task InitializeAsync()
    {
        _operation.Begin();
        SetBusy(true);
        try
        {
            var config = await _configStore.LoadAsync();
            _theme.Preset = ZarpaThemePreferences.Parse(config.Theme);
            _theme.BackdropStyle = ZarpaBackdropStyle.None;
            foreach (var id in HarnessAdapter.Ids)
            {
                try
                {
                    var state = await Task.Run(() => _installer.GetInstallationStatusAsync(new HarnessAdapter(id)));
                    _cards[id].Status.Text = state == HubOperationState.Ready ? "Listo · detección al instalar" : StateText(state);
                    _cards[id].Action.Text = state == HubOperationState.RecoveryRequired ? "Recuperar" : state == HubOperationState.Ready ? "Instalar" : "Comprobar / actualizar";
                }
                catch { _cards[id].Status.Text = "Perfil no disponible · ver al instalar"; }
            }
            var candidates = await _authorization.CandidatesAsync(CancellationToken.None);
            _token = null;
            var hasCredential = candidates.Count > 0;
            foreach (var candidate in candidates)
            {
                try
                {
                    await new GitHubRepositorySource(_client).VerifyAccessAsync(candidate);
                    _token = candidate;
                    break;
                }
                catch (GitHubAccessException) { }
            }
            _connection.Text = _token is not null ? "GitHub · acceso autorizado" :
                hasCredential ? "GitHub · token encontrado, pero sin acceso al repositorio" : "GitHub · desconectado";
            _operation.Set(_token is null ? HubOperationState.NotConnected : HubOperationState.Ready);
        }
        catch (Exception ex) { ShowError(ex); }
        finally { FinishOperation(); }
    }

    private async Task ConnectAsync()
    {
        if (_operation.IsBusy) return;
        _operation.Begin();
        SetBusy(true);
        _connection.Text = "GitHub · verificando acceso…";
        _operationCancellation = new();
        try
        {
            var deviceAuthorization = await OAuthLoginService.StartHarnessDeviceFlowAsync(_operationCancellation.Token);
            using var dialog = new GitHubDeviceFlowDialog(deviceAuthorization, _theme.Preset);
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            await ValidateAndSaveAsync(dialog.AccessToken!, _operationCancellation.Token);
            _operation.Set(HubOperationState.Ready);
            _connection.Text = "GitHub · acceso autorizado a la biblioteca privada";
        }
        catch (OperationCanceledException) { _operation.Set(HubOperationState.NotConnected); _progress.Text = "Conexión cancelada."; }
        catch (Exception ex) { ShowError(ex); _connection.Text = ex is GitHubAccessException ? "GitHub · autenticado, sin acceso al repositorio" : "GitHub · error de autenticación"; }
        finally { FinishOperation(); }
    }

    private async Task ValidateAndSaveAsync(string token, CancellationToken ct)
    {
        // Reading the private tree proves content access; a /user identity response is insufficient.
        await new GitHubRepositorySource(_client).VerifyAccessAsync(token, ct);
        await _authorization.SaveAsync(token, ct);
        _token = token;
    }

    private async Task InstallAsync(string id)
    {
        if (_operation.IsBusy) return;
        _operation.Begin();
        SetBusy(true, id);
        _operationCancellation = new();
        var ct = _operationCancellation.Token;
        try
        {
            var adapter = new HarnessAdapter(id);
            _cards[id].Action.Text = "Comprobando…";
            if (await Task.Run(() => _installer.GetInstallationStatusAsync(adapter), ct) == HubOperationState.RecoveryRequired)
            {
                var issues = await Task.Run(() => _installer.RecoverAsync(adapter, ct), ct);
                _details.Text = string.Join(Environment.NewLine, issues);
                _progress.Text = issues.Count == 0 ? "Recuperación completada. Ya puedes instalar." : "Se han conservado cambios posteriores. Revisa los detalles antes de continuar.";
                _operation.Set(issues.Count == 0 ? HubOperationState.Ready : HubOperationState.RecoveryRequired);
                _cards[id].Status.Text = StateText(_operation.State);
                return;
            }
            if (!Detect(id) && ZarpaDialog.Show(this, adapter.DisplayName, "No se detecta el harness en PATH. ¿Preparar únicamente sus archivos de configuración personal? No se instalará ni ejecutará el programa.",
                    ZarpaFeedbackKind.Information, ZarpaDialogButtons.YesNo, _theme.Theme) != DialogResult.Yes) return;
            PreparedSnapshot? snapshot = null;
            var candidates = (await _authorization.CandidatesAsync(ct)).ToList();
            if (_token is not null) candidates.Insert(0, _token);
            foreach (var candidate in candidates.Distinct(StringComparer.Ordinal))
            {
                try { snapshot = await new GitHubRepositorySource(_client).PrepareAsync(candidate, adapter, Progress(), ct); _token = candidate; break; }
                catch (GitHubAccessException ex) when (ex.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound) { }
            }
            if (snapshot is null) throw new GitHubAccessException("Conecta GitHub con acceso de lectura al repositorio privado. Copilot no se modifica.", HttpStatusCode.Unauthorized);
            _connection.Text = "GitHub · acceso verificado";
            _operation.Set(HubOperationState.Planning);
            _cards[id].Action.Text = "Preparando…";
            _progress.Text = "Preparando cambios y comprobando conflictos…";
            var plan = await Task.Run(() => _installer.PlanAsync(adapter, snapshot, ct), ct);
            _details.Text = string.Join(Environment.NewLine, new[] { $"Fuente: {snapshot.Commit}", $"Contrato: {snapshot.ManifestVersion}", "Cobertura:" }
                .Concat(snapshot.Coverage.Select(c => $"{c.Classification} · {c.Source} · {c.FileCount} archivo(s) · {c.Reason}"))
                .Append("Destinos:").Concat(plan.Files.Select(f => $"{f.Action} · {f.Path} · {f.Reason}")));
            if (plan.Files.Count == 0)
            {
                _operation.Set(HubOperationState.Ready);
                _progress.Text = "Este snapshot no declara configuraciones compatibles. Consulta la cobertura; no se ha instalado nada.";
                return;
            }
            if (plan.Files.Any(f => f.Action == InstallAction.Conflict) && ZarpaDialog.Show(this,
                    "Hay contenido local que se conservará. Se omitirá el grupo completo cuando sus recursos tengan conflictos.\n\n¿Instalar los elementos compatibles y conservar los conflictos?",
                    "Conservar configuración local", ZarpaFeedbackKind.Warning, ZarpaDialogButtons.OkCancel, _theme.Theme) != DialogResult.OK) return;
            _operation.Set(HubOperationState.Installing);
            _cards[id].Action.Text = "Instalando…";
            var installProgress = Progress();
            var result = await Task.Run(() => _installer.ApplyAsync(adapter, plan, preserveConflicts: true, installProgress, ct), ct);
            _operation.Set(result.Conflicts > 0 ? HubOperationState.PartiallyInstalled : HubOperationState.Installed);
            _cards[id].Status.Text = StateText(_operation.State);
            _cards[id].Action.Text = "Comprobar / actualizar";
            _progress.Text = $"{result.Created} nuevos · {result.Updated} actualizados · {result.Unchanged} idénticos · {result.Conflicts} conservados";
        }
        catch (OperationCanceledException) { _operation.Set(HubOperationState.Ready); _progress.Text = "Operación cancelada; los cambios aplicados se han recuperado cuando era seguro."; }
        catch (Exception ex) { ShowError(ex); _cards[id].Status.Text = "Error · ver detalles / reintentar"; _cards[id].Action.Text = "Reintentar"; }
        finally
        {
            if (_cards[id].Action.Text is "Comprobando…" or "Preparando…" or "Instalando…")
                _cards[id].Action.Text = _operation.State == HubOperationState.Error ? "Reintentar" : "Instalar";
            FinishOperation();
        }
    }

    private IProgress<string> Progress() => new Progress<string>(text => { if (!IsDisposed) _progress.Text = text; });
    private void SetBusy(bool busy, string? activeCardId = null)
    {
        _connect.Enabled = !busy;
        _cancel.Enabled = busy;
        foreach (var (id, card) in _cards)
        {
            var active = busy && id == activeCardId;
            card.Action.Loading = active;
            if (!active) card.Action.Enabled = !busy;
        }
        UseWaitCursor = busy;
    }
    private void FinishOperation()
    {
        if (_operation.IsBusy) _operation.Set(_token is null ? HubOperationState.NotConnected : HubOperationState.Ready);
        _operationCancellation?.Dispose();
        _operationCancellation = null;
        SetBusy(false);
        if (_closeWhenFinished) Close();
    }
    private void ShowError(Exception ex)
    {
        _operation.Set(ex is GitHubAccessException ? HubOperationState.NoRepositoryAccess : HubOperationState.Error);
        _progress.Text = ex is GitHubAccessException or InvalidDataException or IOException or InvalidOperationException ? ex.Message : "No se pudo completar la operación. Reintenta o revisa los permisos del perfil.";
        _details.Text = _progress.Text;
        _details.Visible = true;
    }
    private static string StateText(HubOperationState state) => state switch
    {
        HubOperationState.RecoveryRequired => "Recuperación pendiente",
        HubOperationState.Installed => "Instalado · comprobar para actualizar",
        HubOperationState.PartiallyInstalled => "Instalación parcial · contenido local conservado",
        HubOperationState.Conflict => "Modificaciones locales · se conservarán",
        HubOperationState.UpdateAvailable => "Actualización disponible",
        _ => "Listo para instalar"
    };
    private static bool Detect(string id)
    {
        var executable = id == "copilot" ? "copilot" : id;
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(Directory.Exists)
            .Any(dir => new[] { ".exe", ".cmd", ".ps1" }.Any(ext => File.Exists(Path.Combine(dir, executable + ext))));
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { _operationCancellation?.Cancel(); _client.Dispose(); _theme.Dispose(); }
        base.Dispose(disposing);
    }
}

internal sealed class HarnessInstallCard : ZarpaCardPanel, IZarpaThemeAware, IZarpaThemeBoundary
{
    private readonly string _id;
    private readonly TableLayoutPanel _body = new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2 };
    private readonly PictureBox _icon = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.CenterImage };
    public Label Status { get; } = new() { Text = "Comprobando…", Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft };
    public ZarpaButton Action { get; } = new() { Text = "Instalar", Width = 190, ButtonStyle = ZarpaButtonStyle.Primary };

    public HarnessInstallCard(string id, string name)
    {
        _id = id;
        TitleText = name;
        Compact = true;
        RoundContentCorners = false;
        _body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 58));
        _body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _body.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        _body.Controls.Add(_icon, 0, 0);
        _body.SetRowSpan(_icon, 2);
        _body.Controls.Add(Status, 1, 0);
        _body.Controls.Add(Action, 1, 1);
        Action.AccessibleName = "Instalar " + name;
        Controls.Add(_body);
    }
    public new void ApplyTheme(ZarpaThemeTokens theme)
    {
        base.ApplyTheme(theme);
        _body.BackColor = _icon.BackColor = Status.BackColor = theme.Surface;
        Status.ForeColor = theme.TextMuted;
        Status.Font = Font;
        Action.ApplyTheme(theme);
        var previous = _icon.Image;
        _icon.Image = ZarpaProviderIconCatalog.CreateBitmap(_id, 38, theme.Text);
        previous?.Dispose();
    }
    protected override void Dispose(bool disposing) { if (disposing) _icon.Image?.Dispose(); base.Dispose(disposing); }
}

internal sealed class GitHubDeviceFlowDialog : ZarpaModernForm
{
    private readonly GitHubDeviceAuthorization _authorization;
    private readonly ZarpaThemeManager _theme = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Label _status = new() { Dock = DockStyle.Fill, Text = "Esperando confirmación en GitHub…", TextAlign = ContentAlignment.MiddleLeft };
    private bool _finished;
    public string? AccessToken { get; private set; }

    public GitHubDeviceFlowDialog(GitHubDeviceAuthorization authorization, ZarpaThemePreset preset)
    {
        _authorization = authorization;
        _theme.Preset = preset;
        Text = "Conectar con GitHub";
        TitleIconKey = "ic_fluent_lock_shield_24_regular";
        Size = new Size(560, 330);
        MinimizeBox = false;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterParent;

        var body = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 1, RowCount = 4 };
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        body.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        body.Controls.Add(new ZarpaSectionHeader { TitleText = "Autoriza Estao en GitHub", DescriptionText = "No tienes que crear ni pegar ningún token.", Dock = DockStyle.Fill }, 0, 0);

        var codeRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1 };
        codeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        codeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));
        var code = new Label { Dock = DockStyle.Fill, Text = authorization.UserCode, Font = new Font("Segoe UI", 21F, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter, AccessibleName = "Código de autorización GitHub" };
        var copy = new ZarpaButton { Text = "Copiar código", Dock = DockStyle.Fill, ButtonStyle = ZarpaButtonStyle.Secondary };
        copy.Click += (_, _) => { Clipboard.SetText(authorization.UserCode); _status.Text = "Código copiado. Introdúcelo en la página de GitHub."; };
        codeRow.Controls.Add(code, 0, 0);
        codeRow.Controls.Add(copy, 1, 0);
        body.Controls.Add(codeRow, 0, 1);
        body.Controls.Add(_status, 0, 2);

        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false };
        var cancel = new ZarpaButton { Text = "Cancelar", Width = 110, DialogResult = DialogResult.Cancel, ButtonStyle = ZarpaButtonStyle.Subtle };
        var open = new ZarpaButton { Text = "Abrir GitHub", Width = 130, ButtonStyle = ZarpaButtonStyle.Primary };
        open.Click += (_, _) => OpenVerificationPage();
        actions.Controls.Add(cancel);
        actions.Controls.Add(open);
        body.Controls.Add(actions, 0, 3);
        Controls.Add(body);
        CancelButton = cancel;
        _theme.Attach(this);
        Shown += async (_, _) => await WaitForAuthorizationAsync();
    }

    private void OpenVerificationPage()
    {
        try { Process.Start(new ProcessStartInfo(_authorization.VerificationUri) { UseShellExecute = true }); }
        catch (Exception ex) { _status.Text = $"Abre github.com/login/device e escribe el código. {ex.Message}"; }
    }

    private async Task WaitForAuthorizationAsync()
    {
        OpenVerificationPage();
        try
        {
            AccessToken = await OAuthLoginService.CompleteHarnessDeviceFlowAsync(_authorization, _cancellation.Token);
            _status.Text = "Autorización recibida. Comprobando acceso a la biblioteca privada…";
            _finished = true;
            DialogResult = DialogResult.OK;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _status.Text = $"No se completó la autorización: {ex.Message}"; }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_finished) _cancellation.Cancel();
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing) { if (disposing) { _cancellation.Dispose(); _theme.Dispose(); } base.Dispose(disposing); }
}
