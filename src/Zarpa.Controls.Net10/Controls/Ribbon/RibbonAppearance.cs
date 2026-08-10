using System.ComponentModel;
using System.Drawing;

namespace ZarpaSuite.Controls
{
    public enum RibbonThemePreset
    {
        ZarpaLight,
        ZarpaDark,
        MicaBlue,
        Graphite,
        WarmSand,
        HighContrast,
        Custom
    }

    public enum RibbonTabAnimation
    {
        None,
        Slide,
        FluentStretch,
        Fade
    }

    [TypeConverter(typeof(ExpandableObjectConverter))]
    public sealed class RibbonAppearance
    {
        private readonly RibbonControl owner;
        private RibbonThemePreset preset;
        private Color canvasColor, surfaceColor, raisedColor, groupSurfaceColor;
        private Color headerSurfaceColor, tabStripColor, headerTextColor;
        private Color hoverColor, pressedColor, selectionColor;
        private Color borderColor, strongBorderColor, groupBorderColor, shadowColor;
        private Color textColor, mutedTextColor;
        private Color accentColor, accentHoverColor, accentPressedColor, accentGlowColor;
        private Color successColor, warningColor, dangerColor, informationColor;
        private int cornerRadius, groupCornerRadius, shadowDepth;
        private int headerHeight, tabHeight, contentPadding, itemSpacing, iconSize, borderThickness;
        private string fontFamily;
        private float fontSize;
        private int tabAnimationDuration;
        private int hoverAnimationDuration, pressAnimationDuration, rippleAnimationDuration;
        private bool showGroupCards, motionEnabled;
        private bool enableRipples, animateBadges;
        private RibbonTabAnimation tabAnimation;

        internal RibbonAppearance(RibbonControl owner)
        {
            this.owner = owner;
            ApplyPreset(RibbonThemePreset.ZarpaLight);
        }

        [Category("Tema"), DefaultValue(RibbonThemePreset.ZarpaLight)]
        public RibbonThemePreset Preset
        {
            get { return preset; }
            set { ApplyPreset(value); }
        }

        [Category("Superficies")]
        public Color CanvasColor { get { return canvasColor; } set { canvasColor = value; Changed(); } }
        [Category("Superficies")]
        public Color SurfaceColor { get { return surfaceColor; } set { surfaceColor = value; Changed(); } }
        [Category("Superficies")]
        public Color RaisedColor { get { return raisedColor; } set { raisedColor = value; Changed(); } }
        [Category("Superficies")]
        public Color GroupSurfaceColor { get { return groupSurfaceColor; } set { groupSurfaceColor = value; Changed(); } }
        [Category("Cabecera")]
        public Color HeaderSurfaceColor { get { return headerSurfaceColor; } set { headerSurfaceColor = value; Changed(); } }
        [Category("Cabecera")]
        public Color TabStripColor { get { return tabStripColor; } set { tabStripColor = value; Changed(); } }
        [Category("Cabecera")]
        public Color HeaderTextColor { get { return headerTextColor; } set { headerTextColor = value; Changed(); } }

        [Category("Estados")]
        public Color HoverColor { get { return hoverColor; } set { hoverColor = value; Changed(); } }
        [Category("Estados")]
        public Color PressedColor { get { return pressedColor; } set { pressedColor = value; Changed(); } }
        [Category("Estados")]
        public Color SelectionColor { get { return selectionColor; } set { selectionColor = value; Changed(); } }

        [Category("Bordes")]
        public Color BorderColor { get { return borderColor; } set { borderColor = value; Changed(); } }
        [Category("Bordes")]
        public Color StrongBorderColor { get { return strongBorderColor; } set { strongBorderColor = value; Changed(); } }
        [Category("Bordes")]
        public Color GroupBorderColor { get { return groupBorderColor; } set { groupBorderColor = value; Changed(); } }
        [Category("Bordes")]
        public Color ShadowColor { get { return shadowColor; } set { shadowColor = value; Changed(); } }

        [Category("Texto")]
        public Color TextColor { get { return textColor; } set { textColor = value; Changed(); } }
        [Category("Texto")]
        public Color MutedTextColor { get { return mutedTextColor; } set { mutedTextColor = value; Changed(); } }
        [Category("Tipografía"), DefaultValue("Segoe UI")]
        public string FontFamily { get { return fontFamily; } set { fontFamily = string.IsNullOrEmpty(value) ? "Segoe UI" : value; Changed(); } }
        [Category("Tipografía"), DefaultValue(9F)]
        public float FontSize { get { return fontSize; } set { fontSize = value < 7F ? 7F : value > 16F ? 16F : value; Changed(); } }

        [Category("Acento")]
        public Color AccentColor { get { return accentColor; } set { accentColor = value; Changed(); } }
        [Category("Acento")]
        public Color AccentHoverColor { get { return accentHoverColor; } set { accentHoverColor = value; Changed(); } }
        [Category("Acento")]
        public Color AccentPressedColor { get { return accentPressedColor; } set { accentPressedColor = value; Changed(); } }
        [Category("Acento")]
        public Color AccentGlowColor { get { return accentGlowColor; } set { accentGlowColor = value; Changed(); } }

        [Category("Semántica")]
        public Color SuccessColor { get { return successColor; } set { successColor = value; Changed(); } }
        [Category("Semántica")]
        public Color WarningColor { get { return warningColor; } set { warningColor = value; Changed(); } }
        [Category("Semántica")]
        public Color DangerColor { get { return dangerColor; } set { dangerColor = value; Changed(); } }
        [Category("Semántica")]
        public Color InformationColor { get { return informationColor; } set { informationColor = value; Changed(); } }

        [Category("Geometría"), DefaultValue(8)]
        public int CornerRadius
        {
            get { return cornerRadius; }
            set { cornerRadius = Clamp(value, 0, 18); Changed(); }
        }

        [Category("Geometría"), DefaultValue(10)]
        public int GroupCornerRadius
        {
            get { return groupCornerRadius; }
            set { groupCornerRadius = Clamp(value, 0, 20); Changed(); }
        }

        [Category("Geometría"), DefaultValue(2)]
        public int ShadowDepth
        {
            get { return shadowDepth; }
            set { shadowDepth = Clamp(value, 0, 6); Changed(); }
        }

        [Category("Geometría"), DefaultValue(40)]
        public int HeaderHeight { get { return headerHeight; } set { headerHeight = Clamp(value, 32, 64); Changed(); } }
        [Category("Geometría"), DefaultValue(38)]
        public int TabHeight { get { return tabHeight; } set { tabHeight = Clamp(value, 30, 52); Changed(); } }
        [Category("Geometría"), DefaultValue(8)]
        public int ContentPadding { get { return contentPadding; } set { contentPadding = Clamp(value, 4, 20); Changed(); } }
        [Category("Geometría"), DefaultValue(4)]
        public int ItemSpacing { get { return itemSpacing; } set { itemSpacing = Clamp(value, 2, 12); Changed(); } }
        [Category("Geometría"), DefaultValue(22)]
        public int IconSize { get { return iconSize; } set { iconSize = Clamp(value, 16, 32); Changed(); } }
        [Category("Geometría"), DefaultValue(1)]
        public int BorderThickness { get { return borderThickness; } set { borderThickness = Clamp(value, 1, 3); Changed(); } }

        [Category("Estilo"), DefaultValue(true)]
        public bool ShowGroupCards { get { return showGroupCards; } set { showGroupCards = value; Changed(); } }

        [Category("Movimiento"), DefaultValue(true)]
        public bool MotionEnabled { get { return motionEnabled; } set { motionEnabled = value; Changed(); } }

        [Category("Movimiento"), DefaultValue(RibbonTabAnimation.FluentStretch)]
        public RibbonTabAnimation TabAnimation
        {
            get { return tabAnimation; }
            set { tabAnimation = value; Changed(); }
        }

        [Category("Movimiento"), DefaultValue(185)]
        public int TabAnimationDuration
        {
            get { return tabAnimationDuration; }
            set { tabAnimationDuration = Clamp(value, 80, 600); Changed(); }
        }

        [Category("Movimiento"), DefaultValue(140)]
        public int HoverAnimationDuration
        {
            get { return hoverAnimationDuration; }
            set { hoverAnimationDuration = Clamp(value, 60, 500); Changed(); }
        }

        [Category("Movimiento"), DefaultValue(95)]
        public int PressAnimationDuration
        {
            get { return pressAnimationDuration; }
            set { pressAnimationDuration = Clamp(value, 40, 400); Changed(); }
        }

        [Category("Movimiento"), DefaultValue(320)]
        public int RippleAnimationDuration
        {
            get { return rippleAnimationDuration; }
            set { rippleAnimationDuration = Clamp(value, 120, 800); Changed(); }
        }

        [Category("Movimiento"), DefaultValue(true)]
        public bool EnableRipples { get { return enableRipples; } set { enableRipples = value; Changed(); } }

        [Category("Movimiento"), DefaultValue(true)]
        public bool AnimateBadges { get { return animateBadges; } set { animateBadges = value; Changed(); } }

        public void Reset() { ApplyPreset(RibbonThemePreset.ZarpaLight); }

        public void ApplyPreset(RibbonThemePreset value)
        {
            preset = value;
            switch (value)
            {
                case RibbonThemePreset.ZarpaDark:
                    ApplySharedPreset(ZarpaPresetCatalog.Get(ZarpaThemePreset.ZarpaDark));
                    break;
                case RibbonThemePreset.MicaBlue:
                    ApplySharedPreset(ZarpaPresetCatalog.Get(ZarpaThemePreset.MicaBlue));
                    break;
                case RibbonThemePreset.Graphite:
                    SetPalette(Color.FromArgb(35, 36, 40), Color.FromArgb(45, 46, 51), Color.FromArgb(54, 55, 61), Color.FromArgb(49, 50, 56),
                        Color.FromArgb(67, 68, 75), Color.FromArgb(78, 79, 87), Color.FromArgb(66, 63, 82), Color.FromArgb(73, 74, 81), Color.FromArgb(94, 95, 104),
                        Color.FromArgb(75, 76, 84), Color.FromArgb(85, 0, 0, 0), Color.FromArgb(245, 245, 247), Color.FromArgb(171, 172, 180),
                        Color.FromArgb(167, 139, 250), Color.FromArgb(196, 181, 253), Color.FromArgb(124, 58, 237), Color.FromArgb(75, 167, 139, 250));
                    break;
                case RibbonThemePreset.WarmSand:
                    ApplySharedPreset(ZarpaPresetCatalog.Get(ZarpaThemePreset.WarmSand));
                    break;
                case RibbonThemePreset.HighContrast:
                    SetPalette(Color.Black, Color.Black, Color.FromArgb(25, 25, 25), Color.Black, Color.FromArgb(35, 35, 35), Color.FromArgb(55, 55, 55),
                        Color.FromArgb(0, 45, 75), Color.White, Color.White, Color.White, Color.Black, Color.White, Color.White,
                        Color.FromArgb(0, 174, 255), Color.Cyan, Color.FromArgb(0, 120, 190), Color.Cyan);
                    break;
                case RibbonThemePreset.Custom:
                    Changed();
                    return;
                default:
                    ApplySharedPreset(ZarpaPresetCatalog.Get(ZarpaThemePreset.ZarpaLight));
                    break;
            }
            successColor = Color.FromArgb(16, 150, 104);
            warningColor = Color.FromArgb(217, 119, 6);
            dangerColor = Color.FromArgb(220, 38, 38);
            informationColor = Color.FromArgb(2, 132, 199);
            ZarpaPresetDefinition shared = value == RibbonThemePreset.ZarpaLight ? ZarpaPresetCatalog.Get(ZarpaThemePreset.ZarpaLight) :
                value == RibbonThemePreset.ZarpaDark ? ZarpaPresetCatalog.Get(ZarpaThemePreset.ZarpaDark) :
                value == RibbonThemePreset.MicaBlue ? ZarpaPresetCatalog.Get(ZarpaThemePreset.MicaBlue) :
                value == RibbonThemePreset.WarmSand ? ZarpaPresetCatalog.Get(ZarpaThemePreset.WarmSand) : null;
            cornerRadius = value == RibbonThemePreset.HighContrast ? 0 : shared == null ? 8 : shared.CornerRadius;
            groupCornerRadius = value == RibbonThemePreset.HighContrast ? 0 : shared == null ? 10 : shared.GroupCornerRadius;
            shadowDepth = value == RibbonThemePreset.HighContrast ? 0 : shared == null ? 2 : shared.ShadowDepth;
            headerHeight = shared == null ? 40 : shared.HeaderHeight;
            tabHeight = shared == null ? 38 : shared.TabHeight;
            contentPadding = shared == null ? 8 : shared.SpacingMedium;
            itemSpacing = shared == null ? 4 : shared.SpacingSmall;
            iconSize = shared == null ? 22 : shared.IconSize;
            borderThickness = shared == null ? 1 : shared.BorderThickness;
            fontFamily = shared == null ? "Segoe UI" : shared.FontFamily;
            fontSize = shared == null ? 9F : shared.FontSize;
            showGroupCards = true;
            motionEnabled = value != RibbonThemePreset.HighContrast;
            tabAnimation = value == RibbonThemePreset.HighContrast
                ? RibbonTabAnimation.None
                : RibbonTabAnimation.FluentStretch;
            tabAnimationDuration = shared == null ? 180 : shared.TabDuration;
            hoverAnimationDuration = shared == null ? 140 : shared.HoverDuration;
            pressAnimationDuration = shared == null ? 100 : shared.PressDuration;
            rippleAnimationDuration = 320;
            enableRipples = value != RibbonThemePreset.HighContrast;
            animateBadges = value != RibbonThemePreset.HighContrast;
            headerSurfaceColor = Mix(surfaceColor, accentColor,
                value == RibbonThemePreset.ZarpaDark || value == RibbonThemePreset.Graphite ? 0.10F : 0.045F);
            tabStripColor = Mix(surfaceColor, raisedColor, 0.42F);
            headerTextColor = textColor;
            Changed();
        }

        private void SetPalette(Color canvas, Color surface, Color raised, Color groupSurface,
            Color hover, Color pressed, Color selection, Color border, Color strongBorder,
            Color groupBorder, Color shadow, Color text, Color muted,
            Color accent, Color accentHover, Color accentPressed, Color glow)
        {
            canvasColor = canvas; surfaceColor = surface; raisedColor = raised; groupSurfaceColor = groupSurface;
            hoverColor = hover; pressedColor = pressed; selectionColor = selection;
            borderColor = border; strongBorderColor = strongBorder; groupBorderColor = groupBorder; shadowColor = shadow;
            textColor = text; mutedTextColor = muted;
            accentColor = accent; accentHoverColor = accentHover; accentPressedColor = accentPressed; accentGlowColor = glow;
        }

        private void ApplySharedPreset(ZarpaPresetDefinition value)
        {
            SetPalette(value.Canvas, value.Surface, value.Raised,
                Mix(value.Surface, value.Raised, 0.28F), value.Raised, value.Overlay,
                value.Selection, value.Border, value.BorderStrong, value.Border, value.Shadow,
                value.Text, value.Muted, value.Accent, value.AccentHover, value.AccentPressed,
                Color.FromArgb(72, value.Accent));
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            return value < minimum ? minimum : value > maximum ? maximum : value;
        }

        private static Color Mix(Color from, Color to, float amount)
        {
            amount = amount < 0F ? 0F : amount > 1F ? 1F : amount;
            return Color.FromArgb(
                (int)(from.A + (to.A - from.A) * amount),
                (int)(from.R + (to.R - from.R) * amount),
                (int)(from.G + (to.G - from.G) * amount),
                (int)(from.B + (to.B - from.B) * amount));
        }

        public override string ToString() { return preset + " · Zarpa Fluent"; }

        private void Changed()
        {
            if (owner != null) owner.AppearanceChanged();
        }
    }
}
