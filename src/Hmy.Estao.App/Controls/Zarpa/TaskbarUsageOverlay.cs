using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Formatting;
using Hmy.Estao.Core.Models;

namespace Hmy.Estao.App.Controls.Zarpa;

/// <summary>
/// A small, non-activating window painted over the unused part of the Windows
/// taskbar. It deliberately hides itself when the taskbar cannot accommodate
/// the configured content instead of covering task buttons or the tray.
/// </summary>
internal sealed class TaskbarUsageOverlay : Form
{
    private const int DefaultSegmentWidth = 86;
    private const int CompactOverlayHeight = 32;
    private const int StandardOverlayHeight = 38;
    private const int TaskbarReservedLeft = 120;
    private const int TaskbarGap = 6;
    private const int DragHandleWidth = 18;
    private const int ProviderHoverDelay = 350;
    private const string OverlayFontFamily = "Segoe UI";
    private const float OverlayFontSize = 8.5F;
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;
    private const int HtClient = 1;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new(-1);

    private WindowsTaskbarPalette _palette;
    private readonly System.Windows.Forms.Timer _placementTimer;
    private readonly System.Windows.Forms.Timer _hoverTimer;
    private IReadOnlyList<UsageSnapshot> _snapshots = [];
    private IReadOnlyList<UsageHistoryPoint> _history = [];
    private TaskbarOverlayConfig _config = new();
    private IReadOnlyDictionary<string, UsageColorConfig> _usageColorsByProvider =
        new Dictionary<string, UsageColorConfig>(StringComparer.OrdinalIgnoreCase);
    private Rectangle _regionBounds;
    private bool _dragging;
    private Point _dragOffset;
    private Point _dragStartLocation;
    private bool _suppressPositionNotification;
    private string? _hoverCandidate;
    private string? _reportedHoverProvider;
    private long _hoverStartedAt;

    public event Action<Point>? PositionCommitted;
    public event Action<string, Point>? ProviderHoverRequested;

    public TaskbarUsageOverlay()
    {
        _palette = WindowsTaskbarPalette.Read();
        SystemEvents.UserPreferenceChanged += OnWindowsPreferenceChanged;
        _placementTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _placementTimer.Tick += (_, _) => RefreshPlacement();
        _placementTimer.Start();
        _hoverTimer = new System.Windows.Forms.Timer { Interval = 100 };
        _hoverTimer.Tick += (_, _) => PollProviderHover();
        _hoverTimer.Start();

        AutoScaleMode = AutoScaleMode.None;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        DoubleBuffered = true;
        ClientSize = new Size(180, StandardOverlayHeight);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Text = "Estao taskbar usage";
        Cursor = Cursors.SizeAll;
    }

    protected override bool ShowWithoutActivation => true;

    public void Update(
        IReadOnlyList<UsageSnapshot> snapshots,
        IReadOnlyList<UsageHistoryPoint> history,
        TaskbarOverlayConfig config,
        IReadOnlyList<ProviderConfig> providers)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Update(snapshots, history, config, providers));
            return;
        }

        _snapshots = snapshots;
        _history = history;
        _config = CloneConfig(config);
        _usageColorsByProvider = providers
            .GroupBy(provider => ProviderCatalog.NormalizeId(provider.Id), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().UsageColors, StringComparer.OrdinalIgnoreCase);
        RefreshPlacement();
        Invalidate();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= OnWindowsPreferenceChanged;
            _placementTimer.Stop();
            _placementTimer.Dispose();
            _hoverTimer.Stop();
            _hoverTimer.Dispose();
        }
        base.Dispose(disposing);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0 || _regionBounds.Size == ClientSize) return;
        _regionBounds = new Rectangle(Point.Empty, ClientSize);
        using var path = ZarpaPopoverPaint.RoundedPath(_regionBounds, 11);
        var previousRegion = Region;
        Region = new Region(path);
        previousRegion?.Dispose();
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        if (!_dragging || _suppressPositionNotification || WindowState != FormWindowState.Normal) return;

        _config.PositionX = Left;
        _config.PositionY = Top;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !_config.MoveEnabled || !DragHandleBounds.Contains(e.Location)) return;

        _dragging = true;
        _dragOffset = e.Location;
        _dragStartLocation = Location;
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        if ((Control.MouseButtons & MouseButtons.Left) == 0)
        {
            StopDragging();
            return;
        }

        var cursor = Cursor.Position;
        var desired = new Rectangle(cursor.X - _dragOffset.X, cursor.Y - _dragOffset.Y, Width, Height);
        Location = ConstrainToScreen(desired).Location;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left) StopDragging();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture && _dragging) StopDragging();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyWindowsCorners();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.HighQuality;
        e.Graphics.CompositingQuality = CompositingQuality.HighQuality;
        e.Graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var theme = _palette;
        var card = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        ZarpaPopoverPaint.FillRounded(e.Graphics, theme.Surface, card, 8);
        using (var outline = new Pen(_dragging ? theme.Accent : theme.Border, _dragging ? 2F : 1F))
        using (var path = ZarpaPopoverPaint.RoundedPath(card, 8))
            e.Graphics.DrawPath(outline, path);

        DrawDragHandle(e.Graphics, theme);

        var providers = VisibleSnapshots();
        var segmentWidth = SegmentWidth();
        for (var index = 0; index < providers.Count; index++)
        {
            var bounds = new Rectangle(DragHandleWidth + 7 + index * segmentWidth, 0, segmentWidth - 4, Height);
            DrawProvider(e.Graphics, providers[index], bounds, theme);
            if (index < providers.Count - 1)
            {
                using var separator = new Pen(Color.FromArgb(110, theme.Border), 1F);
                e.Graphics.DrawLine(separator, bounds.Right + 1, 10, bounds.Right + 1, Height - 10);
            }
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x00000080 | 0x08000000; // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
            return parameters;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmNcHitTest)
        {
            var packedPoint = m.LParam.ToInt64();
            var screenPoint = new Point(unchecked((short)(packedPoint & 0xffff)),
                unchecked((short)((packedPoint >> 16) & 0xffff)));
            var clientPoint = PointToClient(screenPoint);
            m.Result = (IntPtr)(_config.MoveEnabled && DragHandleBounds.Contains(clientPoint) ? HtClient : HtTransparent);
            return;
        }

        base.WndProc(ref m);
    }

    private void DrawProvider(Graphics graphics, UsageSnapshot snapshot, Rectangle bounds, WindowsTaskbarPalette theme)
    {
        var window = snapshot.Windows.FirstOrDefault();
        var used = window?.PercentUsed is double value ? Math.Clamp(value, 0D, 1D) : 0D;
        var provider = ProviderCatalog.NormalizeId(snapshot.Provider);
        var indicatorColor = ZarpaUsageColorResolver.OverrideFor(
            _usageColorsByProvider.GetValueOrDefault(provider), window?.PercentUsed);
        if (indicatorColor.IsEmpty) indicatorColor = theme.Accent;
        var showIcon = !string.Equals(_config.DisplayMode, "title", StringComparison.OrdinalIgnoreCase);
        var showTitle = !string.Equals(_config.DisplayMode, "icon", StringComparison.OrdinalIgnoreCase);
        var moduleWidth = ModuleWidth();
        var rowY = bounds.Top + Math.Max(0, (Height - 20) / 2);
        var moduleY = bounds.Top + Math.Max(0, (Height - 14) / 2);
        var moduleX = bounds.Left + 6;

        if (showIcon)
        {
            ZarpaProviderIconCatalog.TryDraw(graphics, provider,
                new Rectangle(moduleX, rowY, 20, 20), theme.Text);
            moduleX += 26;
        }
        if (showTitle)
        {
            DrawText(graphics, snapshot.DisplayName, new Font(OverlayFontFamily, OverlayFontSize, FontStyle.Bold),
                new RectangleF(moduleX, rowY + 2, showIcon ? 72 : 76, 16), theme.Text);
            moduleX += (showIcon ? 78 : 82);
        }

        if (HasControl("percentage"))
        {
            DrawText(graphics, window?.PercentUsed is double ? $"{used:P0}" : "—",
                new Font(OverlayFontFamily, OverlayFontSize), new RectangleF(moduleX, moduleY, 30, 14), theme.TextMuted);
            moduleX += 34;
        }

        if (HasControl("bar"))
        {
            var bar = new Rectangle(moduleX, moduleY + 5, 42, 4);
            ZarpaPopoverPaint.FillRounded(graphics, Color.FromArgb(70, theme.TextMuted), bar, 3);
            ZarpaPopoverPaint.FillRounded(graphics, indicatorColor,
                new Rectangle(bar.Left, bar.Top, Math.Max(2, (int)Math.Round(bar.Width * used)), bar.Height), 3);
            moduleX += 46;
        }

        if (HasControl("pie"))
        {
            DrawDonut(graphics, used, indicatorColor, new Rectangle(moduleX, moduleY - 1, 18, 18));
            moduleX += 24;
        }
        if (HasControl("chart"))
        {
            DrawSparkline(graphics, snapshot, theme.Accent, new Rectangle(moduleX, moduleY + 3, 48, 10), theme);
            moduleX += 52;
        }
        if (HasControl("usedTotal") && window?.Used is double actual && window.Limit is double limit)
        {
            DrawText(graphics, $"{FormatValue(actual)} / {FormatValue(limit)} {window.Unit}".Trim(),
                new Font(OverlayFontFamily, OverlayFontSize), new RectangleF(moduleX, moduleY, 64, 14), theme.TextMuted);
            moduleX += 68;
        }
        if (HasControl("reset") && window?.ResetAt is DateTimeOffset resetAt)
        {
            DrawText(graphics, ResetText(resetAt), new Font(OverlayFontFamily, OverlayFontSize),
                new RectangleF(moduleX, moduleY, 54, 14), theme.TextMuted, StringAlignment.Near);
        }
    }

    private void DrawDragHandle(Graphics graphics, WindowsTaskbarPalette theme)
    {
        using var separator = new Pen(Color.FromArgb(90, theme.Border), 1F);
        graphics.DrawLine(separator, DragHandleWidth, 9, DragHandleWidth, Height - 9);

        var dotColor = _dragging
            ? theme.Accent
            : _config.MoveEnabled
                ? theme.TextMuted
                : Color.FromArgb(80, theme.TextMuted);
        using var dot = new SolidBrush(dotColor);
        const int dotSize = 2;
        const int gap = 3;
        const int rows = 4;
        var groupWidth = dotSize * 2 + gap;
        var groupHeight = dotSize * rows + gap * (rows - 1);
        var left = (DragHandleWidth - groupWidth) / 2;
        var top = (Height - groupHeight) / 2;
        for (var row = 0; row < rows; row++)
        for (var column = 0; column < 2; column++)
            graphics.FillEllipse(dot, left + column * (dotSize + gap), top + row * (dotSize + gap), dotSize, dotSize);
    }

    private void DrawSparkline(Graphics graphics, UsageSnapshot snapshot, Color color, Rectangle bounds, WindowsTaskbarPalette theme)
    {
        var window = snapshot.Windows.FirstOrDefault();
        if (window is null) return;

        var now = DateTimeOffset.UtcNow;
        var displayRange = UsageWindowCatalog.DisplayRange(snapshot.Provider, window.Id, window.Title);
        var rangeStart = now - displayRange;
        var points = _history.Where(point =>
                string.Equals(ProviderCatalog.NormalizeId(point.Provider), ProviderCatalog.NormalizeId(snapshot.Provider), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(point.Window, window.Id, StringComparison.OrdinalIgnoreCase) &&
                point.Timestamp >= rangeStart && point.Timestamp <= now)
            .OrderBy(point => point.Timestamp)
            .TakeLast(18)
            .ToArray();
        if (points.Length < 2) return;

        var state = graphics.Save();
        graphics.SmoothingMode = SmoothingMode.HighQuality;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        using var track = new Pen(Color.FromArgb(80, theme.TextMuted), 1F);
        graphics.DrawLine(track, bounds.Left, bounds.Bottom, bounds.Right, bounds.Bottom);
        using var line = new Pen(color, 1.4F);
        var mapped = points.Select(point => new PointF(
            bounds.Left + (float)Math.Clamp(
                (point.Timestamp - rangeStart).TotalSeconds / displayRange.TotalSeconds, 0D, 1D) * (bounds.Width - 1F),
            bounds.Bottom - (float)Math.Clamp(point.PercentUsed, 0D, 1D) * bounds.Height)).ToArray();
        graphics.DrawLines(line, mapped);
        graphics.Restore(state);
    }

    private static void DrawDonut(Graphics graphics, double used, Color color, Rectangle bounds)
    {
        using var track = new Pen(Color.FromArgb(75, color), 3F);
        using var value = new Pen(color, 3F);
        graphics.DrawArc(track, bounds, 0, 360);
        graphics.DrawArc(value, bounds, -90, (float)(360D * used));
    }

    private static void DrawText(Graphics graphics, string text, Font font, RectangleF bounds, Color color,
        StringAlignment alignment = StringAlignment.Near)
    {
        using (font)
        using (var brush = new SolidBrush(color))
        using (var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = alignment,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = StringTrimming.EllipsisCharacter
        })
        {
            graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            graphics.DrawString(text, font, brush, bounds, format);
        }
    }

    private bool HasControl(string id) => _config.Controls.Any(control =>
        string.Equals(control, id, StringComparison.OrdinalIgnoreCase));

    private IReadOnlyList<UsageSnapshot> VisibleSnapshots()
    {
        var configured = _config.ProviderIds;
        var selected = configured.Count == 0
            ? _snapshots.Where(snapshot => snapshot.Error is null && snapshot.Windows.Count > 0)
            : _snapshots.Where(snapshot => configured.Contains(ProviderCatalog.NormalizeId(snapshot.Provider), StringComparer.OrdinalIgnoreCase));
        var snapshots = selected.Take(4).ToArray();
        if (snapshots.Length > 0 || configured.Count == 0) return snapshots;

        // Keep the configured rail visible while the first refresh is pending.
        // This makes the setting discoverable even when credentials are not
        // available yet; the same bar will fill as soon as data arrives.
        return configured.Take(4).Select(provider => new UsageSnapshot(
            provider, ProviderCatalog.DisplayName(provider), "waiting", DateTimeOffset.UtcNow, [])).ToArray();
    }

    private void PollProviderHover()
    {
        if (!Visible || _dragging)
        {
            ResetProviderHover();
            return;
        }

        var cursor = Cursor.Position;
        var clientPoint = PointToClient(cursor);
        var providers = VisibleSnapshots();
        var segmentWidth = SegmentWidth();
        var firstSegmentLeft = DragHandleWidth + 7;
        var index = (clientPoint.X - firstSegmentLeft) / Math.Max(1, segmentWidth);
        var insideSegments = clientPoint.Y >= 0 && clientPoint.Y < Height &&
            clientPoint.X >= firstSegmentLeft && index >= 0 && index < providers.Count;
        var provider = insideSegments ? ProviderCatalog.NormalizeId(providers[index].Provider) : null;
        if (provider is null)
        {
            ResetProviderHover();
            return;
        }

        if (!string.Equals(provider, _hoverCandidate, StringComparison.OrdinalIgnoreCase))
        {
            _hoverCandidate = provider;
            _reportedHoverProvider = null;
            _hoverStartedAt = Environment.TickCount64;
            return;
        }

        if (_reportedHoverProvider is not null || Environment.TickCount64 - _hoverStartedAt < ProviderHoverDelay) return;

        _reportedHoverProvider = provider;
        var segmentLeft = firstSegmentLeft + index * segmentWidth;
        var anchor = PointToScreen(new Point(segmentLeft + segmentWidth / 2, 0));
        ProviderHoverRequested?.Invoke(provider, anchor);
    }

    private void ResetProviderHover()
    {
        _hoverCandidate = null;
        _reportedHoverProvider = null;
        _hoverStartedAt = 0;
    }

    private int SegmentWidth()
    {
        var width = _config.DisplayMode switch
        {
            "icon" => 32,
            "title" => 96,
            _ => 118
        };
        width += ModuleWidth();
        if (string.Equals(_config.Size, "spacious", StringComparison.OrdinalIgnoreCase)) width += 10;
        return Math.Max(DefaultSegmentWidth, width);
    }

    private int ModuleWidth()
    {
        var width = 0;
        if (HasControl("percentage")) width += 34;
        if (HasControl("bar")) width += 46;
        if (HasControl("pie")) width += 24;
        if (HasControl("chart")) width += 52;
        if (HasControl("usedTotal")) width += 68;
        if (HasControl("reset")) width += 47;
        return width;
    }

    private int OverlayHeight()
    {
        if (string.Equals(_config.Size, "spacious", StringComparison.OrdinalIgnoreCase)) return 46;
        if (string.Equals(_config.Size, "compact", StringComparison.OrdinalIgnoreCase))
            return string.Equals(_config.DisplayMode, "icon", StringComparison.OrdinalIgnoreCase) ? 30 : 34;
        return StandardOverlayHeight;
    }

    private bool TryPlace(int providerCount, out Rectangle placement)
    {
        placement = Rectangle.Empty;
        var size = new Size(DragHandleWidth + 7 + providerCount * SegmentWidth(), OverlayHeight());
        ClientSize = size;
        if (_config.PositionX is int x && _config.PositionY is int y)
        {
            placement = ConstrainToScreen(new Rectangle(new Point(x, y), size));
            return true;
        }

        var taskbar = FindWindow("Shell_TrayWnd", null);
        NativeRect taskbarRect = default;
        var hasNativeTaskbar = taskbar != IntPtr.Zero && GetWindowRect(taskbar, out taskbarRect);
        if (!hasNativeTaskbar)
        {
            var screen = Screen.PrimaryScreen;
            if (screen is null) return false;
            var taskbarTop = screen.WorkingArea.Bottom;
            taskbarRect = new NativeRect(screen.WorkingArea.Left, taskbarTop, screen.WorkingArea.Right, screen.Bounds.Bottom);
        }
        if (taskbarRect.Height >= taskbarRect.Width || taskbarRect.Height <= 0) return false;

        var tray = taskbar == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        if (tray == IntPtr.Zero) tray = FindWindow("TrayNotifyWnd", null);
        var right = taskbarRect.Right - 12;
        if (tray != IntPtr.Zero && GetWindowRect(tray, out var trayRect)) right = trayRect.Left - TaskbarGap;

        var width = size.Width;
        var left = right - width;
        if (left < taskbarRect.Left + TaskbarReservedLeft || width > taskbarRect.Width - TaskbarReservedLeft)
        {
            // TrayNotifyWnd can report a stale/whole-taskbar rectangle on some
            // Windows 11 builds. In that case use the taskbar's right edge;
            // the compact pill still fits without covering the clock area.
            right = taskbarRect.Right - 10;
            left = right - width;
            if (left < taskbarRect.Left + 12 || width > taskbarRect.Width - 24) return false;
        }

        var height = size.Height;
        placement = new Rectangle(left, taskbarRect.Top + Math.Max(0, (taskbarRect.Height - height) / 2),
            width, height);
        return true;
    }

    private static Rectangle ConstrainToScreen(Rectangle placement)
    {
        var center = new Point(placement.Left + placement.Width / 2, placement.Top + placement.Height / 2);
        var bounds = Screen.FromPoint(center).Bounds;
        var maxX = Math.Max(bounds.Left, bounds.Right - placement.Width);
        var maxY = Math.Max(bounds.Top, bounds.Bottom - placement.Height);
        return new Rectangle(
            Math.Clamp(placement.Left, bounds.Left, maxX),
            Math.Clamp(placement.Top, bounds.Top, maxY),
            placement.Width,
            placement.Height);
    }

    private void StopDragging()
    {
        var commitPosition = _dragging && Location != _dragStartLocation;
        _dragging = false;
        if (Capture) Capture = false;
        Invalidate();
        if (commitPosition) PositionCommitted?.Invoke(Location);
    }

    private Rectangle DragHandleBounds => new(0, 0, DragHandleWidth, Height);

    private static TaskbarOverlayConfig CloneConfig(TaskbarOverlayConfig value) => new()
    {
        Enabled = value.Enabled,
        ProviderIds = value.ProviderIds?.ToList() ?? [],
        Controls = value.Controls?.ToList() ?? TaskbarOverlayControlCatalog.Default.ToList(),
        DisplayMode = value.DisplayMode,
        Size = value.Size,
        MoveEnabled = value.MoveEnabled,
        PositionX = value.PositionX,
        PositionY = value.PositionY
    };

    private static string FormatValue(double value) => value >= 1000D ? value.ToString("0.#") : value.ToString("0.##");

    private static string ResetText(DateTimeOffset resetAt)
    {
        var remaining = resetAt - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero) return "Reset due";
        return DurationFormatter.ToCompact(remaining);
    }

    private void ApplyWindowsCorners()
    {
        try
        {
            var preference = 2; // DWMWCP_ROUND; Windows 10 simply ignores it.
            DwmSetWindowAttribute(Handle, 33, ref preference, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    private void OnWindowsPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (IsDisposed) return;
        _palette = WindowsTaskbarPalette.Read();
        BeginInvoke(Invalidate);
    }

    private void RefreshPlacement()
    {
        if (IsDisposed) return;
        var visible = VisibleSnapshots();
        if (!_config.Enabled || visible.Count == 0 || !TryPlace(visible.Count, out var placement))
        {
            Hide();
            return;
        }

        _suppressPositionNotification = true;
        try
        {
            Bounds = placement;
        }
        finally
        {
            _suppressPositionNotification = false;
        }
        if (!Visible) Show();
        if (IsHandleCreated)
            SetWindowPos(Handle, HwndTopmost, Left, Top, Width, Height, SwpNoActivate | SwpShowWindow);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string? className, string? windowTitle);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int valueSize);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRect
    {
        public NativeRect(int left, int top, int right, int bottom)
        {
            Left = left;
            Top = top;
            Right = right;
            Bottom = bottom;
        }

        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }
}

/// <summary>
/// The taskbar widget follows Windows personalization rather than the
/// application's Zarpa theme. Registry values are the same values used by
/// Windows for the light/dark app preference; SystemColors keep high contrast
/// and accessibility settings respected too.
/// </summary>
internal readonly record struct WindowsTaskbarPalette(
    Color Surface,
    Color Border,
    Color Text,
    Color TextMuted,
    Color Accent,
    Color Warning,
    Color Danger)
{
    public static WindowsTaskbarPalette Read()
    {
        var light = true;
        using (var key = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Themes\\Personalize"))
        {
            if (key?.GetValue("AppsUseLightTheme") is int value) light = value != 0;
        }

        var accent = SystemColors.Highlight;
        if (accent == Color.Black || accent == Color.White)
            accent = light ? Color.FromArgb(0, 103, 192) : Color.FromArgb(96, 165, 250);

        return light
            ? new WindowsTaskbarPalette(
                Color.FromArgb(248, 248, 248), Color.FromArgb(210, 210, 210),
                SystemColors.ControlText, SystemColors.GrayText, accent,
                Color.FromArgb(180, 95, 0), Color.FromArgb(196, 43, 28))
            : new WindowsTaskbarPalette(
                Color.FromArgb(32, 32, 32), Color.FromArgb(72, 72, 72),
                Color.FromArgb(245, 245, 245), Color.FromArgb(185, 185, 185), accent,
                Color.FromArgb(255, 185, 80), Color.FromArgb(255, 110, 100));
    }
}
