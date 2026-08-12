using System.ComponentModel;
using System.Drawing.Drawing2D;
using Hmy.Estao.Core.Configuration;
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

    internal static Color EnsureContrast(Color foreground, Color background, double minimumRatio = 4.5D)
    {
        var effectiveForeground = Composite(foreground, background);
        if (ContrastRatio(effectiveForeground, background) >= minimumRatio)
            return effectiveForeground;

        var black = Color.Black;
        var white = Color.White;
        return ContrastRatio(black, background) >= ContrastRatio(white, background) ? black : white;
    }

    internal static Color Blend(Color background, Color foreground, double amount)
    {
        var mix = Math.Clamp(amount, 0D, 1D);
        return Color.FromArgb(
            (int)Math.Round(background.R + (foreground.R - background.R) * mix),
            (int)Math.Round(background.G + (foreground.G - background.G) * mix),
            (int)Math.Round(background.B + (foreground.B - background.B) * mix));
    }

    private static Color Composite(Color foreground, Color background)
    {
        if (foreground.A == byte.MaxValue) return foreground;
        var alpha = foreground.A / 255D;
        return Color.FromArgb(
            (int)Math.Round(background.R + (foreground.R - background.R) * alpha),
            (int)Math.Round(background.G + (foreground.G - background.G) * alpha),
            (int)Math.Round(background.B + (foreground.B - background.B) * alpha));
    }

    private static double ContrastRatio(Color first, Color second)
    {
        var firstLuminance = RelativeLuminance(first);
        var secondLuminance = RelativeLuminance(second);
        return (Math.Max(firstLuminance, secondLuminance) + 0.05D) /
            (Math.Min(firstLuminance, secondLuminance) + 0.05D);
    }

    private static double RelativeLuminance(Color color)
    {
        static double Linearize(byte channel)
        {
            var value = channel / 255D;
            return value <= 0.03928D
                ? value / 12.92D
                : Math.Pow((value + 0.055D) / 1.055D, 2.4D);
        }

        return 0.2126D * Linearize(color.R) +
            0.7152D * Linearize(color.G) +
            0.0722D * Linearize(color.B);
    }
}

internal static class ZarpaUsageColorResolver
{
    public static Color OverrideFor(UsageColorConfig? config, double? percentUsed)
    {
        if (percentUsed is not double used) return Color.Empty;
        var value = UsageColorCatalog.ColorFor(config, used);
        if (value is null) return Color.Empty;
        try { return ColorTranslator.FromHtml(value); }
        catch (Exception) { return Color.Empty; }
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

    public static Bitmap? CreateBitmap(string provider, int size, Color? monochromeTint = null)
    {
        var pixelSize = Math.Max(1, size);
        var bitmap = new Bitmap(pixelSize, pixelSize, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.Clear(Color.Transparent);
        if (TryDraw(graphics, provider, new Rectangle(0, 0, pixelSize, pixelSize), monochromeTint)) return bitmap;
        bitmap.Dispose();
        return null;
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

internal sealed class ZarpaScrollBar : Control, IZarpaThemeAware
{
    private const int SmoothScrollInterval = 16;
    private const int WheelStep = 24;
    private ZarpaThemeTokens? _theme;
    private Orientation _orientation = Orientation.Vertical;
    private int _contentSize;
    private int _viewportSize = 1;
    private int _value;
    private int _targetValue;
    private bool _hot;
    private bool _dragging;
    private int _dragOffset;
    private readonly System.Windows.Forms.Timer _smoothScrollTimer = new() { Interval = SmoothScrollInterval };

    public ZarpaScrollBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        BackColor = ZarpaPopoverPalette.SurfaceTop;
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.ScrollBar;
        MinimumSize = new Size(8, 8);
        _smoothScrollTimer.Tick += (_, _) => AnimateScroll();
    }

    public event EventHandler? ValueChanged;

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Orientation Orientation
    {
        get => _orientation;
        set
        {
            if (_orientation == value) return;
            _orientation = value;
            Invalidate();
        }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int ContentSize
    {
        get => _contentSize;
        set { _contentSize = Math.Max(0, value); ClampValue(); Invalidate(); }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int ViewportSize
    {
        get => _viewportSize;
        set { _viewportSize = Math.Max(1, value); ClampValue(); Invalidate(); }
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => _value;
        set
        {
            var next = Math.Clamp(value, 0, MaximumValue);
            if (_value == next) return;
            StopSmoothScroll();
            SetCurrentValue(next);
        }
    }

    public int MaximumValue => Math.Max(0, _contentSize - _viewportSize);

    public void SetRange(int contentSize, int viewportSize)
    {
        _contentSize = Math.Max(0, contentSize);
        _viewportSize = Math.Max(1, viewportSize);
        ClampValue();
        Invalidate();
    }

    public void ScrollByWheel(int delta)
    {
        if (!Enabled || delta == 0 || MaximumValue == 0) return;
        var wheelDelta = delta / (double)SystemInformation.MouseWheelScrollDelta;
        var start = _smoothScrollTimer.Enabled ? _targetValue : _value;
        _targetValue = Math.Clamp(
            (int)Math.Round(start - wheelDelta * WheelStep), 0, MaximumValue);
        if (_targetValue == _value) return;
        _smoothScrollTimer.Start();
    }

    public void ApplyTheme(ZarpaThemeTokens value)
    {
        _theme = value;
        BackColor = value.Surface;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new Rectangle(2, 2, Math.Max(1, Width - 4), Math.Max(1, Height - 4));
        var trackColor = Color.FromArgb(55, _theme?.SurfaceRaised ?? ZarpaPopoverPalette.Track);
        ZarpaPopoverPaint.FillRounded(e.Graphics, trackColor, track, Math.Min(track.Width, track.Height) / 2);

        var thumb = ThumbBounds(track);
        if (thumb.Width <= 0 || thumb.Height <= 0) return;
        var thumbColor = _hot || _dragging
            ? _theme?.Accent ?? ZarpaPopoverPalette.Accent
            : Color.FromArgb(145, _theme?.TextMuted ?? ZarpaPopoverPalette.TextMuted);
        ZarpaPopoverPaint.FillRounded(e.Graphics, thumbColor, thumb,
            Math.Min(thumb.Width, thumb.Height) / 2);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (!_dragging) { _hot = false; Invalidate(); } }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left) return;
        StopSmoothScroll();
        var thumb = ThumbBounds(TrackBounds());
        var coordinate = _orientation == Orientation.Horizontal ? e.X : e.Y;
        if (thumb.Contains(e.Location))
        {
            _dragging = true;
            _dragOffset = coordinate - (_orientation == Orientation.Horizontal ? thumb.Left : thumb.Top);
            Capture = true;
            Invalidate();
            return;
        }

        var direction = coordinate < (_orientation == Orientation.Horizontal ? thumb.Left : thumb.Top) ? -1 : 1;
        Value += direction * Math.Max(1, _viewportSize);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        var track = TrackBounds();
        var thumb = ThumbBounds(track);
        var coordinate = _orientation == Orientation.Horizontal ? e.X : e.Y;
        var trackLength = _orientation == Orientation.Horizontal ? track.Width : track.Height;
        var thumbLength = _orientation == Orientation.Horizontal ? thumb.Width : thumb.Height;
        var available = Math.Max(1, trackLength - thumbLength);
        var position = Math.Clamp(coordinate - _dragOffset - (_orientation == Orientation.Horizontal ? track.Left : track.Top), 0, available);
        Value = (int)Math.Round(position / (double)available * MaximumValue);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button != MouseButtons.Left) return;
        _dragging = false;
        Capture = false;
        _hot = ClientRectangle.Contains(e.Location);
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ScrollByWheel(e.Delta);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!Enabled) return;
        var line = Math.Max(1, _viewportSize / 10);
        var page = Math.Max(1, _viewportSize - line);
        switch (e.KeyCode)
        {
            case Keys.Up when _orientation == Orientation.Vertical:
            case Keys.Left when _orientation == Orientation.Horizontal:
                Value -= line;
                e.Handled = true;
                break;
            case Keys.Down when _orientation == Orientation.Vertical:
            case Keys.Right when _orientation == Orientation.Horizontal:
                Value += line;
                e.Handled = true;
                break;
            case Keys.PageUp:
                Value -= page;
                e.Handled = true;
                break;
            case Keys.PageDown:
                Value += page;
                e.Handled = true;
                break;
            case Keys.Home:
                Value = 0;
                e.Handled = true;
                break;
            case Keys.End:
                Value = MaximumValue;
                e.Handled = true;
                break;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _smoothScrollTimer.Dispose();
        base.Dispose(disposing);
    }

    private Rectangle TrackBounds() => new(2, 2, Math.Max(1, Width - 4), Math.Max(1, Height - 4));

    private Rectangle ThumbBounds(Rectangle track)
    {
        var content = Math.Max(_contentSize, _viewportSize);
        var trackLength = _orientation == Orientation.Horizontal ? track.Width : track.Height;
        var minimum = _orientation == Orientation.Horizontal ? 28 : 24;
        var thumbLength = Math.Clamp((int)Math.Round(trackLength * _viewportSize / (double)Math.Max(1, content)), minimum, trackLength);
        var available = Math.Max(0, trackLength - thumbLength);
        var offset = MaximumValue == 0 ? 0 : (int)Math.Round(available * _value / (double)MaximumValue);
        return _orientation == Orientation.Horizontal
            ? new Rectangle(track.Left + offset, track.Top, thumbLength, track.Height)
            : new Rectangle(track.Left, track.Top + offset, track.Width, thumbLength);
    }

    private void AnimateScroll()
    {
        var distance = _targetValue - _value;
        if (Math.Abs(distance) <= 1)
        {
            SetCurrentValue(_targetValue);
            _smoothScrollTimer.Stop();
            return;
        }

        var step = Math.Sign(distance) * Math.Max(1, (int)Math.Ceiling(Math.Abs(distance) * 0.28D));
        SetCurrentValue(_value + step);
    }

    private void ClampValue()
    {
        _targetValue = Math.Clamp(_targetValue, 0, MaximumValue);
        if (_value > MaximumValue) SetCurrentValue(MaximumValue);
        if (_targetValue == _value) _smoothScrollTimer.Stop();
    }

    private void SetCurrentValue(int value)
    {
        var next = Math.Clamp(value, 0, MaximumValue);
        if (_value == next) return;
        _value = next;
        ValueChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    private void StopSmoothScroll()
    {
        _smoothScrollTimer.Stop();
        _targetValue = _value;
    }
}

/// <summary>
/// Small original vector motifs that give branded palettes a visual signature
/// without embedding or reproducing a tobacco company logo.
/// </summary>
internal sealed class ZarpaThemeMotif : Control, IZarpaThemeAware
{
    private ZarpaThemeTokens? _theme;

    public ZarpaThemeMotif()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = ZarpaPopoverPalette.SurfaceTop;
    }

    public void ApplyTheme(ZarpaThemeTokens value)
    {
        _theme = value;
        BackColor = value.Surface;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_theme is null || Width < 40 || Height < 10) return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var primary = Color.FromArgb(210, _theme.Accent);
        var secondary = Color.FromArgb(175, _theme.Selection);
        var bounds = new Rectangle(Math.Max(4, Width - 42), 2, 34, Height - 5);
        var center = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);

        switch (_theme.Preset)
        {
            case ZarpaThemePreset.Camel:
                DrawCamel(e.Graphics, bounds, primary, secondary);
                break;
            case ZarpaThemePreset.Marlboro:
            case ZarpaThemePreset.MarlboroGold:
                DrawChevron(e.Graphics, bounds, primary, secondary);
                break;
            case ZarpaThemePreset.Lucky:
                using (var outer = new Pen(primary, 2F))
                using (var inner = new Pen(secondary, 2F))
                using (var dot = new SolidBrush(primary))
                {
                    e.Graphics.DrawEllipse(outer, new Rectangle(center.X - 9, center.Y - 9, 18, 18));
                    e.Graphics.DrawEllipse(inner, new Rectangle(center.X - 5, center.Y - 5, 10, 10));
                    e.Graphics.FillEllipse(dot, new Rectangle(center.X - 2, center.Y - 2, 4, 4));
                }
                break;
            case ZarpaThemePreset.Winston:
                using (var red = new SolidBrush(primary))
                using (var blue = new SolidBrush(secondary))
                {
                    e.Graphics.FillRectangle(red, bounds.Left + 2, bounds.Top + 3, bounds.Width - 4, 4);
                    e.Graphics.FillRectangle(blue, bounds.Left + 2, bounds.Top + 9, bounds.Width - 4, 4);
                }
                break;
            case ZarpaThemePreset.Virginia:
                using (var teal = new Pen(primary, 2F))
                using (var gold = new Pen(secondary, 1.5F))
                {
                    e.Graphics.DrawLine(teal, bounds.Left + 8, bounds.Bottom - 3, center.X, bounds.Top + 3);
                    e.Graphics.DrawLine(teal, center.X, bounds.Top + 3, bounds.Right - 8, bounds.Bottom - 3);
                    e.Graphics.DrawLine(gold, bounds.Left + 4, bounds.Top + 4, bounds.Left + 4, bounds.Bottom - 3);
                }
                break;
            case ZarpaThemePreset.Pueblo:
                using (var sun = new Pen(primary, 2F))
                using (var leaf = new Pen(secondary, 2F))
                {
                    e.Graphics.DrawEllipse(sun, new Rectangle(center.X - 6, center.Y - 6, 12, 12));
                    for (var angle = 0; angle < 360; angle += 45)
                    {
                        var radians = angle * Math.PI / 180D;
                        var start = new PointF(center.X + (float)Math.Cos(radians) * 9F, center.Y + (float)Math.Sin(radians) * 9F);
                        var end = new PointF(center.X + (float)Math.Cos(radians) * 12F, center.Y + (float)Math.Sin(radians) * 12F);
                        e.Graphics.DrawLine(sun, start, end);
                    }
                    e.Graphics.DrawArc(leaf, new Rectangle(bounds.Left + 5, bounds.Top + 2, 18, 17), 205, 125);
                }
                break;
            default:
                using (var diamond = new Pen(primary, 1.5F))
                    e.Graphics.DrawRectangle(diamond, new Rectangle(center.X - 6, center.Y - 6, 12, 12));
                break;
        }
    }

    private static void DrawChevron(Graphics graphics, Rectangle bounds, Color primary, Color secondary)
    {
        using var gold = new Pen(secondary, 2F);
        using var red = new Pen(primary, 1.5F);
        var points = new[]
        {
            new PointF(bounds.Left + 3, bounds.Top + 4),
            new PointF(bounds.Left + bounds.Width / 2F, bounds.Bottom - 3),
            new PointF(bounds.Right - 3, bounds.Top + 4)
        };
        graphics.DrawLines(gold, points);
        graphics.DrawLine(red, points[0].X + 3, points[0].Y, points[1].X, points[1].Y - 3);
        graphics.DrawLine(red, points[1].X, points[1].Y - 3, points[2].X - 3, points[2].Y);
    }

    private static void DrawCamel(Graphics graphics, Rectangle bounds, Color primary, Color secondary)
    {
        using var sun = new SolidBrush(secondary);
        using var dunes = new Pen(primary, 1.5F);
        graphics.FillEllipse(sun, new Rectangle(bounds.Left + 4, bounds.Top + 2, 7, 7));
        graphics.DrawArc(dunes, new Rectangle(bounds.Left, bounds.Top + 8, bounds.Width, 12), 190, 160);
        graphics.DrawArc(dunes, new Rectangle(bounds.Left + 6, bounds.Top + 4, 14, 10), 185, 170);
        graphics.DrawArc(dunes, new Rectangle(bounds.Left + 15, bounds.Top + 5, 14, 9), 185, 170);
        graphics.DrawLine(dunes, bounds.Left + 10, bounds.Bottom - 5, bounds.Left + 10, bounds.Bottom - 1);
        graphics.DrawLine(dunes, bounds.Left + 23, bounds.Bottom - 5, bounds.Left + 23, bounds.Bottom - 1);
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
        using (var path = ZarpaPopoverPaint.RoundedPath(bounds, CornerRadius))
            e.Graphics.FillPath(background, path);

        using var border = new Pen(_theme?.Border ?? ZarpaPopoverPalette.Border, 1F);
        using var borderPath = ZarpaPopoverPaint.RoundedPath(bounds, CornerRadius);
        e.Graphics.DrawPath(border, borderPath);
    }

    public void ApplyTheme(ZarpaThemeTokens value)
    {
        _theme = value;
        BackColor = value.Surface;
        Invalidate();
    }

    private int CornerRadius
    {
        get
        {
            var logicalRadius = _theme is null ? 9 : Math.Max(6, _theme.GroupCornerRadius);
            return Math.Max(1, (int)Math.Round(logicalRadius * DeviceDpi / 96D));
        }
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
    private Color _usageColor = Color.Empty;

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

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color UsageColor { get => _usageColor; set { _usageColor = value; Invalidate(); } }

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
        var surface = _theme?.Surface ?? ZarpaPopoverPalette.SurfaceTop;
        var selection = _theme?.Selection ?? accent;
        var muted = _theme?.TextMuted ?? ZarpaPopoverPalette.TextMuted;
        var selectedText = ZarpaPopoverPaint.EnsureContrast(selection, surface);
        var text = _active
            ? selectedText
            : _available
                ? ZarpaPopoverPaint.EnsureContrast(_theme?.Text ?? ZarpaPopoverPalette.Text, surface)
                : ZarpaPopoverPaint.EnsureContrast(muted, surface, 3D);
        if (_active)
            ZarpaPopoverPaint.FillRounded(e.Graphics, selection, bounds, veryCompact ? 9 : 13);
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
            new Rectangle(groupLeft, iconTop, iconSize, iconSize), text);
        TextRenderer.DrawText(e.Graphics, Provider, font,
            new Rectangle(groupLeft + iconSize + 4, 0, textWidth, contentHeight), text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);

        var trackHeight = veryCompact ? 3 : compact ? 5 : 7;
        var trackInset = veryCompact ? 8 : 9;
        var track = new Rectangle(trackInset, Height - trackHeight - (veryCompact ? 3 : 3),
            Math.Max(1, Width - trackInset * 2), trackHeight);
        ZarpaPopoverPaint.FillRounded(e.Graphics,
            _active
                ? ZarpaPopoverPaint.Blend(selection, selectedText, 0.28D)
                : _theme?.SurfaceRaised ?? Color.FromArgb(172, 172, 213), track, 4);
        var meterWidth = (int)Math.Round(track.Width * _usagePercent / 100D);
        if (meterWidth > 0)
            ZarpaPopoverPaint.FillRounded(e.Graphics, _usageColor.IsEmpty
                    ? _theme?.Success ?? Color.FromArgb(69, 169, 165)
                    : _usageColor,
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
