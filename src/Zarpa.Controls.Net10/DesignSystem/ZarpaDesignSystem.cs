using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ZarpaSuite.Controls
{
    internal sealed class ZarpaDpiScale
    {
        internal const int DefaultDpi = 96;
        private readonly int dpiX, dpiY;

        internal ZarpaDpiScale(int horizontalDpi, int verticalDpi)
        {
            dpiX = Math.Max(48, horizontalDpi);
            dpiY = Math.Max(48, verticalDpi);
        }

        internal int DpiX { get { return dpiX; } }
        internal int DpiY { get { return dpiY; } }
        internal int X(int logicalPixels) { return Scale(logicalPixels, dpiX); }
        internal int Y(int logicalPixels) { return Scale(logicalPixels, dpiY); }
        internal float X(float logicalPixels) { return logicalPixels * dpiX / DefaultDpi; }
        internal Size Size(Size logical) { return new Size(X(logical.Width), Y(logical.Height)); }
        internal Point Point(Point logical) { return new Point(X(logical.X), Y(logical.Y)); }
        internal Padding Padding(Padding logical) { return new Padding(X(logical.Left), Y(logical.Top), X(logical.Right), Y(logical.Bottom)); }
        internal Rectangle Rectangle(Rectangle logical) { return new Rectangle(X(logical.X), Y(logical.Y), X(logical.Width), Y(logical.Height)); }
        internal int Stroke(int logicalPixels) { return Math.Max(1, X(logicalPixels)); }

        internal static ZarpaDpiScale FromControl(Control control)
        {
            if (control == null || control.IsDisposed) return new ZarpaDpiScale(DefaultDpi, DefaultDpi);
            using (Graphics graphics = control.CreateGraphics())
                return new ZarpaDpiScale((int)Math.Round(graphics.DpiX), (int)Math.Round(graphics.DpiY));
        }

        private static int Scale(int value, int dpi)
        {
            long scaled = (long)value * dpi;
            return (int)(scaled >= 0 ? (scaled + DefaultDpi / 2) / DefaultDpi :
                (scaled - DefaultDpi / 2) / DefaultDpi);
        }
    }

    internal sealed class ZarpaPaintAnimator : IDisposable
    {
        private readonly Control control;
        private readonly Action<float> advanceFrame;
        private readonly System.Threading.Timer timer;
        private long lastTimestamp;
        private bool running;
        private bool disposed;
        private int tickPending;

        internal ZarpaPaintAnimator(Control owner, Action<float> frame)
        {
            control = owner;
            advanceFrame = frame;
            timer = new System.Threading.Timer(Pulse, null,
                System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
        }

        internal void Update(bool animate)
        {
            if (disposed) return;
            if (!animate || !control.IsHandleCreated)
            {
                Stop();
                return;
            }
            if (running) return;
            lastTimestamp = Stopwatch.GetTimestamp();
            running = true;
            timer.Change(0, 15);
        }

        internal void Stop()
        {
            running = false;
            if (disposed) return;
            timer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            lastTimestamp = 0L;
        }

        private void Pulse(object state)
        {
            if (!running || control.IsDisposed || !control.IsHandleCreated ||
                System.Threading.Interlocked.Exchange(ref tickPending, 1) != 0) return;
            try
            {
                control.BeginInvoke((MethodInvoker)delegate
                {
                    System.Threading.Interlocked.Exchange(ref tickPending, 0);
                    if (!running || control.IsDisposed) return;
                    long now = Stopwatch.GetTimestamp();
                    float elapsed = (float)Math.Max(0.001, Math.Min(0.050,
                        (now - lastTimestamp) / (double)Stopwatch.Frequency));
                    lastTimestamp = now;
                    advanceFrame(elapsed);
                });
            }
            catch (ObjectDisposedException) { System.Threading.Interlocked.Exchange(ref tickPending, 0); }
            catch (InvalidOperationException) { System.Threading.Interlocked.Exchange(ref tickPending, 0); }
        }

        public void Dispose()
        {
            running = false;
            disposed = true;
            timer.Dispose();
        }
    }

    internal sealed class ZarpaSizeAnimator : IDisposable
    {
        private readonly Control control;
        private readonly Func<int> getValue;
        private readonly Action<int> setValue;
        private readonly Timer timer;
        private int startValue, targetValue, duration;
        private long started;

        internal ZarpaSizeAnimator(Control owner, Func<int> getter, Action<int> setter)
        {
            control = owner;
            getValue = getter;
            setValue = setter;
            timer = new Timer { Interval = 15 };
            timer.Tick += Tick;
        }

        internal void Start(int target, int milliseconds, bool animate)
        {
            targetValue = target;
            if (!animate || !control.IsHandleCreated)
            {
                Stop();
                setValue(target);
                return;
            }
            startValue = getValue();
            duration = Math.Max(1, milliseconds);
            started = Stopwatch.GetTimestamp();
            timer.Start();
        }

        private void Tick(object sender, EventArgs e)
        {
            if (control.IsDisposed || !control.IsHandleCreated)
            {
                Stop();
                return;
            }
            float progress = (float)((Stopwatch.GetTimestamp() - started) /
                (double)Stopwatch.Frequency * 1000D / duration);
            if (progress >= 1F)
            {
                setValue(targetValue);
                Stop();
                return;
            }
            progress = Math.Max(0F, progress);
            float eased = 1F - (float)Math.Pow(1F - progress, 3F);
            int next = (int)Math.Round(startValue + (targetValue - startValue) * eased);
            if (next != getValue()) setValue(next);
        }

        private void Stop()
        {
            started = 0L;
            timer.Stop();
        }

        public void Dispose()
        {
            timer.Tick -= Tick;
            timer.Dispose();
        }
    }

    public enum ZarpaThemePreset
    {
        ZarpaLight,
        ZarpaDark,
        MicaBlue,
        Graphite,
        WarmSand,
        OceanTeal,
        EmeraldForest,
        RoseQuartz,
        LavenderMist,
        MidnightNavy,
        NordFrost,
        Aubergine,
        Marlboro,
        HighContrast,
        Custom
    }

    internal sealed class ZarpaPresetDefinition
    {
        internal Color Canvas, Surface, Raised, Overlay, Border, BorderStrong, Text, Muted;
        internal Color Accent, AccentHover, AccentPressed, Selection, Shadow;
        internal string FontFamily;
        internal float FontSize, HeadingFontSize;
        internal int CornerRadius, GroupCornerRadius, ControlHeight, HeaderHeight, TabHeight;
        internal int SpacingSmall, SpacingMedium, SpacingLarge, IconSize, BorderThickness, ShadowDepth;
        internal int HoverDuration, PressDuration, TabDuration;
    }

    internal static class ZarpaPresetCatalog
    {
        internal static ZarpaPresetDefinition Get(ZarpaThemePreset preset)
        {
            switch (preset)
            {
                case ZarpaThemePreset.ZarpaDark:
                    return Create(Color.FromArgb(15, 20, 31), Color.FromArgb(24, 31, 46), Color.FromArgb(32, 41, 58), Color.FromArgb(45, 56, 76),
                        Color.FromArgb(55, 65, 81), Color.FromArgb(75, 85, 99), Color.FromArgb(244, 247, 251), Color.FromArgb(163, 174, 192),
                        Color.FromArgb(56, 189, 248), Color.FromArgb(125, 211, 252), Color.FromArgb(14, 165, 233), Color.FromArgb(42, 52, 75), Color.FromArgb(90, 0, 0, 0),
                        "Segoe UI", 9F, 22F, 7, 9, 34, 40, 38, 4, 8, 16, 22, 1, 2, 140, 95, 190);
                case ZarpaThemePreset.MicaBlue:
                    return Create(Color.FromArgb(242, 247, 253), Color.FromArgb(251, 253, 255), Color.FromArgb(233, 242, 252), Color.FromArgb(220, 235, 250),
                        Color.FromArgb(207, 224, 241), Color.FromArgb(165, 193, 220), Color.FromArgb(23, 49, 74), Color.FromArgb(91, 117, 143),
                        Color.FromArgb(0, 120, 212), Color.FromArgb(32, 145, 224), Color.FromArgb(0, 90, 158), Color.FromArgb(218, 235, 252), Color.FromArgb(45, 28, 63, 90),
                        "Segoe UI", 9F, 22F, 9, 11, 36, 42, 40, 4, 9, 18, 22, 1, 2, 150, 100, 200);
                case ZarpaThemePreset.WarmSand:
                    return Create(Color.FromArgb(250, 247, 241), Color.FromArgb(255, 253, 249), Color.FromArgb(246, 240, 230), Color.FromArgb(242, 231, 214),
                        Color.FromArgb(226, 215, 199), Color.FromArgb(190, 174, 151), Color.FromArgb(62, 48, 34), Color.FromArgb(123, 103, 81),
                        Color.FromArgb(194, 90, 35), Color.FromArgb(218, 117, 56), Color.FromArgb(157, 65, 22), Color.FromArgb(249, 230, 205), Color.FromArgb(35, 78, 57, 35),
                        "Segoe UI", 9F, 23F, 8, 10, 36, 42, 40, 5, 10, 20, 23, 1, 2, 160, 105, 210);
                case ZarpaThemePreset.OceanTeal:
                    return Create(Color.FromArgb(239, 249, 249), Color.FromArgb(251, 255, 255), Color.FromArgb(226, 244, 244), Color.FromArgb(211, 236, 236),
                        Color.FromArgb(190, 220, 219), Color.FromArgb(126, 176, 174), Color.FromArgb(20, 58, 58), Color.FromArgb(75, 112, 111),
                        Color.FromArgb(0, 128, 128), Color.FromArgb(0, 153, 153), Color.FromArgb(0, 102, 102), Color.FromArgb(207, 239, 238), Color.FromArgb(40, 24, 72, 72),
                        "Segoe UI", 9F, 22F, 10, 12, 36, 42, 40, 4, 9, 18, 22, 1, 2, 145, 95, 195);
                case ZarpaThemePreset.EmeraldForest:
                    return Create(Color.FromArgb(242, 248, 243), Color.FromArgb(252, 255, 252), Color.FromArgb(229, 241, 231), Color.FromArgb(213, 232, 217),
                        Color.FromArgb(199, 220, 202), Color.FromArgb(144, 179, 151), Color.FromArgb(28, 58, 34), Color.FromArgb(88, 117, 93),
                        Color.FromArgb(35, 126, 72), Color.FromArgb(49, 151, 88), Color.FromArgb(25, 98, 55), Color.FromArgb(216, 239, 222), Color.FromArgb(38, 35, 70, 42),
                        "Segoe UI", 9F, 22F, 7, 9, 35, 41, 39, 4, 8, 17, 22, 1, 2, 135, 90, 185);
                case ZarpaThemePreset.RoseQuartz:
                    return Create(Color.FromArgb(253, 247, 249), Color.FromArgb(255, 252, 253), Color.FromArgb(249, 234, 240), Color.FromArgb(244, 218, 229),
                        Color.FromArgb(235, 205, 217), Color.FromArgb(202, 151, 171), Color.FromArgb(75, 38, 53), Color.FromArgb(132, 89, 106),
                        Color.FromArgb(190, 65, 112), Color.FromArgb(214, 88, 135), Color.FromArgb(153, 45, 88), Color.FromArgb(248, 218, 230), Color.FromArgb(36, 85, 38, 58),
                        "Segoe UI", 9F, 23F, 12, 14, 37, 43, 41, 5, 10, 20, 23, 1, 2, 165, 105, 215);
                case ZarpaThemePreset.LavenderMist:
                    return Create(Color.FromArgb(248, 247, 254), Color.FromArgb(254, 253, 255), Color.FromArgb(238, 235, 250), Color.FromArgb(226, 220, 245),
                        Color.FromArgb(215, 209, 235), Color.FromArgb(168, 157, 204), Color.FromArgb(47, 40, 72), Color.FromArgb(105, 96, 134),
                        Color.FromArgb(111, 78, 190), Color.FromArgb(132, 100, 210), Color.FromArgb(86, 58, 159), Color.FromArgb(231, 224, 250), Color.FromArgb(38, 57, 44, 88),
                        "Segoe UI", 9F, 22F, 9, 12, 36, 42, 40, 4, 9, 18, 22, 1, 3, 155, 100, 205);
                case ZarpaThemePreset.MidnightNavy:
                    return Create(Color.FromArgb(9, 18, 33), Color.FromArgb(15, 29, 49), Color.FromArgb(23, 40, 64), Color.FromArgb(31, 50, 76),
                        Color.FromArgb(48, 68, 94), Color.FromArgb(75, 97, 126), Color.FromArgb(238, 245, 255), Color.FromArgb(149, 166, 190),
                        Color.FromArgb(59, 130, 246), Color.FromArgb(96, 165, 250), Color.FromArgb(37, 99, 210), Color.FromArgb(27, 57, 96), Color.FromArgb(115, 0, 0, 0),
                        "Segoe UI", 9F, 22F, 6, 8, 34, 40, 38, 4, 8, 16, 22, 1, 3, 125, 85, 175);
                case ZarpaThemePreset.NordFrost:
                    return Create(Color.FromArgb(35, 42, 52), Color.FromArgb(46, 54, 66), Color.FromArgb(57, 66, 80), Color.FromArgb(68, 78, 94),
                        Color.FromArgb(76, 86, 102), Color.FromArgb(105, 116, 134), Color.FromArgb(236, 239, 244), Color.FromArgb(171, 180, 194),
                        Color.FromArgb(136, 192, 208), Color.FromArgb(163, 214, 226), Color.FromArgb(103, 166, 185), Color.FromArgb(64, 87, 102), Color.FromArgb(100, 0, 0, 0),
                        "Segoe UI", 9F, 22F, 8, 10, 35, 41, 39, 4, 9, 18, 22, 1, 2, 145, 95, 195);
                case ZarpaThemePreset.Aubergine:
                    return Create(Color.FromArgb(28, 17, 32), Color.FromArgb(42, 26, 47), Color.FromArgb(55, 36, 61), Color.FromArgb(69, 47, 75),
                        Color.FromArgb(82, 59, 88), Color.FromArgb(113, 84, 120), Color.FromArgb(249, 241, 250), Color.FromArgb(190, 166, 193),
                        Color.FromArgb(211, 107, 170), Color.FromArgb(229, 137, 192), Color.FromArgb(174, 76, 137), Color.FromArgb(81, 49, 76), Color.FromArgb(110, 0, 0, 0),
                        "Segoe UI", 9F, 23F, 10, 12, 36, 42, 40, 5, 10, 19, 23, 1, 3, 155, 100, 205);
                case ZarpaThemePreset.Marlboro:
                    return Create(Color.FromArgb(246, 245, 242), Color.FromArgb(255, 255, 253), Color.FromArgb(247, 236, 234), Color.FromArgb(239, 218, 215),
                        Color.FromArgb(222, 205, 202), Color.FromArgb(177, 148, 145), Color.FromArgb(28, 27, 25), Color.FromArgb(104, 98, 93),
                        Color.FromArgb(205, 24, 35), Color.FromArgb(230, 45, 54), Color.FromArgb(161, 14, 24), Color.FromArgb(250, 216, 218), Color.FromArgb(48, 55, 20, 20),
                        "Segoe UI", 9F, 23F, 4, 6, 36, 42, 40, 4, 9, 18, 22, 1, 2, 125, 85, 175);
                default:
                    return Create(Color.FromArgb(247, 248, 252), Color.White, Color.FromArgb(242, 244, 250), Color.FromArgb(234, 236, 246),
                        Color.FromArgb(224, 227, 238), Color.FromArgb(188, 193, 211), Color.FromArgb(28, 29, 36), Color.FromArgb(103, 107, 124),
                        Color.FromArgb(91, 80, 225), Color.FromArgb(111, 99, 239), Color.FromArgb(67, 56, 202), Color.FromArgb(237, 235, 255), Color.FromArgb(42, 40, 48, 80),
                        "Segoe UI", 9F, 22F, 8, 10, 34, 40, 38, 4, 8, 16, 22, 1, 2, 140, 95, 185);
            }
        }

        internal static bool IsCatalogPreset(ZarpaThemePreset preset)
        {
            return preset == ZarpaThemePreset.ZarpaLight || preset == ZarpaThemePreset.ZarpaDark ||
                preset == ZarpaThemePreset.MicaBlue || preset == ZarpaThemePreset.WarmSand ||
                preset == ZarpaThemePreset.OceanTeal || preset == ZarpaThemePreset.EmeraldForest ||
                preset == ZarpaThemePreset.RoseQuartz || preset == ZarpaThemePreset.LavenderMist ||
                preset == ZarpaThemePreset.MidnightNavy || preset == ZarpaThemePreset.NordFrost ||
                preset == ZarpaThemePreset.Aubergine || preset == ZarpaThemePreset.Marlboro;
        }

        private static ZarpaPresetDefinition Create(Color canvas, Color surface, Color raised, Color overlay,
            Color border, Color borderStrong, Color text, Color muted, Color accent, Color accentHover,
            Color accentPressed, Color selection, Color shadow, string font, float fontSize, float headingSize,
            int radius, int groupRadius, int controlHeight, int headerHeight, int tabHeight,
            int spacingSmall, int spacingMedium, int spacingLarge, int iconSize, int borderThickness,
            int shadowDepth, int hoverDuration, int pressDuration, int tabDuration)
        {
            return new ZarpaPresetDefinition { Canvas = canvas, Surface = surface, Raised = raised, Overlay = overlay,
                Border = border, BorderStrong = borderStrong, Text = text, Muted = muted, Accent = accent,
                AccentHover = accentHover, AccentPressed = accentPressed, Selection = selection, Shadow = shadow,
                FontFamily = font, FontSize = fontSize, HeadingFontSize = headingSize, CornerRadius = radius,
                GroupCornerRadius = groupRadius, ControlHeight = controlHeight, HeaderHeight = headerHeight,
                TabHeight = tabHeight, SpacingSmall = spacingSmall, SpacingMedium = spacingMedium,
                SpacingLarge = spacingLarge, IconSize = iconSize, BorderThickness = borderThickness,
                ShadowDepth = shadowDepth, HoverDuration = hoverDuration, PressDuration = pressDuration,
                TabDuration = tabDuration };
        }
    }

    public interface IZarpaThemeAware
    {
        void ApplyTheme(ZarpaThemeTokens theme);
    }

    /// <summary>
    /// Marks a composite control that owns the appearance of its private child controls.
    /// Theme traversal stops at this boundary so native editors are not restyled twice.
    /// </summary>
    public interface IZarpaThemeBoundary { }

    [TypeConverter(typeof(ExpandableObjectConverter))]
    public sealed class ZarpaThemeTokens
    {
        private readonly Action changed;
        private ZarpaThemePreset preset;
        private Color canvas, surface, surfaceRaised, surfaceOverlay;
        private Color border, borderStrong, text, textMuted;
        private Color accent, accentHover, accentPressed, selection;
        private Color success, warning, danger, information, shadow;
        private string fontFamily;
        private float fontSize, headingFontSize;
        private int spacingSmall, spacingMedium, spacingLarge, controlHeight, cornerRadius, groupCornerRadius;
        private int headerHeight, tabHeight, iconSize, borderThickness, shadowDepth;
        private int hoverDuration, pressDuration, tabDuration;
        private bool motionEnabled;

        internal ZarpaThemeTokens(Action changedCallback)
        {
            changed = changedCallback;
            ApplyPreset(ZarpaThemePreset.ZarpaLight);
        }

        [Category("Tema"), DefaultValue(ZarpaThemePreset.ZarpaLight)]
        public ZarpaThemePreset Preset { get { return preset; } set { ApplyPreset(value); } }

        [Category("Superficies")]
        public Color Canvas { get { return canvas; } set { canvas = value; Changed(); } }
        [Category("Superficies")]
        public Color Surface { get { return surface; } set { surface = value; Changed(); } }
        [Category("Superficies")]
        public Color SurfaceRaised { get { return surfaceRaised; } set { surfaceRaised = value; Changed(); } }
        [Category("Superficies")]
        public Color SurfaceOverlay { get { return surfaceOverlay; } set { surfaceOverlay = value; Changed(); } }

        [Category("Bordes")]
        public Color Border { get { return border; } set { border = value; Changed(); } }
        [Category("Bordes")]
        public Color BorderStrong { get { return borderStrong; } set { borderStrong = value; Changed(); } }

        [Category("Texto")]
        public Color Text { get { return text; } set { text = value; Changed(); } }
        [Category("Texto")]
        public Color TextMuted { get { return textMuted; } set { textMuted = value; Changed(); } }

        [Category("Acento")]
        public Color Accent { get { return accent; } set { accent = value; Changed(); } }
        [Category("Acento")]
        public Color AccentHover { get { return accentHover; } set { accentHover = value; Changed(); } }
        [Category("Acento")]
        public Color AccentPressed { get { return accentPressed; } set { accentPressed = value; Changed(); } }
        [Category("Acento")]
        public Color Selection { get { return selection; } set { selection = value; Changed(); } }

        [Category("Semántica")]
        public Color Success { get { return success; } set { success = value; Changed(); } }
        [Category("Semántica")]
        public Color Warning { get { return warning; } set { warning = value; Changed(); } }
        [Category("Semántica")]
        public Color Danger { get { return danger; } set { danger = value; Changed(); } }
        [Category("Semántica")]
        public Color Information { get { return information; } set { information = value; Changed(); } }
        [Category("Semántica")]
        public Color Shadow { get { return shadow; } set { shadow = value; Changed(); } }

        [Category("Tipografía"), DefaultValue("Segoe UI")]
        public string FontFamily { get { return fontFamily; } set { fontFamily = string.IsNullOrEmpty(value) ? "Segoe UI" : value; Changed(); } }
        [Category("Tipografía"), DefaultValue(9F)]
        public float FontSize { get { return fontSize; } set { fontSize = Clamp(value, 7F, 18F); Changed(); } }
        [Category("Tipografía"), DefaultValue(22F)]
        public float HeadingFontSize { get { return headingFontSize; } set { headingFontSize = Clamp(value, 12F, 40F); Changed(); } }

        [Category("Métricas"), DefaultValue(4)]
        public int SpacingSmall { get { return spacingSmall; } set { spacingSmall = Clamp(value, 2, 12); Changed(); } }
        [Category("Métricas"), DefaultValue(8)]
        public int SpacingMedium { get { return spacingMedium; } set { spacingMedium = Clamp(value, 4, 24); Changed(); } }
        [Category("Métricas"), DefaultValue(16)]
        public int SpacingLarge { get { return spacingLarge; } set { spacingLarge = Clamp(value, 8, 40); Changed(); } }
        [Category("Métricas"), DefaultValue(34)]
        public int ControlHeight { get { return controlHeight; } set { controlHeight = Clamp(value, 24, 56); Changed(); } }
        [Category("Métricas"), DefaultValue(8)]
        public int CornerRadius { get { return cornerRadius; } set { cornerRadius = Clamp(value, 0, 16); Changed(); } }
        [Category("Métricas"), DefaultValue(10)]
        public int GroupCornerRadius { get { return groupCornerRadius; } set { groupCornerRadius = Clamp(value, 0, 20); Changed(); } }
        [Category("Métricas"), DefaultValue(40)]
        public int HeaderHeight { get { return headerHeight; } set { headerHeight = Clamp(value, 32, 64); Changed(); } }
        [Category("Métricas"), DefaultValue(38)]
        public int TabHeight { get { return tabHeight; } set { tabHeight = Clamp(value, 30, 52); Changed(); } }
        [Category("Métricas"), DefaultValue(22)]
        public int IconSize { get { return iconSize; } set { iconSize = Clamp(value, 16, 32); Changed(); } }
        [Category("Métricas"), DefaultValue(1)]
        public int BorderThickness { get { return borderThickness; } set { borderThickness = Clamp(value, 1, 3); Changed(); } }
        [Category("Métricas"), DefaultValue(2)]
        public int ShadowDepth { get { return shadowDepth; } set { shadowDepth = Clamp(value, 0, 6); Changed(); } }

        [Category("Movimiento"), DefaultValue(true)]
        public bool MotionEnabled { get { return motionEnabled; } set { motionEnabled = value; Changed(); } }
        [Category("Movimiento"), DefaultValue(140)]
        public int HoverDuration { get { return hoverDuration; } set { hoverDuration = Clamp(value, 50, 500); Changed(); } }
        [Category("Movimiento"), DefaultValue(95)]
        public int PressDuration { get { return pressDuration; } set { pressDuration = Clamp(value, 40, 400); Changed(); } }
        [Category("Movimiento"), DefaultValue(185)]
        public int TabDuration { get { return tabDuration; } set { tabDuration = Clamp(value, 80, 600); Changed(); } }

        public void ApplyPreset(ZarpaThemePreset value)
        {
            preset = value;
            switch (value)
            {
                case ZarpaThemePreset.ZarpaLight:
                case ZarpaThemePreset.ZarpaDark:
                case ZarpaThemePreset.MicaBlue:
                case ZarpaThemePreset.WarmSand:
                case ZarpaThemePreset.OceanTeal:
                case ZarpaThemePreset.EmeraldForest:
                case ZarpaThemePreset.RoseQuartz:
                case ZarpaThemePreset.LavenderMist:
                case ZarpaThemePreset.MidnightNavy:
                case ZarpaThemePreset.NordFrost:
                case ZarpaThemePreset.Aubergine:
                case ZarpaThemePreset.Marlboro:
                    ApplySharedPreset(ZarpaPresetCatalog.Get(value));
                    break;
                case ZarpaThemePreset.Graphite:
                    SetPalette(Color.FromArgb(34, 36, 41), Color.FromArgb(44, 47, 53), Color.FromArgb(53, 56, 63), Color.FromArgb(62, 66, 74),
                        Color.FromArgb(78, 82, 92), Color.FromArgb(101, 106, 118), Color.FromArgb(246, 247, 249), Color.FromArgb(176, 181, 190),
                        Color.FromArgb(139, 121, 230), Color.FromArgb(164, 148, 242), Color.FromArgb(108, 88, 207), Color.FromArgb(70, 64, 101), Color.FromArgb(100, 0, 0, 0));
                    break;
                case ZarpaThemePreset.HighContrast:
                    SetPalette(Color.Black, Color.Black, Color.FromArgb(18, 18, 18), Color.FromArgb(28, 28, 28),
                        Color.White, Color.White, Color.White, Color.White, Color.Cyan, Color.Cyan, Color.DeepSkyBlue,
                        Color.FromArgb(0, 55, 90), Color.Black);
                    break;
                case ZarpaThemePreset.Custom:
                    Changed();
                    return;
                default:
                    ApplySharedPreset(ZarpaPresetCatalog.Get(ZarpaThemePreset.ZarpaLight));
                    break;
            }
            success = Color.FromArgb(21, 128, 88);
            warning = Color.FromArgb(190, 105, 8);
            danger = Color.FromArgb(197, 48, 48);
            information = Color.FromArgb(2, 119, 189);
            ZarpaPresetDefinition shared = ZarpaPresetCatalog.IsCatalogPreset(value) ? ZarpaPresetCatalog.Get(value) : null;
            fontFamily = shared == null ? "Segoe UI" : shared.FontFamily;
            fontSize = shared == null ? 9F : shared.FontSize;
            headingFontSize = shared == null ? 22F : shared.HeadingFontSize;
            spacingSmall = shared == null ? 4 : shared.SpacingSmall;
            spacingMedium = shared == null ? 8 : shared.SpacingMedium;
            spacingLarge = shared == null ? 16 : shared.SpacingLarge;
            controlHeight = shared == null ? 32 : shared.ControlHeight;
            cornerRadius = value == ZarpaThemePreset.HighContrast ? 0 : shared == null ? 6 : shared.CornerRadius;
            groupCornerRadius = value == ZarpaThemePreset.HighContrast ? 0 : shared == null ? 8 : shared.GroupCornerRadius;
            headerHeight = shared == null ? 40 : shared.HeaderHeight;
            tabHeight = shared == null ? 38 : shared.TabHeight;
            iconSize = shared == null ? 22 : shared.IconSize;
            borderThickness = shared == null ? 1 : shared.BorderThickness;
            shadowDepth = value == ZarpaThemePreset.HighContrast ? 0 : shared == null ? 2 : shared.ShadowDepth;
            motionEnabled = value != ZarpaThemePreset.HighContrast;
            hoverDuration = shared == null ? 130 : shared.HoverDuration;
            pressDuration = shared == null ? 90 : shared.PressDuration;
            tabDuration = shared == null ? 180 : shared.TabDuration;
            Changed();
        }

        private void SetPalette(Color c, Color s, Color raised, Color overlay, Color b, Color bs,
            Color t, Color tm, Color a, Color ah, Color ap, Color sel, Color sh)
        {
            canvas = c; surface = s; surfaceRaised = raised; surfaceOverlay = overlay;
            border = b; borderStrong = bs; text = t; textMuted = tm;
            accent = a; accentHover = ah; accentPressed = ap; selection = sel; shadow = sh;
        }

        private void ApplySharedPreset(ZarpaPresetDefinition value)
        {
            SetPalette(value.Canvas, value.Surface, value.Raised, value.Overlay, value.Border,
                value.BorderStrong, value.Text, value.Muted, value.Accent, value.AccentHover,
                value.AccentPressed, value.Selection, value.Shadow);
        }

        private void Changed() { if (changed != null) changed(); }
        private static int Clamp(int value, int min, int max) { return value < min ? min : value > max ? max : value; }
        private static float Clamp(float value, float min, float max) { return value < min ? min : value > max ? max : value; }
        public override string ToString() { return preset + " · Zarpa Design System"; }
    }

    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(Timer))]
    [DefaultProperty("Preset")]
    [Designer("ZarpaSuite.Controls.Design.ZarpaThemeManagerDesigner, Zarpa.Controls")]
    public sealed class ZarpaThemeManager : Component
    {
        private readonly ZarpaThemeTokens theme;
        private readonly List<Control> roots = new List<Control>();
        private bool autoApply = true;
        private bool applyThemeFontToNativeControls;
        private Control rootControl;

        public ZarpaThemeManager() { theme = new ZarpaThemeTokens(OnThemeChanged); }
        public ZarpaThemeManager(IContainer container) : this() { if (container != null) container.Add(this); }

        [Category("Tema"), DefaultValue(ZarpaThemePreset.ZarpaLight)]
        public ZarpaThemePreset Preset { get { return theme.Preset; } set { theme.Preset = value; } }

        [Category("Tema")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
        public ZarpaThemeTokens Theme { get { return theme; } }

        [Category("Tipografía"), DefaultValue("Segoe UI")]
        public string FontFamily { get { return theme.FontFamily; } set { theme.FontFamily = value; } }

        [Category("Tipografía"), DefaultValue(9F)]
        public float FontSize { get { return theme.FontSize; } set { theme.FontSize = value; } }

        [Category("Tipografía"), DefaultValue(22F)]
        public float HeadingFontSize { get { return theme.HeadingFontSize; } set { theme.HeadingFontSize = value; } }

        [Category("Comportamiento"), DefaultValue(true)]
        public bool AutoApply { get { return autoApply; } set { autoApply = value; if (value) Apply(); } }

        [Category("Tipografía"), DefaultValue(false)]
        public bool ApplyThemeFontToNativeControls
        {
            get { return applyThemeFontToNativeControls; }
            set { if (applyThemeFontToNativeControls == value) return; applyThemeFontToNativeControls = value; Apply(); }
        }

        [Category("Comportamiento"), DefaultValue(null)]
        public Control RootControl
        {
            get { return rootControl; }
            set
            {
                if (ReferenceEquals(rootControl, value)) return;
                Control previous = rootControl;
                rootControl = value;
                if (previous != null) Detach(previous);
                if (rootControl != null) Attach(rootControl);
            }
        }

        public event EventHandler ThemeChanged;

        public void Attach(Control root)
        {
            if (root == null || roots.Contains(root)) return;
            roots.Add(root);
            root.Disposed += RootDisposed;
            HookTree(root);
            ApplyTo(root);
        }

        public void Detach(Control root)
        {
            if (root == null || !roots.Remove(root)) return;
            root.Disposed -= RootDisposed;
            UnhookTree(root);
        }

        public void Apply()
        {
            foreach (Control root in roots.ToArray())
                if (!root.IsDisposed) ApplyTo(root);
        }

        public void ApplyTo(Control root)
        {
            if (root == null) return;
            ApplyRecursive(root);
            root.Invalidate(true);
        }

        public static int Scale(Control control, int logicalPixels)
        {
            return control == null ? logicalPixels : ZarpaDpiScale.FromControl(control).X(logicalPixels);
        }

        private void ApplyRecursive(Control control)
        {
            IZarpaThemeAware themed = control as IZarpaThemeAware;
            if (themed != null)
                themed.ApplyTheme(theme);
            else
            {
                control.BackColor = theme.Canvas;
                control.ForeColor = theme.Text;
                PropertyDescriptor fontProperty = TypeDescriptor.GetProperties(control)["Font"];
                bool explicitFont = fontProperty != null && fontProperty.ShouldSerializeValue(control);
                if (applyThemeFontToNativeControls || !explicitFont && !(control is TextBoxBase) && !(control is ComboBox))
                    control.Font = new Font(theme.FontFamily, theme.FontSize, control.Font.Style);
            }
            if (!(control is IZarpaThemeBoundary))
                foreach (Control child in control.Controls) ApplyRecursive(child);
        }

        private void HookTree(Control control)
        {
            if (control == null || control is IZarpaThemeBoundary) return;
            control.ControlAdded -= ControlAdded;
            control.ControlRemoved -= ControlRemoved;
            control.ControlAdded += ControlAdded;
            control.ControlRemoved += ControlRemoved;
            foreach (Control child in control.Controls) HookTree(child);
        }

        private void UnhookTree(Control control)
        {
            if (control == null || control is IZarpaThemeBoundary) return;
            control.ControlAdded -= ControlAdded;
            control.ControlRemoved -= ControlRemoved;
            foreach (Control child in control.Controls) UnhookTree(child);
        }

        private void ControlAdded(object sender, ControlEventArgs e)
        {
            if (e.Control == null) return;
            HookTree(e.Control);
            ApplyRecursive(e.Control);
            e.Control.Invalidate(true);
        }

        private void ControlRemoved(object sender, ControlEventArgs e)
        {
            if (e.Control != null) UnhookTree(e.Control);
        }

        private void OnThemeChanged()
        {
            if (autoApply) Apply();
            if (ThemeChanged != null) ThemeChanged(this, EventArgs.Empty);
        }

        private void RootDisposed(object sender, EventArgs e)
        {
            Control root = sender as Control;
            Detach(root);
            if (ReferenceEquals(rootControl, root)) rootControl = null;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (Control root in roots.ToArray()) Detach(root);
                rootControl = null;
            }
            base.Dispose(disposing);
        }
    }

    internal static class ZarpaPaint
    {
        internal static Color Blend(Color from, Color to, float amount)
        {
            amount = amount < 0F ? 0F : amount > 1F ? 1F : amount;
            return Color.FromArgb((int)(from.A + (to.A - from.A) * amount),
                (int)(from.R + (to.R - from.R) * amount),
                (int)(from.G + (to.G - from.G) * amount),
                (int)(from.B + (to.B - from.B) * amount));
        }

        internal static void FillRounded(Graphics graphics, Color color, Rectangle bounds, int radius)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            if (radius <= 0) { using (SolidBrush brush = new SolidBrush(color)) graphics.FillRectangle(brush, bounds); return; }
            GraphicsState state = graphics.Save();
            try
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                using (GraphicsPath path = RoundedPath(bounds, radius))
                using (SolidBrush brush = new SolidBrush(color)) graphics.FillPath(brush, path);
            }
            finally { graphics.Restore(state); }
        }

        internal static void DrawRounded(Graphics graphics, Color color, Rectangle bounds, int radius, int thickness)
        {
            DrawRounded(graphics, color, bounds, radius, (float)thickness);
        }

        internal static void DrawRounded(Graphics graphics, Color color, Rectangle bounds, int radius, float thickness)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            GraphicsState state = graphics.Save();
            try
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float width = Math.Max(1F, thickness);
                float inset = width / 2F;
                RectangleF strokeBounds = new RectangleF(bounds.Left + inset, bounds.Top + inset,
                    Math.Max(1F, bounds.Width - width), Math.Max(1F, bounds.Height - width));
                using (GraphicsPath path = RoundedPath(strokeBounds, Math.Max(1F, radius - inset)))
                using (Pen pen = new Pen(color, width)) graphics.DrawPath(pen, path);
            }
            finally { graphics.Restore(state); }
        }

        internal static Color EffectiveBackColor(Control parent)
        {
            Control current = parent;
            while (current != null)
            {
                if (current.BackColor != Color.Transparent) return current.BackColor;
                current = current.Parent;
            }
            return SystemColors.Control;
        }

        internal static GraphicsPath RoundedPath(Rectangle bounds, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            int diameter = Math.Min(Math.Min(bounds.Width, bounds.Height), Math.Max(1, radius * 2));
            Rectangle arc = new Rectangle(bounds.Left, bounds.Top, diameter, diameter);
            path.AddArc(arc, 180, 90); arc.X = bounds.Right - diameter; path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter; path.AddArc(arc, 0, 90); arc.X = bounds.Left; path.AddArc(arc, 90, 90);
            path.CloseFigure(); return path;
        }

        private static GraphicsPath RoundedPath(RectangleF bounds, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            float diameter = Math.Min(Math.Min(bounds.Width, bounds.Height), Math.Max(1F, radius * 2F));
            RectangleF arc = new RectangleF(bounds.Left, bounds.Top, diameter, diameter);
            path.AddArc(arc, 180, 90); arc.X = bounds.Right - diameter; path.AddArc(arc, 270, 90);
            arc.Y = bounds.Bottom - diameter; path.AddArc(arc, 0, 90); arc.X = bounds.Left; path.AddArc(arc, 90, 90);
            path.CloseFigure(); return path;
        }
    }
}

