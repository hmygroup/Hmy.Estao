using System.ComponentModel;
using System.Drawing.Drawing2D;
using Svg;
using ZarpaSuite.Controls;

namespace Hmy.Estao.App.Controls.Zarpa;

internal static class ZarpaPopoverPalette
{
    public static readonly Color Canvas = Color.FromArgb(70, 65, 211);
    public static readonly Color SurfaceTop = Color.FromArgb(225, 224, 255);
    public static readonly Color SurfaceBottom = Color.FromArgb(190, 192, 247);
    public static readonly Color Border = Color.FromArgb(220, 218, 255);
    public static readonly Color Text = Color.FromArgb(37, 36, 57);
    public static readonly Color TextMuted = Color.FromArgb(105, 101, 129);
    public static readonly Color Track = Color.FromArgb(193, 193, 232);
    public static readonly Color Accent = Color.FromArgb(56, 124, 235);
    public static readonly Color Meter = Color.FromArgb(199, 125, 79);
}

internal static class ZarpaPopoverPaint
{
    internal static GraphicsPath RoundedPath(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(Math.Min(bounds.Width, bounds.Height), Math.Max(1, radius * 2));
        var arc = new Rectangle(bounds.Left, bounds.Top, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    internal static void FillRounded(Graphics graphics, Color color, Rectangle bounds, int radius)
    {
        using var path = RoundedPath(bounds, radius);
        using var brush = new SolidBrush(color);
        graphics.FillPath(brush, path);
    }
}

internal static class ZarpaProviderIconCatalog
{
    private static readonly Dictionary<(string Provider, int Width, int Height, int Tint), Bitmap?> Cache = new();
    private static readonly Dictionary<string, string?> SvgSources = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object SyncRoot = new();

    public static bool TryDraw(Graphics graphics, string provider, Rectangle bounds, Color? monochromeTint = null)
    {
        if (string.IsNullOrWhiteSpace(provider) || bounds.Width <= 0 || bounds.Height <= 0) return false;
        var key = provider.Trim().ToLowerInvariant();
        Bitmap? bitmap;
        var cacheKey = (key, bounds.Width, bounds.Height, monochromeTint?.ToArgb() ?? 0);
        lock (SyncRoot)
        {
            if (!Cache.TryGetValue(cacheKey, out bitmap))
            {
                if (!SvgSources.TryGetValue(key, out var svgSource))
                {
                    using var stream = typeof(ZarpaProviderIconCatalog).Assembly
                        .GetManifestResourceStream($"Hmy.Estao.ProviderIcons.{key}.svg");
                    if (stream is not null)
                    {
                        using var reader = new StreamReader(stream);
                        svgSource = reader.ReadToEnd();
                    }
                    SvgSources[key] = svgSource;
                }

                if (string.IsNullOrWhiteSpace(svgSource))
                {
                    Cache[cacheKey] = null;
                    return false;
                }

                if (monochromeTint is Color tint)
                {
                    var html = $"#{tint.R:X2}{tint.G:X2}{tint.B:X2}";
                    svgSource = svgSource
                        .Replace("#1F2328", html, StringComparison.OrdinalIgnoreCase)
                        .Replace("#211E1E", html, StringComparison.OrdinalIgnoreCase);
                }
                var document = SvgDocument.FromSvg<SvgDocument>(svgSource);
                bitmap = document.Draw(bounds.Width, bounds.Height);
                Cache[cacheKey] = bitmap;
            }
        }

        if (bitmap is null) return false;
        var previousInterpolation = graphics.InterpolationMode;
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(bitmap, bounds);
        graphics.InterpolationMode = previousInterpolation;
        return true;
    }
}

internal sealed class ZarpaPopoverBackdrop : Panel
{
    public ZarpaPopoverBackdrop()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = ZarpaPopoverPalette.Canvas;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using (var background = new LinearGradientBrush(ClientRectangle,
                   Color.FromArgb(78, 75, 222), Color.FromArgb(62, 44, 183), 28F))
            e.Graphics.FillRectangle(background, ClientRectangle);

        using (var firstBand = new SolidBrush(Color.FromArgb(58, 229, 134, 188)))
        using (var path = new GraphicsPath())
        {
            path.AddPolygon
            ([
                new Point(-48, 138), new Point(Width + 80, 212), new Point(Width + 80, 276),
                new Point(-48, 202)
            ]);
            e.Graphics.FillPath(firstBand, path);
        }

        using (var secondBand = new SolidBrush(Color.FromArgb(46, 236, 155, 204)))
        using (var path = new GraphicsPath())
        {
            path.AddPolygon
            ([
                new Point(-70, 286), new Point(Width + 90, 414), new Point(Width + 90, 478),
                new Point(-70, 350)
            ]);
            e.Graphics.FillPath(secondBand, path);
        }

    }
}

internal sealed class ZarpaBufferedPanel : Panel
{
    public ZarpaBufferedPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint |
                 ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw, true);
        UpdateStyles();
    }
}

internal sealed class ZarpaReferenceSurface : Panel, IZarpaThemeAware
{
    private ZarpaThemeTokens? _theme;

    public ZarpaReferenceSurface()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = ZarpaPopoverPalette.SurfaceTop;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        using var path = ZarpaPopoverPaint.RoundedPath(new Rectangle(0, 0, Width, Height), 22);
        Region = new Region(path);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(_theme?.Surface ?? ZarpaPopoverPalette.SurfaceTop);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        using (var background = new SolidBrush(_theme?.Surface ?? ZarpaPopoverPalette.SurfaceTop))
        using (var path = ZarpaPopoverPaint.RoundedPath(bounds, 18))
            e.Graphics.FillPath(background, path);

        using var border = new Pen(_theme?.Border ?? ZarpaPopoverPalette.Border, 1F);
        using var borderPath = ZarpaPopoverPaint.RoundedPath(bounds, 18);
        e.Graphics.DrawPath(border, borderPath);
    }

    public void ApplyTheme(ZarpaThemeTokens value)
    {
        _theme = value;
        BackColor = value.Surface;
        Invalidate();
    }
}

internal sealed class ZarpaReferenceProgressBar : Control
{
    private int _value;

    public ZarpaReferenceProgressBar()
    {
        Height = 12;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => _value;
        set { _value = Math.Clamp(value, 0, 100); Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new Rectangle(0, 1, Math.Max(1, Width - 1), Math.Max(4, Height - 2));
        ZarpaPopoverPaint.FillRounded(e.Graphics, ZarpaPopoverPalette.Track, track, track.Height / 2);
        var fillWidth = (int)Math.Round(track.Width * _value / 100D);
        if (fillWidth > 0)
            ZarpaPopoverPaint.FillRounded(e.Graphics, ZarpaPopoverPalette.Meter,
                new Rectangle(track.Left, track.Top, Math.Max(10, fillWidth), track.Height), track.Height / 2);
    }
}

internal sealed class ZarpaProviderTab : Control, IZarpaThemeAware
{
    private ZarpaThemeTokens? _theme;
    private bool _active;
    private bool _available = true;
    private int _usagePercent;

    public ZarpaProviderTab(string provider, string iconKey)
    {
        Provider = provider;
        IconKey = iconKey;
        AccessibleName = provider;
        AccessibleRole = AccessibleRole.PageTab;
        Cursor = Cursors.Hand;
        TabStop = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        BackColor = ZarpaPopoverPalette.SurfaceTop;
    }

    public string Provider { get; }
    public string IconKey { get; }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Active { get => _active; set { _active = value; Invalidate(); } }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Available { get => _available; set { _available = value; Invalidate(); } }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int UsagePercent { get => _usagePercent; set { _usagePercent = Math.Clamp(value, 0, 100); Invalidate(); } }

    public void ApplyTheme(ZarpaThemeTokens value)
    {
        _theme = value;
        BackColor = value.Surface;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var veryCompact = Height <= 40;
        var bounds = veryCompact
            ? new Rectangle(2, 0, Math.Max(1, Width - 5), Math.Max(1, Height - 1))
            : new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
        var accent = _theme?.Accent ?? ZarpaPopoverPalette.Accent;
        var muted = _theme?.TextMuted ?? ZarpaPopoverPalette.TextMuted;
        if (_active) ZarpaPopoverPaint.FillRounded(e.Graphics, accent, bounds, veryCompact ? 9 : 13);
        var text = _active ? Color.White : _available ? muted : Color.FromArgb(130, muted);
        var compact = Height < 65;
        using var font = new Font("Segoe UI", veryCompact ? 8.5F : compact ? 9F : 12F);
        var textSize = TextRenderer.MeasureText(Provider, font, new Size(Width, Height),
            TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        var iconSize = veryCompact ? 15 : 18;
        var contentHeight = Height - (veryCompact ? 7 : 10);
        var textWidth = Math.Min(textSize.Width, Math.Max(20, Width - iconSize - 12));
        var groupWidth = iconSize + 4 + textWidth;
        var groupLeft = Math.Max(4, (Width - groupWidth) / 2);
        var iconTop = Math.Max(1, (contentHeight - iconSize) / 2);
        ZarpaProviderIconCatalog.TryDraw(e.Graphics, IconKey,
            new Rectangle(groupLeft, iconTop, iconSize, iconSize), _theme?.Text);
        TextRenderer.DrawText(e.Graphics, Provider, font,
            new Rectangle(groupLeft + iconSize + 4, 0, textWidth, contentHeight), text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        var trackHeight = veryCompact ? 3 : compact ? 5 : 7;
        var trackInset = veryCompact ? 8 : 9;
        var track = new Rectangle(trackInset, Height - trackHeight - (veryCompact ? 3 : 3),
            Math.Max(1, Width - trackInset * 2), trackHeight);
        ZarpaPopoverPaint.FillRounded(e.Graphics,
            _active ? Color.FromArgb(120, 255, 255, 255) : _theme?.SurfaceRaised ?? Color.FromArgb(172, 172, 213), track, 4);
        var meterWidth = (int)Math.Round(track.Width * _usagePercent / 100D);
        if (meterWidth > 0)
            ZarpaPopoverPaint.FillRounded(e.Graphics, _theme?.Success ?? Color.FromArgb(69, 169, 165),
                new Rectangle(track.Left, track.Top, Math.Max(5, meterWidth), track.Height), 4);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location)) OnClick(EventArgs.Empty);
    }
}

internal sealed class ZarpaReferenceAction : Control, IZarpaThemeAware
{
    private ZarpaThemeTokens? _theme;
    private bool _hot;
    private bool _pressed;

    public ZarpaReferenceAction()
    {
        Height = 34;
        Cursor = Cursors.Hand;
        Font = new Font("Segoe UI", 11F);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        BackColor = ZarpaPopoverPalette.SurfaceTop;
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string IconKey { get; init; } = string.Empty;

    public void ApplyTheme(ZarpaThemeTokens value)
    {
        _theme = value;
        BackColor = value.Surface;
        ForeColor = value.Text;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (_hot) ZarpaPopoverPaint.FillRounded(e.Graphics, _theme?.SurfaceRaised ?? Color.FromArgb(30, 180, 179, 226), ClientRectangle, 8);
        TextRenderer.DrawText(e.Graphics, Text, Font,
            new Rectangle(4, 0, Width - 4, Height),
            _theme?.Text ?? ZarpaPopoverPalette.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hot = false; _pressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _pressed = e.Button == MouseButtons.Left; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        var invoke = _pressed && e.Button == MouseButtons.Left && ClientRectangle.Contains(e.Location);
        _pressed = false;
        Invalidate();
        if (invoke) OnClick(EventArgs.Empty);
    }
}
