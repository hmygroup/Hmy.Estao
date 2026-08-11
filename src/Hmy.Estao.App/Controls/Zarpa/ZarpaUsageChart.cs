using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.Drawing.Text;
using Hmy.Estao.Core.Models;
using ZarpaSuite.Controls;

namespace Hmy.Estao.App.Controls.Zarpa;

internal sealed record ZarpaUsageChartPoint(DateTimeOffset Timestamp, double Value);

/// <summary>
/// A drawable line on <see cref="ZarpaUsageChart"/>. Set <paramref name="IsTarget"/>
/// to render this as a dashed daily-pacing reference line instead of a filled
/// usage curve (no area fill, no point markers, no legend/dot chip).
/// </summary>
internal sealed record ZarpaUsageChartSeries(
    string Label, Color Color, IReadOnlyList<ZarpaUsageChartPoint> Points,
    TimeSpan TimeRange, DateTimeOffset? ResetAt = null,
    bool IsTarget = false, bool IsProjection = false);

internal sealed record ZarpaUsageChartForecast(string Text, Color Color);

/// <summary>
/// Compact, double-buffered usage graph. It only receives locally persisted
/// samples; no network call is made by this control.
/// </summary>
internal sealed class ZarpaUsageChart : Control, IZarpaThemeAware
{
    internal const int PreferredHeight = 224;
    // Keep the timer close to the display cadence. The Stopwatch still owns
    // the animation clock, so delayed UI ticks catch up instead of slowing
    // down the animation.
    private const int AnimationTimerIntervalMs = 16;
    private const int AnimationDurationMs = 700;
    private readonly Font _titleFont = new("Segoe UI", 9.5F, FontStyle.Bold);
    private readonly Font _bodyFont = new("Segoe UI", 8F);
    private readonly Font _axisFont = new("Segoe UI", 7F);
    private readonly Stopwatch _animationStopwatch = new();
    private readonly System.Windows.Forms.Timer _animationTimer;
    private ZarpaThemeTokens? _theme;
    private IReadOnlyList<ZarpaUsageChartSeries> _series = [];
    private bool _preview;
    private Bitmap? _renderCache;
    private bool _cacheDirty = true;
    private bool _seriesCacheDirty = true;
    private double _animationProgress = 1D;
    private bool _animationRequested;
    private bool _disposed;
    private bool _mappedPointsDirty = true;
    private Rectangle _mappedPlot;
    private PointF[][] _mappedPoints = [];
    private Bitmap? _seriesCache;
    private Rectangle _seriesCachePlot;
    private DateTimeOffset _rangeEnd = DateTimeOffset.UtcNow;
    private ZarpaUsageChartForecast? _forecast;

    public ZarpaUsageChart()
    {
        Height = PreferredHeight;
        MinimumSize = new Size(240, 169);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = ZarpaPopoverPalette.SurfaceTop;
        AccessibleName = "Local usage history";
        AccessibleRole = AccessibleRole.Graphic;

        _animationTimer = new System.Windows.Forms.Timer { Interval = AnimationTimerIntervalMs };
        _animationTimer.Tick += AnimationTimerOnTick;
    }

    public void SetData(IReadOnlyList<ZarpaUsageChartSeries> series, bool preview = false,
        ZarpaUsageChartForecast? forecast = null)
    {
        _series = series;
        _preview = preview;
        _forecast = forecast;
        _rangeEnd = DateTimeOffset.UtcNow;
        _mappedPointsDirty = true;
        _animationRequested = true;
        StartAnimation();
        InvalidateCache();
    }

    public void ApplyTheme(ZarpaThemeTokens value)
    {
        _theme = value;
        BackColor = value.Surface;
        InvalidateCache();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _titleFont.Dispose();
            _bodyFont.Dispose();
            _axisFont.Dispose();
            _disposed = true;
            StopAnimation();
            _animationTimer.Dispose();
            _renderCache?.Dispose();
            _seriesCache?.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 0 || Height <= 0) return;
        if (_cacheDirty || _renderCache is null || _renderCache.Size != ClientSize)
        {
            _renderCache?.Dispose();
            _renderCache = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using var cacheGraphics = Graphics.FromImage(_renderCache);
            Render(cacheGraphics);
            _cacheDirty = false;
        }

        EnsureSeriesCache(GetPlotRectangle());
        e.Graphics.DrawImageUnscaled(_renderCache, Point.Empty);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        DrawAnimatedSeries(e.Graphics, GetPlotRectangle(), _animationProgress);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        _mappedPointsDirty = true;
        InvalidateCache();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (_animationRequested)
            StartAnimation();
    }

    private void InvalidateCache()
    {
        _cacheDirty = true;
        _seriesCacheDirty = true;
        Invalidate();
    }

    private void Render(Graphics graphics)
    {
        graphics.Clear(_theme?.Surface ?? ZarpaPopoverPalette.SurfaceTop);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        var surface = _theme?.SurfaceRaised ?? ZarpaPopoverPalette.SurfaceTop;
        var border = _theme?.Border ?? ZarpaPopoverPalette.Border;
        var text = _theme?.Text ?? ZarpaPopoverPalette.Text;
        var muted = _theme?.TextMuted ?? ZarpaPopoverPalette.TextMuted;
        // Reserve a clean axis gutter on the right so labels never sit on top
        // of the curves when the widget is narrow.
        var plot = GetPlotRectangle();
        var card = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));

        ZarpaPopoverPaint.FillRounded(graphics, surface, card, 12);
        using (var outline = new Pen(border, 1F))
        using (var path = ZarpaPopoverPaint.RoundedPath(card, 12))
            graphics.DrawPath(outline, path);

        DrawChartText(graphics, "Usage history", _titleFont,
            new RectangleF(12, 7, 120, 22), text, StringAlignment.Near);
        var ranges = string.Join(" / ", _series
            .Where(series => !series.IsTarget)
            .Select(series => UsageWindowCatalog.DisplayLabel(series.TimeRange))
            .Distinct(StringComparer.Ordinal));
        var subtitle = _preview ? "Preview · local history" : "Stored locally";
        if (!string.IsNullOrEmpty(ranges)) subtitle += $" · {ranges} windows";
        if (_series.Any(series => series.IsTarget))
            subtitle += " · - - daily target";
        if (_series.Any(series => series.IsProjection))
            subtitle += " · trend";
        DrawChartText(graphics, subtitle, _bodyFont,
            new RectangleF(12, 25, 220, 16), muted, StringAlignment.Near);

        if (_forecast is not null)
            DrawChartText(graphics, _forecast.Text, _bodyFont,
                new RectangleF(12, 40, Math.Max(120, Width - 68), 16),
                _forecast.Color, StringAlignment.Near, true);

        DrawLegend(graphics, text, muted);
        DrawGrid(graphics, plot, border, muted);

        if (_series.Count == 0 || _series.All(series => series.Points.Count == 0))
        {
            DrawChartText(graphics, "Collecting local usage data…", _bodyFont,
                plot, muted, StringAlignment.Center);
        }

        DrawTimeAxis(graphics, plot, border, muted);
    }

    private void DrawTimeAxis(Graphics graphics, Rectangle plot, Color border, Color muted)
    {
        var reference = _series.FirstOrDefault(series => !series.IsTarget && !series.IsProjection);
        var rangeStart = reference is null ? _rangeEnd - UsageWindowCatalog.Weekly : RangeStart(reference);
        var rangeEnd = reference is null ? _rangeEnd : RangeEnd(reference);
        var midpoint = rangeStart + TimeSpan.FromTicks((rangeEnd - rangeStart).Ticks / 2);
        var fullRange = rangeEnd - rangeStart;

        using var markerPen = new Pen(Color.FromArgb(55, border), 1F) { DashStyle = DashStyle.Dot };
        graphics.DrawLine(markerPen, plot.Left + plot.Width / 2, plot.Top, plot.Left + plot.Width / 2, plot.Bottom);

        DrawChartText(graphics, FormatAxisTime(rangeStart, fullRange), _axisFont,
            new RectangleF(plot.Left, plot.Bottom + 5, 88, 15), muted, StringAlignment.Near);
        DrawChartText(graphics, FormatAxisTime(midpoint, fullRange), _axisFont,
            new RectangleF(plot.Left + plot.Width / 2F - 44F, plot.Bottom + 5, 88, 15), muted, StringAlignment.Center);
        DrawChartText(graphics, FormatAxisTime(rangeEnd, fullRange), _axisFont,
            new RectangleF(plot.Right - 88, plot.Bottom + 5, 88, 15), muted, StringAlignment.Far);

        if (_rangeEnd > rangeStart && _rangeEnd < rangeEnd)
        {
            var progress = Math.Clamp(
                (_rangeEnd - rangeStart).TotalSeconds / fullRange.TotalSeconds, 0D, 1D);
            var nowX = plot.Left + (float)progress * plot.Width;
            using var nowPen = new Pen(Color.FromArgb(150, muted), 1F) { DashStyle = DashStyle.Dash };
            graphics.DrawLine(nowPen, nowX, plot.Top, nowX, plot.Bottom);
            DrawChartText(graphics, "now", _axisFont,
                new RectangleF(Math.Clamp(nowX - 18F, plot.Left, plot.Right - 36F), plot.Top + 2, 36, 13),
                muted, StringAlignment.Center);
        }
    }

    private static string FormatAxisTime(DateTimeOffset timestamp, TimeSpan elapsed)
    {
        var local = timestamp.ToLocalTime();
        if (elapsed <= TimeSpan.FromDays(1)) return local.ToString("HH:mm");
        if (elapsed <= TimeSpan.FromDays(14)) return local.ToString("dd/MM HH:mm");
        return local.ToString("dd/MM");
    }

    private void DrawLegend(Graphics graphics, Color text, Color muted)
    {
        var x = Math.Max(155, Width - 164);
        foreach (var series in _series.Where(series => !series.IsTarget && !series.IsProjection).Take(3))
        {
            using var brush = new SolidBrush(series.Color);
            graphics.FillEllipse(brush, new Rectangle(x, 14, 6, 6));
            DrawChartText(graphics, series.Label, _bodyFont,
                new RectangleF(x + 10, 8, 48, 18), text, StringAlignment.Near, true);
            x += 55;
            if (x >= Width - 25) break;
        }
    }

    private static void DrawChartText(Graphics graphics, string value, Font font,
        RectangleF bounds, Color color, StringAlignment alignment, bool ellipsis = false)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = alignment,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
            Trimming = ellipsis ? StringTrimming.EllipsisCharacter : StringTrimming.None
        };
        graphics.DrawString(value, font, brush, bounds, format);
    }

    private void DrawGrid(Graphics graphics, Rectangle plot, Color border, Color muted)
    {
        using var gridPen = new Pen(Color.FromArgb(75, border), 1F);
        gridPen.DashStyle = DashStyle.Dot;
        foreach (var level in new[] { 0D, .5D, 1D })
        {
            var y = plot.Bottom - (int)Math.Round(plot.Height * level);
            graphics.DrawLine(gridPen, plot.Left, y, plot.Right, y);
            DrawChartText(graphics, $"{level:P0}", _axisFont,
                new RectangleF(plot.Right + 4, y - 10, Math.Max(20, Width - plot.Right - 4), 16),
                muted, StringAlignment.Far);
        }
    }

    private void DrawAnimatedSeries(Graphics graphics, Rectangle plot, double progress)
    {
        if (progress <= 0D) return;

        if (_seriesCache is null) return;

        var state = graphics.Save();
        try
        {
            var clipWidth = plot.Width * (float)Math.Clamp(progress, 0D, 1D);
            // Temporarily reveal the chart in conventional chronological order,
            // from the window start on the left towards "now" on the right.
            // A small overflow keeps thick/dotted strokes from being cut.
            graphics.SetClip(new RectangleF(
                plot.Left - 3F, plot.Top - 3F,
                clipWidth + 6F, plot.Height + 6F), CombineMode.Intersect);
            graphics.DrawImageUnscaled(_seriesCache, Point.Empty);
        }
        finally
        {
            graphics.Restore(state);
        }
    }

    private void EnsureSeriesCache(Rectangle plot)
    {
        if (!_seriesCacheDirty && _seriesCache is not null &&
            _seriesCache.Size == ClientSize && _seriesCachePlot == plot)
            return;

        _seriesCache?.Dispose();
        _seriesCache = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
        _seriesCachePlot = plot;

        using var graphics = Graphics.FromImage(_seriesCache);
        graphics.Clear(Color.Transparent);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;

        EnsureMappedPoints(plot);
        for (var index = 0; index < _series.Count; index++)
            DrawSeries(graphics, plot, _series[index].Color, _mappedPoints[index],
                _series[index].IsTarget, _series[index].IsProjection);

        _seriesCacheDirty = false;
    }

    private static void DrawSeries(Graphics graphics, Rectangle plot, Color color,
        IReadOnlyList<PointF> mapped, bool isTarget = false, bool isProjection = false)
    {
        if (mapped.Count == 0) return;

        if (mapped.Count == 1)
        {
            if (isTarget || isProjection) return;
            using var dotBrush = new SolidBrush(color);
            graphics.FillEllipse(dotBrush, new RectangleF(mapped[0].X - 3, mapped[0].Y - 3, 6, 6));
            return;
        }

        if (isTarget)
        {
            using var targetPen = new Pen(color, 2F)
            {
                DashStyle = DashStyle.Dot,
                DashCap = DashCap.Round
            };
            // Pacing is intentionally linear. Smoothing a line that reaches
            // 100% and then stays flat produces a Bezier overshoot above the
            // plot, which looks like a large missing segment.
            graphics.DrawLines(targetPen, mapped.ToArray());
            return;
        }

        if (isProjection)
        {
            using var projectionPen = new Pen(color, 2F)
            {
                DashStyle = DashStyle.Dash,
                DashCap = DashCap.Round
            };
            graphics.DrawLines(projectionPen, mapped.ToArray());
            using var endpoint = new SolidBrush(color);
            var last = mapped[^1];
            graphics.FillEllipse(endpoint, last.X - 3F, last.Y - 3F, 6F, 6F);
            return;
        }

        using var linePath = CreateSmoothPath(mapped);

        using var fillPath = (GraphicsPath)linePath.Clone();
        fillPath.AddLine(mapped[^1].X, plot.Bottom, mapped[0].X, plot.Bottom);
        fillPath.CloseFigure();
        using (var fill = new LinearGradientBrush(plot, Color.FromArgb(92, color), Color.FromArgb(8, color), 90F))
            graphics.FillPath(fill, fillPath);
        using (var pen = new Pen(color, 1.8F))
            graphics.DrawPath(pen, linePath);

        using var brush = new SolidBrush(color);
        foreach (var point in mapped)
            graphics.FillEllipse(brush, new RectangleF(point.X - 2.5F, point.Y - 2.5F, 5F, 5F));
    }

    private void EnsureMappedPoints(Rectangle plot)
    {
        if (!_mappedPointsDirty && _mappedPlot == plot && _mappedPoints.Length == _series.Count)
            return;

        _mappedPoints = _series.Select(series =>
        {
            var rangeStart = RangeStart(series);
            var rangeEnd = RangeEnd(series);
            var rangeSeconds = Math.Max(1D, (rangeEnd - rangeStart).TotalSeconds);
            // Target endpoints are intentionally created at the exact window
            // boundaries. SetData captures its clock a few milliseconds later,
            // so filtering them like history could remove the first endpoint
            // and leave an undrawable one-point target. Keep target endpoints
            // and clamp them to the plot edges instead.
            var visiblePoints = series.IsTarget || series.IsProjection
                ? series.Points
                : series.Points.Where(point => point.Timestamp >= rangeStart && point.Timestamp <= _rangeEnd);
            return visiblePoints
                .OrderBy(point => point.Timestamp)
                .Where(point => !double.IsNaN(point.Value) && !double.IsInfinity(point.Value))
                .Select(point =>
                {
                    var x = plot.Left + (float)Math.Clamp(
                        (point.Timestamp - rangeStart).TotalSeconds / rangeSeconds, 0D, 1D) * plot.Width;
                    // Leave two pixels above 100% so dotted target caps remain
                    // fully visible instead of merging into the top border.
                    var y = plot.Bottom - (float)Math.Clamp(point.Value, 0D, 1D) * (plot.Height - 2F);
                    return new PointF(x, y);
                })
                .ToArray();
        })
            .ToArray();
        _mappedPlot = plot;
        _mappedPointsDirty = false;
    }

    private Rectangle GetPlotRectangle() => new(
        12, 58, Math.Max(20, Width - 52), Math.Max(35, Height - 83));

    private DateTimeOffset RangeStart(ZarpaUsageChartSeries series)
    {
        var range = series.TimeRange > TimeSpan.Zero ? series.TimeRange : UsageWindowCatalog.Weekly;
        return series.ResetAt is { } resetAt && resetAt > _rangeEnd
            ? resetAt - range
            : _rangeEnd - range;
    }

    private DateTimeOffset RangeEnd(ZarpaUsageChartSeries series) =>
        series.ResetAt is { } resetAt && resetAt > _rangeEnd ? resetAt : _rangeEnd;

    private void StartAnimation()
    {
        if (_disposed || !IsHandleCreated) return;

        _animationRequested = false;
        _animationProgress = 0D;
        _animationStopwatch.Restart();
        _animationTimer.Start();
    }

    private void StopAnimation()
    {
        _animationTimer.Stop();
    }

    private void AnimationTimerOnTick(object? sender, EventArgs e)
    {
        if (_disposed || !IsHandleCreated)
        {
            StopAnimation();
            return;
        }

        var elapsedMs = _animationStopwatch.Elapsed.TotalMilliseconds;
        _animationProgress = Math.Clamp(elapsedMs / AnimationDurationMs, 0D, 1D);
        Invalidate(GetPlotRectangle(), false);

        if (_animationProgress >= 1D)
            StopAnimation();
    }

    private static GraphicsPath CreateSmoothPath(IReadOnlyList<PointF> points)
    {
        var path = new GraphicsPath();
        for (var index = 0; index < points.Count - 1; index++)
        {
            var p0 = index == 0 ? points[index] : points[index - 1];
            var p1 = points[index];
            var p2 = points[index + 1];
            var p3 = index + 2 < points.Count ? points[index + 2] : p2;
            var c1 = new PointF(p1.X + (p2.X - p0.X) / 6F, p1.Y + (p2.Y - p0.Y) / 6F);
            var c2 = new PointF(p2.X - (p3.X - p1.X) / 6F, p2.Y - (p3.Y - p1.Y) / 6F);
            path.AddBezier(p1, c1, c2, p2);
        }

        return path;
    }
}
