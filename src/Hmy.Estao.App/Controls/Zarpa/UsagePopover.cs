using System.Diagnostics;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Models;
using ZarpaSuite.Controls;

namespace Hmy.Estao.App.Controls.Zarpa;

internal sealed class UsagePopover : ZarpaModernForm
{
    private const int WidgetWidth = 470;
    private const int WidgetHeight = 720;
    private readonly Func<Task> _refresh;
    private readonly Action _showSettings;
    private readonly Action _quit;
    private readonly ZarpaReferenceSurface _surface = new();
    private readonly TableLayoutPanel _tabs = new();
    private readonly Panel _tabSpacer = new() { Dock = DockStyle.Top, Height = 6 };
    private readonly ZarpaUsageContent _content = new();
    private readonly ZarpaScrollBar _scrollBar = new() { Orientation = Orientation.Vertical, Dock = DockStyle.Right, Width = 9 };
    private readonly ZarpaThemeManager _theme;
    private IReadOnlyList<UsageSnapshot> _snapshots = [];
    private IReadOnlyList<UsageHistoryPoint> _history = [];
    private string? _selectedProvider;
    private bool _allowDeactivateClose;
    private Size _regionSize;

    public UsagePopover(
        IReadOnlyList<UsageSnapshot> snapshots,
        Func<Task> refresh,
        Action showSettings,
        Action quit,
        ZarpaThemePreset themePreset,
        IReadOnlyList<UsageHistoryPoint>? history = null,
        PacingConfig? pacing = null,
        ZarpaBackdropStyle backdropStyle = ZarpaBackdropStyle.None,
        int backdropOpacity = 96)
    {
        _refresh = refresh;
        _showSettings = showSettings;
        _quit = quit;
        _history = history ?? [];
        _theme = new ZarpaThemeManager
        {
            Preset = themePreset,
            BackdropStyle = backdropStyle,
            BackdropOpacity = backdropOpacity
        };

        // The application is already SystemAware. Scaling this owner-drawn widget a
        // second time makes its children wider than the fixed popover window.
        AutoScaleMode = AutoScaleMode.None;
        ModernChrome = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        DoubleBuffered = true;
        BackColor = ZarpaPopoverPalette.SurfaceTop;
        ClientSize = new Size(WidgetWidth, WidgetHeight);
        FormBorderStyle = FormBorderStyle.None;
        KeyPreview = true;
        MaximizeBox = false;
        MinimizeBox = false;
        Name = nameof(UsagePopover);
        ShowIcon = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "Estao usage";
        TopMost = true;

        _surface.Dock = DockStyle.Fill;
        _surface.Padding = new Padding(14, 10, 14, 16);

        _tabs.Dock = DockStyle.Top;
        _tabs.Height = 38;
        _tabs.BackColor = ZarpaPopoverPalette.SurfaceTop;
        _tabs.ColumnCount = 1;
        _tabs.RowCount = 1;
        _tabs.Margin = Padding.Empty;
        _tabs.Padding = Padding.Empty;
        _tabs.GrowStyle = TableLayoutPanelGrowStyle.FixedSize;
        _tabs.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));

        _content.Dock = DockStyle.Fill;
        _content.Margin = Padding.Empty;
        _content.Pacing = pacing ?? new PacingConfig();
        _content.SettingsRequested += (_, _) => { Close(); _showSettings(); };
        _content.RefreshRequested += async (_, _) => await RefreshFromUiAsync().ConfigureAwait(true);
        _content.QuitRequested += (_, _) => { Close(); _quit(); };
        _content.ScrollChanged += (_, _) => SyncScrollBarFromContent();
        _content.Resize += (_, _) => UpdateScrollBar();
        _scrollBar.ValueChanged += (_, _) => _content.ScrollTo(_scrollBar.Value);

        _surface.Controls.Add(_content);
        _surface.Controls.Add(_scrollBar);
        _surface.Controls.Add(_tabSpacer);
        _surface.Controls.Add(_tabs);
        Controls.Add(_surface);
        _theme.ThemeChanged += (_, _) => ApplyContainerTheme();
        _theme.Attach(this);
        ApplyContainerTheme();
        UpdateScrollBar();
        KeyDown += (_, args) =>
        {
            if (args.KeyCode != Keys.Escape) return;
            args.Handled = true;
            Close();
        };
        Shown += (_, _) => BeginInvoke(() => _allowDeactivateClose = true);
        Deactivate += (_, _) => { if (_allowDeactivateClose && !IsDisposed) Close(); };
        UpdateSnapshots(snapshots);
    }

    protected override bool ShowWithoutActivation => true;

    protected override void Dispose(bool disposing)
    {
        if (disposing) _theme.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateWindowRegion();
    }

    private void UpdateWindowRegion()
    {
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        if (_regionSize == ClientSize && Region is not null) return;
        _regionSize = ClientSize;

        using var path = ZarpaPopoverPaint.RoundedPath(
            new Rectangle(Point.Empty, ClientSize), PopupCornerRadius);
        var previousRegion = Region;
        Region = new Region(path);
        previousRegion?.Dispose();
    }

    private int PopupCornerRadius
    {
        get
        {
            var logicalRadius = _theme is null ? 9 : Math.Max(6, _theme.Theme.GroupCornerRadius);
            return Math.Max(1, (int)Math.Round(logicalRadius * DeviceDpi / 96D));
        }
    }

    public void ShowAt(Point anchor)
    {
        var working = Screen.FromPoint(anchor).WorkingArea;
        var width = Math.Min(WidgetWidth, working.Width);
        var height = Math.Min(WidgetHeight, working.Height);
        ClientSize = new Size(width, height);
        Location = new Point(
            Math.Clamp(anchor.X - width / 2, working.Left, working.Right - width),
            Math.Clamp(anchor.Y - height, working.Top, working.Bottom - height));
        Show();
    }

    public void UpdateSnapshots(IReadOnlyList<UsageSnapshot> snapshots, IReadOnlyList<UsageHistoryPoint>? history = null)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => UpdateSnapshots(snapshots, history));
            return;
        }

        _snapshots = snapshots;
        if (history is not null) _history = history;
        if (_selectedProvider is null || snapshots.All(item =>
                !string.Equals(item.Provider, _selectedProvider, StringComparison.OrdinalIgnoreCase)))
            _selectedProvider = snapshots.FirstOrDefault()?.Provider;

        var providers = ProviderIds();
        var existingProviders = _tabs.Controls.Cast<ZarpaProviderTab>()
            .Select(tab => (string?)tab.Tag)
            .ToArray();
        if (!providers.SequenceEqual(existingProviders, StringComparer.OrdinalIgnoreCase))
            RebuildTabs(providers);
        else
            UpdateTabVisuals();
        ShowSelectedSnapshot();
    }

    public void UpdatePacing(PacingConfig pacing)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => UpdatePacing(pacing));
            return;
        }

        _content.Pacing = pacing;
        ShowSelectedSnapshot();
    }

    public void ApplyTheme(ZarpaThemePreset preset, ZarpaBackdropStyle backdropStyle, int backdropOpacity = 96)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => ApplyTheme(preset, backdropStyle, backdropOpacity));
            return;
        }

        _theme.Preset = preset;
        _theme.BackdropStyle = backdropStyle;
        _theme.BackdropOpacity = backdropOpacity;
        UpdateWindowRegion();
        ApplyContainerTheme();
    }

    private string[] ProviderIds() => ProviderCatalog.InitialProviderIds
        .Concat(_snapshots.Select(item => ProviderCatalog.NormalizeId(item.Provider)))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private void RebuildTabs(string[]? providerIds = null)
    {
        _tabs.SuspendLayout();
        try
        {
            DisposeChildren(_tabs);
            var providers = providerIds ?? ProviderIds();
            _tabs.ColumnStyles.Clear();
            _tabs.ColumnCount = providers.Length;
            foreach (var _ in providers)
                _tabs.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / providers.Length));

            var column = 0;
            foreach (var provider in providers)
            {
                var snapshot = FindSnapshot(provider);
                var tab = new ZarpaProviderTab(TabDisplayName(provider), provider)
                {
                    Active = string.Equals(provider, _selectedProvider, StringComparison.OrdinalIgnoreCase),
                    Available = snapshot is not null && snapshot.Error is null,
                    UsagePercent = PercentUsed(snapshot),
                    Dock = DockStyle.Fill,
                    Margin = new Padding(column == 0 ? 0 : 2, 0,
                        column == providers.Length - 1 ? 0 : 2, 0),
                    Tag = provider
                };
                tab.Click += (_, _) =>
                {
                    if (tab.Tag is string selected) SelectProvider(selected);
                };
                _tabs.Controls.Add(tab, column, 0);
                column++;
            }
        }
        finally
        {
            _tabs.ResumeLayout();
        }
    }

    private void UpdateTabVisuals()
    {
        foreach (ZarpaProviderTab tab in _tabs.Controls)
        {
            var provider = (string?)tab.Tag;
            var snapshot = FindSnapshot(provider);
            tab.Active = string.Equals(provider, _selectedProvider, StringComparison.OrdinalIgnoreCase);
            tab.Available = snapshot is not null && snapshot.Error is null;
            tab.UsagePercent = PercentUsed(snapshot);
        }
    }

    private void SelectProvider(string provider)
    {
        _selectedProvider = provider;
        foreach (ZarpaProviderTab tab in _tabs.Controls)
            tab.Active = string.Equals((string?)tab.Tag, provider, StringComparison.OrdinalIgnoreCase);
        ShowSelectedSnapshot();
    }

    private void ShowSelectedSnapshot()
    {
        _content.Display(FindSnapshot(_selectedProvider), _selectedProvider, _history);
        UpdateScrollBar();
    }

    private void UpdateScrollBar()
    {
        if (_scrollBar.IsDisposed || _content.IsDisposed) return;
        var contentHeight = _content.ContentHeight;
        var viewportHeight = Math.Max(1, _content.ClientSize.Height);
        _scrollBar.SetRange(contentHeight, viewportHeight);
        _scrollBar.Enabled = contentHeight > viewportHeight + 1;
        if (!_scrollBar.Enabled) _scrollBar.Value = 0;
    }

    private void SyncScrollBarFromContent()
    {
        if (_scrollBar.IsDisposed) return;
        _scrollBar.Value = _content.ScrollOffset;
        UpdateScrollBar();
    }

    private static void DisposeChildren(Control parent)
    {
        while (parent.Controls.Count > 0) parent.Controls[0].Dispose();
    }

    private async Task RefreshFromUiAsync()
    {
        _content.SetRefreshing(true);
        try { await _refresh().ConfigureAwait(true); }
        finally { if (!_content.IsDisposed) _content.SetRefreshing(false); }
    }

    private UsageSnapshot? FindSnapshot(string? provider) => _snapshots.FirstOrDefault(item =>
        string.Equals(ProviderCatalog.NormalizeId(item.Provider), ProviderCatalog.NormalizeId(provider ?? string.Empty),
            StringComparison.OrdinalIgnoreCase));

    private static int PercentUsed(UsageSnapshot? snapshot) => snapshot?.Windows.FirstOrDefault()?.PercentUsed is double value
        ? (int)Math.Round(Math.Clamp(value, 0D, 1D) * 100D)
        : 0;

    private static string TabDisplayName(string provider) => ProviderCatalog.NormalizeId(provider) switch
    {
        "copilot" => "Copilot",
        "opencode" => "OpenCode",
        _ => ProviderCatalog.DisplayName(provider)
    };

    private void ApplyContainerTheme()
    {
        _tabs.BackColor = _theme.Theme.Surface;
        _tabSpacer.BackColor = _theme.Theme.Surface;
        _surface.ApplyTheme(_theme.Theme);
        _content.ApplyTheme(_theme.Theme);
        _scrollBar.ApplyTheme(_theme.Theme);
    }

}

internal sealed class ZarpaUsageContent : Panel, IZarpaThemeAware, IZarpaThemeBoundary
{
    private readonly ZarpaBufferedPanel _canvas = new();
    private int _scrollOffset;
    private readonly Font _headingFont = new("Segoe UI", 15F, FontStyle.Bold);
    private readonly Font _sectionFont = new("Segoe UI", 12F, FontStyle.Bold);
    private readonly Font _bodyFont = new("Segoe UI", 10.5F);
    private readonly Font _mutedFont = new("Segoe UI", 9.5F);
    private readonly ZarpaThemeManager _progressTheme = new() { Preset = ZarpaThemePreset.Custom };
    private ZarpaThemeTokens? _activeTheme;
    private bool _refreshing;
    private UsageSnapshot? _snapshot;
    private string? _provider;
    private IReadOnlyList<UsageHistoryPoint> _history = [];
    private PacingConfig _pacing = new();

    public ZarpaUsageContent()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor |
            ControlStyles.UserPaint, true);
        AutoScroll = false;
        HScroll = false;
        VScroll = false;
        BackColor = ZarpaPopoverPalette.SurfaceTop;
        _canvas.BackColor = BackColor;
        _canvas.Location = Point.Empty;
        _canvas.Size = new Size(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height));
        _canvas.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        Controls.Add(_canvas);
        Resize += (_, _) => ResizeCanvas();
        _canvas.MouseWheel += OnCanvasMouseWheel;
        _progressTheme.Theme.Canvas = ZarpaPopoverPalette.SurfaceTop;
        _progressTheme.Theme.SurfaceRaised = ZarpaPopoverPalette.Track;
        _progressTheme.Theme.Accent = ZarpaPopoverPalette.Meter;
    }

    public event EventHandler? SettingsRequested;
    public event EventHandler? RefreshRequested;
    public event EventHandler? QuitRequested;
    public event EventHandler? ScrollChanged;

    public int ContentHeight => Math.Max(ClientSize.Height, _canvas.Height);
    public int ScrollOffset => _scrollOffset;

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public PacingConfig Pacing
    {
        get => _pacing;
        set
        {
            _pacing = value;
            if (_provider is not null || _snapshot is not null) Display(_snapshot, _provider, _history);
        }
    }

    public void ScrollTo(int value)
    {
        var maximum = Math.Max(0, ContentHeight - ClientSize.Height);
        var next = Math.Clamp(value, 0, maximum);
        if (_scrollOffset == next) return;
        _scrollOffset = next;
        _canvas.Top = -_scrollOffset;
        ScrollChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    public void ApplyTheme(ZarpaThemeTokens value)
    {
        _activeTheme = value;
        BackColor = value.Surface;
        _canvas.BackColor = value.Surface;
        _progressTheme.Theme.Canvas = value.Surface;
        _progressTheme.Theme.SurfaceRaised = value.SurfaceRaised;
        _progressTheme.Theme.Accent = value.Warning;
        if (_provider is not null || _snapshot is not null) Display(_snapshot, _provider, _history);
        else Invalidate();
    }

    public void Display(UsageSnapshot? snapshot, string? provider, IReadOnlyList<UsageHistoryPoint>? history = null)
    {
        _snapshot = snapshot;
        _provider = provider;
        if (history is not null) _history = history;
        SuspendLayout();
        try
        {
            DisposeChildren();
            var contentWidth = ContentWidth;
            var displayName = snapshot?.DisplayName ?? ProviderCatalog.DisplayName(provider ?? "codex");
            var y = 0;
            AddText(displayName, _headingFont, TextColor, 0, y, contentWidth * 2 / 3, 29);
            AddText(snapshot is null ? "Waiting for usage data" : UpdatedText(snapshot.UpdatedAt), _mutedFont,
                MutedColor, 0, y + 28, contentWidth * 2 / 3, 21);
            AddText(snapshot?.Plan ?? "Max", _mutedFont, MutedColor,
                contentWidth * 2 / 3, y + 28, contentWidth / 3, 21,
                ContentAlignment.MiddleRight);
            y += 57;
            AddSeparator(y);
            y += 16;

            if (snapshot?.Error is { Length: > 0 } error)
            {
                AddText("Usage unavailable", _sectionFont, TextColor, 0, y, contentWidth, 26);
                AddText(error, _bodyFont, MutedColor, 0, y + 29, contentWidth, 51);
                y += 88;
            }
            else if (snapshot is null || snapshot.Windows.Count == 0)
            {
                AddText("Loading usage…", _sectionFont, TextColor, 0, y, contentWidth, 26);
                AddProgress(30, y + 31);
                y += 57;
            }
            else
            {
            foreach (var window in snapshot.Windows)
                y = AddWindow(window, y);

            var chartProvider = ProviderCatalog.NormalizeId(provider ?? snapshot?.Provider ?? "codex");
            var providerHistory = _history.Where(point =>
                string.Equals(point.Provider, chartProvider, StringComparison.OrdinalIgnoreCase)).ToArray();
            if ((snapshot is not null && snapshot.Error is null && snapshot.Windows.Count > 0) || providerHistory.Length > 0)
            {
                AddSeparator(y);
                y += 10;
                AddUsageChart(snapshot, provider, y);
                y += 199;
            }
            }

            if (snapshot?.Credits is not null)
            {
                AddSeparator(y);
                y += 15;
                AddText("Extra usage", _sectionFont, TextColor, 0, y, contentWidth, 26);
                AddProgress(0, y + 30);
                var balance = snapshot.Credits.Unlimited == true ? "Unlimited" :
                    $"Balance: {snapshot.Credits.Balance:0.##} {snapshot.Credits.Unit}".TrimEnd();
                AddText(balance, _bodyFont, TextColor, 0, y + 43, contentWidth, 23);
                y += 76;
            }

            AddSeparator(y);
            y += 13;
            AddText("Account", _sectionFont, TextColor, 0, y, contentWidth, 26);
            AddText(snapshot?.Account ?? "No account selected", _bodyFont, TextColor, 0, y + 27, contentWidth, 23);
            AddText(snapshot is null ? string.Empty : $"Source: {snapshot.Source}", _mutedFont,
                MutedColor, 0, y + 49, contentWidth, 21);
            y += 78;

            AddSeparator(y);
            y += 8;
            AddAction("Add Account…", string.Empty, y, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty));
            y += 34;
            AddAction(_refreshing ? "Refreshing…" : "Refresh usage", string.Empty, y,
                (_, _) => { if (!_refreshing) RefreshRequested?.Invoke(this, EventArgs.Empty); });
            y += 34;
            AddAction("Usage Dashboard", string.Empty, y,
                (_, _) => OpenDashboard(provider));
            y += 34;
            AddAction("Status Page", string.Empty, y,
                (_, _) => OpenStatus(provider));
            y += 42;

            AddSeparator(y);
            y += 8;
            AddAction("Settings…", string.Empty, y, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty));
            y += 34;
            AddAction("About Estao", string.Empty, y, (_, _) => ShowAbout());
            y += 34;
            AddAction("Quit", string.Empty, y, (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty));
            y += 39;
            _canvas.Size = new Size(Math.Max(1, ContentWidth), Math.Max(y, ClientSize.Height));
            _scrollOffset = Math.Clamp(_scrollOffset, 0, Math.Max(0, _canvas.Height - ClientSize.Height));
            _canvas.Top = -_scrollOffset;
        }
        finally
        {
            ResumeLayout(true);
        }
    }

    public void SetRefreshing(bool value)
    {
        _refreshing = value;
        foreach (Control control in _canvas.Controls)
            if (control is ZarpaReferenceAction action && action.Text is "Refresh usage" or "Refreshing…")
            {
                action.Text = value ? "Refreshing…" : "Refresh usage";
                action.Enabled = !value;
                action.Invalidate();
            }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _progressTheme.Dispose();
            _headingFont.Dispose();
            _sectionFont.Dispose();
            _bodyFont.Dispose();
            _mutedFont.Dispose();
        }
        base.Dispose(disposing);
    }

    private void AddUsageChart(UsageSnapshot? snapshot, string? provider, int y)
    {
        var providerId = ProviderCatalog.NormalizeId(provider ?? snapshot?.Provider ?? "codex");
        var palette = new[]
        {
            _activeTheme?.Accent ?? ZarpaPopoverPalette.Accent,
            _activeTheme?.Warning ?? ZarpaPopoverPalette.Meter,
            _activeTheme?.Information ?? Color.FromArgb(30, 157, 190),
            _activeTheme?.Success ?? Color.FromArgb(69, 169, 165)
        };
        var windows = snapshot?.Windows.Select(window => (window.Id, window.Title)).ToList() ?? [];
        foreach (var group in _history
                     .Where(point => string.Equals(point.Provider, providerId, StringComparison.OrdinalIgnoreCase))
                     .GroupBy(point => point.Window, StringComparer.OrdinalIgnoreCase))
        {
            if (windows.All(window => !string.Equals(window.Id, group.Key, StringComparison.OrdinalIgnoreCase)))
                windows.Add((group.Key, WindowTitle(group.Key)));
        }

        var providerHistory = _history.Where(point =>
            string.Equals(point.Provider, providerId, StringComparison.OrdinalIgnoreCase)).ToArray();
        var preview = providerHistory.Select(point => point.Timestamp).Distinct().Count() < 2;
        var series = windows.Take(palette.Length).Select((window, index) =>
        {
            var points = providerHistory
                .Where(point => string.Equals(point.Window, window.Id, StringComparison.OrdinalIgnoreCase))
                .OrderBy(point => point.Timestamp)
                .Select(point => new ZarpaUsageChartPoint(point.Timestamp, point.PercentUsed))
                .ToArray();
            if (points.Length < 2 && preview) points = MockPoints(index);
            return new ZarpaUsageChartSeries(window.Title, palette[index], points);
        }).ToArray();

        var targetLines = _pacing.Enabled && _pacing.DailyTargetPercent > 0 && !preview
            ? series
                .Select(item => BuildTargetSeries(item, _pacing.DailyTargetPercent))
                .Where(item => item is not null)
                .Select(item => item!)
                .ToArray()
            : [];

        var chart = new ZarpaUsageChart
        {
            Location = new Point(0, y),
            Size = new Size(ContentWidth, 184),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            BackColor = SurfaceColor
        };
        chart.SetData([.. series, .. targetLines], preview);
        if (_activeTheme is not null) chart.ApplyTheme(_activeTheme);
        AddContentControl(chart);
    }

    /// <summary>
    /// Builds the dashed daily-pacing reference line for one window's series,
    /// using its own history points to detect the last reset (if any) within
    /// the visible 7-day range.
    /// </summary>
    private static ZarpaUsageChartSeries? BuildTargetSeries(ZarpaUsageChartSeries series, double dailyTargetPercent)
    {
        if (series.Points.Count == 0) return null;

        var now = DateTimeOffset.UtcNow;
        var rangeStart = now - TimeSpan.FromDays(7);
        var points = series.Points
            .Select(point => new PacingPoint(point.Timestamp, point.Value))
            .ToArray();
        var result = PacingCalculator.Compute(points, dailyTargetPercent, rangeStart, now);
        if (result is null) return null;

        var line = result.TargetLine.Select(point => new ZarpaUsageChartPoint(point.Timestamp, point.Value)).ToArray();
        return new ZarpaUsageChartSeries(series.Label, series.Color, line, IsTarget: true);
    }

    private static string WindowTitle(string id) => id.Trim().ToLowerInvariant() switch
    {
        "session" or "primary" or "five_hour" => "Session",
        "weekly" or "secondary" => "Weekly",
        "premium" or "premium_interactions" => "Premium",
        "chat" => "Chat",
        _ => id
    };

    private static ZarpaUsageChartPoint[] MockPoints(int seriesIndex)
    {
        var values = seriesIndex switch
        {
            1 => new[] { .08D, .12D, .16D, .22D, .27D, .31D, .36D },
            _ => new[] { .18D, .24D, .21D, .39D, .34D, .52D, .47D }
        };
        var now = DateTimeOffset.UtcNow;
        return values.Select((value, index) =>
            new ZarpaUsageChartPoint(now.AddDays(index - (values.Length - 1)), value)).ToArray();
    }

    private int AddWindow(RateWindow window, int y)
    {
        var width = ContentWidth;
        AddText(window.Title, _sectionFont, TextColor, 0, y, width, 26);
        var percent = window.PercentUsed is double used ? (int)Math.Round(Math.Clamp(used, 0D, 1D) * 100D) : 0;
        AddProgress(percent, y + 29);
        AddText(window.PercentUsed is null ? "Usage unavailable" : $"{percent}% used", _bodyFont,
            TextColor, 0, y + 41, width / 2, 23);
        AddText(window.ResetAt is null ? "Reset time unavailable" : ResetText(window.ResetAt), _mutedFont, MutedColor,
            width / 2, y + 41, width - width / 2, 23,
            ContentAlignment.MiddleRight);
        return y + 72;
    }

    private ZarpaProgressBar AddProgress(int value, int y)
    {
        var progress = new ZarpaProgressBar
        {
            BackColor = SurfaceColor,
            Location = new Point(0, y),
            Size = new Size(ContentWidth, 8),
            Value = value
        };
        _progressTheme.Attach(progress);
        AddContentControl(progress);
        return progress;
    }

    private void AddSeparator(int y)
    {
        AddContentControl(new Panel
        {
            BackColor = BorderColor,
            Location = new Point(0, y),
            Size = new Size(ContentWidth, 1),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
        });
    }

    private void AddAction(string text, string iconKey, int y, EventHandler click)
    {
        var action = new ZarpaReferenceAction
        {
            Text = text,
            IconKey = iconKey,
            Location = new Point(0, y),
            Size = new Size(ContentWidth, 34),
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            AccessibleName = text,
            AccessibleRole = AccessibleRole.PushButton,
            TabStop = true
        };
        action.Click += click;
        if (_activeTheme is not null) action.ApplyTheme(_activeTheme);
        AddContentControl(action);
    }

    private void AddText(string text, Font font, Color color, int x, int y, int width, int height,
        ContentAlignment alignment = ContentAlignment.MiddleLeft)
    {
        AddContentControl(new Label
        {
            AutoEllipsis = true,
            BackColor = SurfaceColor,
            Font = font,
            ForeColor = color,
            Location = new Point(x, y),
            Size = new Size(width, height),
            Text = text,
            TextAlign = alignment,
            UseCompatibleTextRendering = false
        });
    }

    private int ContentWidth => Math.Max(240, ClientSize.Width);
    private Color SurfaceColor => _activeTheme?.Surface ?? ZarpaPopoverPalette.SurfaceTop;
    private Color BorderColor => _activeTheme?.Border ?? Color.FromArgb(164, 164, 215);
    private Color TextColor => ZarpaPopoverPaint.EnsureContrast(
        _activeTheme?.Text ?? ZarpaPopoverPalette.Text, SurfaceColor);
    private Color MutedColor => ZarpaPopoverPaint.EnsureContrast(
        _activeTheme?.TextMuted ?? ZarpaPopoverPalette.TextMuted, SurfaceColor, 3D);

    private void DisposeChildren()
    {
        while (_canvas.Controls.Count > 0) _canvas.Controls[0].Dispose();
    }

    private void ResizeCanvas()
    {
        if (_canvas.IsDisposed) return;
        _canvas.Width = Math.Max(1, ClientSize.Width);
        _scrollOffset = Math.Clamp(_scrollOffset, 0, Math.Max(0, _canvas.Height - ClientSize.Height));
        _canvas.Top = -_scrollOffset;
    }

    private void AddContentControl(Control control)
    {
        _canvas.Controls.Add(control);
        WireMouseWheel(control);
    }

    private void WireMouseWheel(Control control)
    {
        control.MouseWheel += OnCanvasMouseWheel;
        foreach (Control child in control.Controls) WireMouseWheel(child);
    }

    private void OnCanvasMouseWheel(object? sender, MouseEventArgs e)
    {
        if (e.Delta != 0) ScrollTo(_scrollOffset - Math.Sign(e.Delta) * 48);
    }

    private static string UpdatedText(DateTimeOffset updated)
    {
        var elapsed = DateTimeOffset.Now - updated.ToLocalTime();
        if (elapsed < TimeSpan.FromMinutes(1)) return "Updated just now";
        if (elapsed < TimeSpan.FromHours(1)) return $"Updated {(int)elapsed.TotalMinutes}m ago";
        if (elapsed < TimeSpan.FromDays(1)) return $"Updated {(int)elapsed.TotalHours}h ago";
        return $"Updated {(int)elapsed.TotalDays}d ago";
    }

    private static string ResetText(DateTimeOffset? resetAt)
    {
        if (resetAt is null) return string.Empty;
        var remaining = resetAt.Value - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero) return "Reset due";
        if (remaining.TotalDays >= 1) return $"Resets in {(int)remaining.TotalDays}d {remaining.Hours}h";
        if (remaining.TotalHours >= 1) return $"Resets in {(int)remaining.TotalHours}h {remaining.Minutes}m";
        return $"Resets in {Math.Max(1, remaining.Minutes)}m";
    }

    private static void OpenDashboard(string? provider) => OpenUrl(ProviderCatalog.NormalizeId(provider ?? string.Empty) switch
    {
        "claude" => "https://claude.ai/settings/usage",
        "copilot" => "https://github.com/settings/billing",
        "opencode" => "https://opencode.ai/",
        _ => "https://platform.openai.com/usage"
    });

    private static void OpenStatus(string? provider) => OpenUrl(ProviderCatalog.NormalizeId(provider ?? string.Empty) switch
    {
        "claude" => "https://status.anthropic.com/",
        "copilot" => "https://www.githubstatus.com/",
        _ => "https://status.openai.com/"
    });

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { /* The shell may be unavailable in restricted sessions. */ }
    }

    private static void ShowAbout() => MessageBox.Show(
        "Estao\nProvider usage at a glance.", "About Estao", MessageBoxButtons.OK, MessageBoxIcon.Information);
}

internal sealed class ZarpaPopoverToolRail : Control
{
    private static readonly string[] Icons =
    [
        "ic_fluent_panel_left_24_regular", "ic_fluent_archive_24_regular", "ic_fluent_cut_24_regular",
        "ic_fluent_chip_24_regular", "ic_fluent_data_bar_vertical_24_regular", "ic_fluent_drink_coffee_24_regular",
        "ic_fluent_grid_24_regular", "ic_fluent_video_clip_24_regular", "ic_fluent_record_24_regular",
        "ic_fluent_bot_24_regular", "ic_fluent_flash_24_regular"
    ];

    public ZarpaPopoverToolRail()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint, true);
        BackColor = Color.Transparent;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var dot = new SolidBrush(Color.FromArgb(234, 233, 255));
        e.Graphics.FillEllipse(dot, 17, 22, 16, 16);
        var available = Math.Max(1, Width - 56);
        var step = available / Icons.Length;
        for (var index = 0; index < Icons.Length; index++)
        {
            var x = 44 + index * step;
            if (index == 0)
                ZarpaPopoverPaint.FillRounded(e.Graphics, Color.FromArgb(38, 255, 255, 255),
                    new Rectangle(x - 4, 6, Math.Max(50, step - 4), 48), 24);
            FluentIconCatalog.TryDraw(e.Graphics, Icons[index], new Rectangle(x + 8, 16, 30, 30),
                Color.FromArgb(238, 237, 255), 27F);
        }
    }
}
