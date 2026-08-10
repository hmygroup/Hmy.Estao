using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Models;
using ZarpaSuite.Controls;

namespace Hmy.Estao.App.Controls.Zarpa;

/// <summary>
/// A small, non-activating window painted over the unused part of the Windows
/// taskbar. It deliberately hides itself when the taskbar cannot accommodate
/// the configured content instead of covering task buttons or the tray.
/// </summary>
internal sealed class TaskbarUsageOverlay : Form
{
    private const int DefaultSegmentWidth = 138;
    private const int OverlayHeight = 54;
    private const int TaskbarReservedLeft = 180;
    private const int TaskbarGap = 6;
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;

    private readonly ZarpaThemeManager _theme;
    private IReadOnlyList<UsageSnapshot> _snapshots = [];
    private IReadOnlyList<UsageHistoryPoint> _history = [];
    private TaskbarOverlayConfig _config = new();
    private Rectangle _regionBounds;

    public TaskbarUsageOverlay(ZarpaThemePreset themePreset)
    {
        _theme = new ZarpaThemeManager { Preset = themePreset };
        _theme.ThemeChanged += (_, _) => Invalidate();

        AutoScaleMode = AutoScaleMode.None;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        DoubleBuffered = true;
        ClientSize = new Size(420, OverlayHeight);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Text = "Estao taskbar usage";
    }

    protected override bool ShowWithoutActivation => true;

    public void SetTheme(ZarpaThemePreset preset) => _theme.Preset = preset;

    public void Update(
        IReadOnlyList<UsageSnapshot> snapshots,
        IReadOnlyList<UsageHistoryPoint> history,
        TaskbarOverlayConfig config)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => Update(snapshots, history, config));
            return;
        }

        _snapshots = snapshots;
        _history = history;
        _config = CloneConfig(config);
        var visible = VisibleSnapshots();
        if (!_config.Enabled || visible.Count == 0 || !TryPlace(visible.Count, out var placement))
        {
            Hide();
            return;
        }

        Bounds = placement;
        Invalidate();
        if (!Visible) Show();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _theme.Dispose();
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

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var theme = _theme.Theme;
        var card = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        ZarpaPopoverPaint.FillRounded(e.Graphics, theme.Surface, card, 11);
        using (var outline = new Pen(theme.Border, 1F))
        using (var path = ZarpaPopoverPaint.RoundedPath(card, 11))
            e.Graphics.DrawPath(outline, path);

        var providers = VisibleSnapshots();
        var segmentWidth = SegmentWidth();
        for (var index = 0; index < providers.Count; index++)
        {
            var bounds = new Rectangle(7 + index * segmentWidth, 0, segmentWidth - 4, Height);
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
            m.Result = (IntPtr)HtTransparent;
            return;
        }

        base.WndProc(ref m);
    }

    private void DrawProvider(Graphics graphics, UsageSnapshot snapshot, Rectangle bounds, ZarpaThemeTokens theme)
    {
        var window = snapshot.Windows.FirstOrDefault();
        var used = window?.PercentUsed is double value ? Math.Clamp(value, 0D, 1D) : 0D;
        var accent = used >= .9D ? theme.Danger : used >= .75D ? theme.Warning : theme.Accent;
        var provider = ProviderCatalog.NormalizeId(snapshot.Provider);

        ZarpaProviderIconCatalog.TryDraw(graphics, provider,
            new Rectangle(bounds.Left + 6, bounds.Top + 7, 22, 22), theme.Text);
        DrawText(graphics, snapshot.DisplayName, new Font("Segoe UI", 7.5F, FontStyle.Bold),
            new RectangleF(bounds.Left + 34, bounds.Top + 4, bounds.Width - 62, 17), theme.Text);

        var x = bounds.Left + 34;
        if (HasControl("percentage"))
        {
            DrawText(graphics, window?.PercentUsed is double ? $"{used:P0} used" : "Usage n/a",
                new Font("Segoe UI", 7.5F), new RectangleF(x, bounds.Top + 20, 60, 16), theme.TextMuted);
        }

        if (HasControl("bar"))
        {
            var bar = new Rectangle(x, bounds.Top + 39, Math.Min(60, bounds.Width - 70), 6);
            ZarpaPopoverPaint.FillRounded(graphics, Color.FromArgb(70, theme.TextMuted), bar, 3);
            ZarpaPopoverPaint.FillRounded(graphics, accent,
                new Rectangle(bar.Left, bar.Top, Math.Max(2, (int)Math.Round(bar.Width * used)), bar.Height), 3);
        }

        if (HasControl("pie")) DrawDonut(graphics, used, accent, new Rectangle(bounds.Right - 32, bounds.Top + 7, 24, 24));
        if (HasControl("chart")) DrawSparkline(graphics, snapshot, accent,
            new Rectangle(bounds.Left + 68, bounds.Top + 35, Math.Max(24, bounds.Width - 104), 12), theme);
        if (HasControl("usedTotal") && window?.Used is double actual && window.Limit is double limit)
        {
            DrawText(graphics, $"{FormatValue(actual)} / {FormatValue(limit)} {window.Unit}".Trim(),
                new Font("Segoe UI", 7F), new RectangleF(x, bounds.Top + 20, bounds.Width - 38, 15), theme.TextMuted);
        }
        if (HasControl("reset") && window?.ResetAt is DateTimeOffset resetAt)
        {
            DrawText(graphics, ResetText(resetAt), new Font("Segoe UI", 7F),
                new RectangleF(bounds.Left + 68, bounds.Top + 20, bounds.Width - 104, 15), theme.TextMuted,
                StringAlignment.Far);
        }
    }

    private void DrawSparkline(Graphics graphics, UsageSnapshot snapshot, Color color, Rectangle bounds, ZarpaThemeTokens theme)
    {
        var points = _history.Where(point =>
                string.Equals(ProviderCatalog.NormalizeId(point.Provider), ProviderCatalog.NormalizeId(snapshot.Provider), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(point.Window, snapshot.Windows.FirstOrDefault()?.Id, StringComparison.OrdinalIgnoreCase))
            .OrderBy(point => point.Timestamp)
            .TakeLast(18)
            .ToArray();
        if (points.Length < 2) return;

        using var track = new Pen(Color.FromArgb(80, theme.TextMuted), 1F);
        graphics.DrawLine(track, bounds.Left, bounds.Bottom, bounds.Right, bounds.Bottom);
        using var line = new Pen(color, 1.4F);
        var mapped = points.Select((point, index) => new PointF(
            bounds.Left + index * (bounds.Width - 1F) / (points.Length - 1),
            bounds.Bottom - (float)Math.Clamp(point.PercentUsed, 0D, 1D) * bounds.Height)).ToArray();
        graphics.DrawLines(line, mapped);
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
        return selected.Take(4).ToArray();
    }

    private int SegmentWidth() => DefaultSegmentWidth +
        (HasControl("usedTotal") ? 18 : 0) + (HasControl("reset") ? 18 : 0);

    private bool TryPlace(int providerCount, out Rectangle placement)
    {
        placement = Rectangle.Empty;
        var taskbar = FindWindow("Shell_TrayWnd", null);
        if (taskbar == IntPtr.Zero || !GetWindowRect(taskbar, out var taskbarRect)) return false;
        if (taskbarRect.Height >= taskbarRect.Width) return false;

        var tray = FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
        var right = taskbarRect.Right - 12;
        if (tray != IntPtr.Zero && GetWindowRect(tray, out var trayRect)) right = trayRect.Left - TaskbarGap;

        var width = 14 + providerCount * SegmentWidth();
        var left = right - width;
        if (left < taskbarRect.Left + TaskbarReservedLeft || width > taskbarRect.Width - TaskbarReservedLeft)
            return false;

        placement = new Rectangle(left, taskbarRect.Top + Math.Max(0, (taskbarRect.Height - OverlayHeight) / 2),
            width, OverlayHeight);
        ClientSize = placement.Size;
        return true;
    }

    private static TaskbarOverlayConfig CloneConfig(TaskbarOverlayConfig value) => new()
    {
        Enabled = value.Enabled,
        ProviderIds = value.ProviderIds?.ToList() ?? [],
        Controls = value.Controls?.ToList() ?? TaskbarOverlayControlCatalog.Default.ToList()
    };

    private static string FormatValue(double value) => value >= 1000D ? value.ToString("0.#") : value.ToString("0.##");

    private static string ResetText(DateTimeOffset resetAt)
    {
        var remaining = resetAt - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero) return "Reset due";
        if (remaining.TotalHours >= 1) return $"{(int)remaining.TotalHours}h {remaining.Minutes}m";
        return $"{Math.Max(1, remaining.Minutes)}m";
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parentHandle, IntPtr childAfter, string? className, string? windowTitle);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out NativeRect rectangle);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeRect
    {
        public readonly int Left;
        public readonly int Top;
        public readonly int Right;
        public readonly int Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }
}
