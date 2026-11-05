using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Models;
using Microsoft.Win32;
using Svg;

namespace Hmy.Estao.Desktop;

// The original taskbar pill, with its fixed native layout and Windows palette.
// No Zarpa controls or WebView are created by this window.
internal sealed class ProviderOverlay : Form
{
    private const int GripWidth = 18;
    private const int OuterPadding = 7;
    private const int ProviderPadding = 6;
    private const int Gap = 6;
    private const int ProviderGap = 10;
    private readonly Font _regular = new("Segoe UI", 8.5F);
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 100 };
    private readonly Dictionary<string, Bitmap> _icons = [];
    private WindowsTaskbarPalette _palette = WindowsTaskbarPalette.Read();
    private List<ProviderLayout> _layouts = [];
    private EstaoConfig? _config;
    private IReadOnlyList<UsageSnapshot> _snapshots = [];
    private bool _dragging;
    private Point _dragOffset;
    private Point _dragStart;
    private string? _hovered;
    private string? _reportedHover;
    private long _hoverStarted;
    private long _lastPlacement;
    private long _lastLayout;

    public event Action<string, Point>? ProviderRequested;
    public event Action<string, Point>? ProviderHoverRequested;
    public event Action<Point>? PositionCommitted;

    public ProviderOverlay()
    {
        AutoScaleMode = AutoScaleMode.None;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        ShowIcon = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        ClientSize = new Size(180, 38);
        Text = "Estao taskbar usage";
        AccessibleName = Text;
        ApplyPalette();
        SystemEvents.UserPreferenceChanged += WindowsPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += DisplaySettingsChanged;
        _timer.Tick += (_, _) => TickOverlay();
        _timer.Start();
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            var value = base.CreateParams;
            value.ExStyle |= 0x00000080 | 0x08000000; // Tool window, never activate on a click.
            return value;
        }
    }

    public void UpdateUsage(EstaoConfig config, IReadOnlyList<UsageSnapshot> snapshots)
    {
        _config = config;
        _snapshots = snapshots;
        RebuildLayouts();
        RefreshPlacement();
        Invalidate();
    }

    private void RebuildLayouts()
    {
        if (_config is null) return;
        using var graphics = CreateGraphics();
        using var format = MeasureFormat();
        _layouts = _config.Providers.Where(p => p.Enabled == true && ProviderCatalog.IsSupported(p.Id)
            && (_config.TaskbarOverlay.ProviderIds.Count == 0 || _config.TaskbarOverlay.ProviderIds.Contains(p.Id)))
            .Select(p =>
            {
                var snapshot = _snapshots.FirstOrDefault(s => s.Provider == p.Id);
                var window = snapshot?.Windows.FirstOrDefault();
                var title = ProviderCatalog.DisplayName(p.Id);
                var percentage = window?.PercentUsed is double value ? $"{Math.Clamp(value, 0, 1):P0}" : "—";
                var reset = UsagePresentation.Reset(window?.ResetAt);
                var percentageWidth = Measure(percentage, _regular);
                var resetWidth = Measure(reset, _regular);
                // Logo-only identity, followed by the three fixed usage indicators.
                // Keep the provider name in accessibility text, not in the painted rail.
                var width = ProviderPadding * 2 + 20 + percentageWidth + 18 + resetWidth + Gap * 3;
                return new ProviderLayout(p.Id, title, percentage, reset, window?.PercentUsed,
                    percentageWidth, resetWidth, width);
            }).ToList();
        _lastLayout = Environment.TickCount64;
        AccessibleDescription = string.Join("; ", _layouts.Select(p => $"{p.Title}: {p.Percentage} used, {p.Reset}"));
        return;

        int Measure(string text, Font font) => Math.Max(1, (int)Math.Ceiling(graphics.MeasureString(text, font, int.MaxValue, format).Width));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        var card = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        using (var path = RoundedPath(card, 8))
        using (var brush = new SolidBrush(_palette.Surface))
        using (var border = new Pen(_dragging ? _palette.Accent : _palette.Border, _dragging ? 2 : 1))
        {
            g.FillPath(brush, path);
            g.DrawPath(border, path);
        }
        DrawGrip(g);
        var left = GripWidth + OuterPadding;
        for (var i = 0; i < _layouts.Count; i++)
        {
            var p = _layouts[i];
            var x = left + ProviderPadding;
            var icon = ProviderIcon(p.Id);
            if (icon is not null) g.DrawImage(icon, x, (Height - 20) / 2, 20, 20);
            x += 20 + Gap;
            DrawText(p.Percentage, _regular, p.PercentageWidth, _palette.TextMuted);
            var custom = p.Used is double used ? UsageColorCatalog.ColorFor(_config?.UsageAlerts, used) : null;
            var color = custom is null ? _palette.Accent : ColorTranslator.FromHtml(custom);
            var donut = new Rectangle(x, (Height - 14) / 2 - 1, 18, 18);
            using (var track = new Pen(Color.FromArgb(75, color), 3)) g.DrawArc(track, donut, 0, 360);
            if (p.Used is double fraction)
                using (var meter = new Pen(color, 3)) g.DrawArc(meter, donut, -90, (float)Math.Clamp(fraction, 0, 1) * 360);
            x += 18 + Gap;
            DrawText(p.Reset, _regular, p.ResetWidth, _palette.TextMuted);
            left += p.Width;
            if (i < _layouts.Count - 1)
            {
                using var separator = new Pen(Color.FromArgb(110, _palette.Border));
                g.DrawLine(separator, left + ProviderGap / 2, 10, left + ProviderGap / 2, Height - 10);
                left += ProviderGap;
            }

            void DrawText(string text, Font font, int width, Color color)
            {
                using var brush = new SolidBrush(color);
                using var format = new StringFormat(StringFormat.GenericTypographic)
                {
                    LineAlignment = StringAlignment.Center,
                    FormatFlags = StringFormatFlags.NoWrap,
                    Trimming = StringTrimming.EllipsisCharacter
                };
                g.DrawString(text, font, brush, new RectangleF(x, (Height - 14) / 2, width, 14), format);
                x += width + Gap;
            }
        }
    }

    private void DrawGrip(Graphics g)
    {
        using var separator = new Pen(Color.FromArgb(90, _palette.Border));
        g.DrawLine(separator, GripWidth, 9, GripWidth, Height - 9);
        using var brush = new SolidBrush(_dragging ? _palette.Accent : _palette.TextMuted);
        for (var row = 0; row < 4; row++)
        for (var column = 0; column < 2; column++)
            g.FillEllipse(brush, (GripWidth - 7) / 2 + column * 5, (Height - 17) / 2 + row * 5, 2, 2);
    }

    private Bitmap? ProviderIcon(string id)
    {
        if (_icons.TryGetValue(id, out var cached)) return cached;
        using var stream = typeof(ProviderOverlay).Assembly.GetManifestResourceStream($"Hmy.Estao.ProviderIcons.{id}.svg");
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        var tint = $"#{_palette.Text.R:X2}{_palette.Text.G:X2}{_palette.Text.B:X2}";
        var source = reader.ReadToEnd().Replace("#1F2328", tint, StringComparison.OrdinalIgnoreCase)
            .Replace("#211E1E", tint, StringComparison.OrdinalIgnoreCase);
        var bitmap = SvgDocument.FromSvg<SvgDocument>(source).Draw(20, 20);
        if (bitmap is not null) _icons[id] = bitmap;
        return bitmap;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        using var path = RoundedPath(new Rectangle(Point.Empty, ClientSize), 11);
        var previous = Region;
        Region = new Region(path);
        previous?.Dispose();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || e.X >= GripWidth) return;
        _dragging = true;
        _dragOffset = e.Location;
        _dragStart = Location;
        Capture = true;
        Invalidate(new Rectangle(0, 0, GripWidth + 1, Height));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = e.X < GripWidth ? Cursors.SizeAll : Cursors.Hand;
        if (!_dragging) return;
        Location = Constrain(new Rectangle(Cursor.Position.X - _dragOffset.X, Cursor.Position.Y - _dragOffset.Y, Width, Height)).Location;
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        if (_dragging) { StopDragging(); return; }
        var hit = ProviderAt(e.Location);
        if (hit.Layout is not null) ProviderRequested?.Invoke(hit.Layout.Id, PointToScreen(new Point(hit.Left, 0)));
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (_dragging && !Capture) StopDragging();
    }

    private void StopDragging()
    {
        _dragging = false;
        Capture = false;
        if (Location != _dragStart && _config is not null)
        {
            _config.TaskbarOverlay.PositionX = Left;
            _config.TaskbarOverlay.PositionY = Top;
            PositionCommitted?.Invoke(Location);
        }
        Invalidate();
    }

    private void TickOverlay()
    {
        if (_config is null || IsDisposed) return;
        var now = Environment.TickCount64;
        if (!_dragging && now - _lastPlacement >= 1000)
        {
            if (now - _lastLayout >= 30_000) { RebuildLayouts(); Invalidate(); }
            RefreshPlacement();
            _lastPlacement = now;
        }
        var hit = Visible && !_dragging ? ProviderAt(PointToClient(Cursor.Position)) : default;
        if (hit.Layout?.Id != _hovered)
        {
            _hovered = hit.Layout?.Id;
            _reportedHover = null;
            _hoverStarted = now;
        }
        if (_hovered is not null && _reportedHover is null && now - _hoverStarted >= 350)
        {
            _reportedHover = _hovered;
            ProviderHoverRequested?.Invoke(_hovered, PointToScreen(new Point(hit.Left + hit.Layout!.Width / 2, 0)));
        }
    }

    private (ProviderLayout? Layout, int Left) ProviderAt(Point point)
    {
        var left = GripWidth + OuterPadding;
        foreach (var p in _layouts)
        {
            if (new Rectangle(left, 0, p.Width, Height).Contains(point)) return (p, left);
            left += p.Width + ProviderGap;
        }
        return default;
    }

    private void RefreshPlacement()
    {
        if (_config is null || _dragging) return;
        if (!_config.TaskbarOverlay.Enabled || _layouts.Count == 0) { Hide(); return; }
        var size = new Size(GripWidth + OuterPadding * 2 + _layouts.Sum(p => p.Width) + ProviderGap * (_layouts.Count - 1), 38);
        Rectangle placement;
        if (_config.TaskbarOverlay.PositionX is int x && _config.TaskbarOverlay.PositionY is int y)
            placement = Constrain(new Rectangle(new Point(x, y), size));
        else
        {
            var taskbar = FindWindow("Shell_TrayWnd", null);
            if (taskbar == IntPtr.Zero || !GetWindowRect(taskbar, out var bar) || bar.Height >= bar.Width || bar.Height <= 0)
            { Hide(); return; }
            var tray = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            var right = bar.Right - 12;
            if (tray != IntPtr.Zero && GetWindowRect(tray, out var trayRect)) right = trayRect.Left - 6;
            var left = right - size.Width;
            if (left < bar.Left + 120)
            {
                right = bar.Right - 10;
                left = right - size.Width;
                if (left < bar.Left + 12 || size.Width > bar.Width - 24) { Hide(); return; }
            }
            placement = new Rectangle(left, bar.Top + Math.Max(0, (bar.Height - size.Height) / 2), size.Width, size.Height);
        }
        if (Bounds != placement) Bounds = placement;
        if (!Visible) Show();
        if (IsHandleCreated) SetWindowPos(Handle, new IntPtr(-1), Left, Top, Width, Height, 0x0010 | 0x0040);
    }

    private static Rectangle Constrain(Rectangle placement)
    {
        var screen = Screen.FromPoint(new Point(placement.Left + placement.Width / 2, placement.Top + placement.Height / 2)).Bounds;
        return new Rectangle(Math.Clamp(placement.X, screen.Left, Math.Max(screen.Left, screen.Right - placement.Width)),
            Math.Clamp(placement.Y, screen.Top, Math.Max(screen.Top, screen.Bottom - placement.Height)), placement.Width, placement.Height);
    }

    private void WindowsPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e) => QueuePaletteRefresh();
    private void DisplaySettingsChanged(object? sender, EventArgs e) => QueuePaletteRefresh();
    private void QueuePaletteRefresh()
    {
        if (IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(() => { if (!IsDisposed) { ApplyPalette(); RebuildLayouts(); RefreshPlacement(); Invalidate(); } }); }
        catch (InvalidOperationException) { /* The native window is closing. */ }
    }

    private void ApplyPalette()
    {
        _palette = WindowsTaskbarPalette.Read();
        BackColor = _palette.Surface;
        ForeColor = _palette.Text;
        foreach (var bitmap in _icons.Values) bitmap.Dispose();
        _icons.Clear();
    }

    private static StringFormat MeasureFormat() => new(StringFormat.GenericTypographic)
    { FormatFlags = StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap };

    private static GraphicsPath RoundedPath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(Math.Min(bounds.Width, bounds.Height), Math.Max(1, radius * 2));
        var arc = new Rectangle(bounds.Left, bounds.Top, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter; path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter; path.AddArc(arc, 0, 90);
        arc.X = bounds.Left; path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= WindowsPreferenceChanged;
            SystemEvents.DisplaySettingsChanged -= DisplaySettingsChanged;
            _timer.Dispose();
            _regular.Dispose();
            foreach (var bitmap in _icons.Values) bitmap.Dispose();
            _icons.Clear();
        }
        base.Dispose(disposing);
    }

    private sealed record ProviderLayout(string Id, string Title, string Percentage, string Reset, double? Used,
        int PercentageWidth, int ResetWidth, int Width);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? windowName);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rect);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
        public readonly int Width => Right - Left;
        public readonly int Height => Bottom - Top;
    }
}
