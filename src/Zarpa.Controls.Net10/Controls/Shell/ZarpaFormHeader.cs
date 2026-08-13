using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Design;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ZarpaSuite.Controls
{
    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(Label))]
    [DefaultProperty("TitleText")]
    public class ZarpaFormHeader : Control, IZarpaThemeAware
    {
        private ZarpaThemeTokens theme;
        private ZarpaDpiScale dpiScale = new ZarpaDpiScale(96, 96);
        private string eyebrowText = "ESPACIO DE TRABAJO";
        private string titleText = "Título";
        private string subtitleText = string.Empty;
        private string contextText = string.Empty;
        private string iconKey = "ic_fluent_apps_24_regular";

        public ZarpaFormHeader()
        {
            theme = new ZarpaThemeTokens(Invalidate);
            Dock = DockStyle.Top;
            Height = HeaderLogicalHeight;
            Font = new Font("Segoe UI", 9F);
            AccessibleRole = AccessibleRole.StaticText;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        }

        [Category("Contenido"), DefaultValue("ESPACIO DE TRABAJO")]
        public string EyebrowText
        {
            get { return eyebrowText; }
            set { eyebrowText = value ?? string.Empty; Invalidate(); }
        }

        [Category("Contenido"), DefaultValue("Título")]
        public string TitleText
        {
            get { return titleText; }
            set { titleText = value ?? string.Empty; AccessibleName = titleText; Invalidate(); }
        }

        [Category("Contenido"), DefaultValue("")]
        public string SubtitleText
        {
            get { return subtitleText; }
            set { subtitleText = value ?? string.Empty; Invalidate(); }
        }

        [Category("Contenido"), DefaultValue("")]
        public string ContextText
        {
            get { return contextText; }
            set { contextText = value ?? string.Empty; Invalidate(); }
        }

        [Category("Icono"), DefaultValue("ic_fluent_apps_24_regular")]
        [Editor("ZarpaSuite.Controls.Design.FluentIconPickerEditor, Zarpa.Controls", typeof(UITypeEditor))]
        public string IconKey
        {
            get { return iconKey; }
            set { iconKey = value ?? string.Empty; Invalidate(); }
        }

        public void ApplyTheme(ZarpaThemeTokens value)
        {
            if (value == null) return;
            theme = value;
            Font = new Font(theme.FontFamily, theme.FontSize);
            BackColor = theme.Surface;
            ForeColor = theme.Text;
            Height = S(HeaderLogicalHeight);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle bounds = ClientRectangle;
            if (bounds.Width <= 0 || bounds.Height <= 0) return;

            Color start = ZarpaPaint.Blend(theme.SurfaceRaised, theme.Selection, .28F);
            using (LinearGradientBrush background = new LinearGradientBrush(bounds, start, theme.Surface, 0F))
                e.Graphics.FillRectangle(background, bounds);
            using (SolidBrush accent = new SolidBrush(theme.Accent))
                e.Graphics.FillRectangle(accent, 0, 0, S(4), Height);
            using (Pen border = new Pen(theme.Border, dpiScale.Stroke(theme.BorderThickness)))
                e.Graphics.DrawLine(border, 0, Height - dpiScale.Stroke(1), Width, Height - dpiScale.Stroke(1));

            int horizontalInset = S(theme.SpacingLarge + theme.SpacingMedium);
            int iconSurfaceSize = S(ZarpaDensityMetrics.Select(theme, 38, 42, 48, 54));
            Rectangle iconSurface = new Rectangle(horizontalInset,
                (Height - iconSurfaceSize) / 2, iconSurfaceSize, iconSurfaceSize);
            ZarpaPaint.FillRounded(e.Graphics, theme.SurfaceOverlay, iconSurface, S(theme.GroupCornerRadius));
            ZarpaPaint.DrawRounded(e.Graphics, theme.BorderStrong, iconSurface, S(theme.GroupCornerRadius),
                dpiScale.Stroke(theme.BorderThickness));
            int iconSize = S(theme.IconSize);
            Rectangle iconBounds = new Rectangle(iconSurface.Left + (iconSurface.Width - iconSize) / 2,
                iconSurface.Top + (iconSurface.Height - iconSize) / 2, iconSize, iconSize);
            FluentIconCatalog.TryDraw(e.Graphics, iconKey, iconBounds, theme.Accent,
                dpiScale.X(Math.Max(16F, theme.IconSize - 2F)));

            int contextWidth = string.IsNullOrEmpty(contextText) ? 0 : Math.Min(S(390),
                TextRenderer.MeasureText(contextText, Font).Width + S(34));
            int textLeft = iconSurface.Right + S(theme.SpacingLarge);
            int textRight = Width - horizontalInset - (contextWidth > 0 ? contextWidth + S(theme.SpacingLarge) : 0);
            int textWidth = Math.Max(S(40), textRight - textLeft);
            int blockHeight = S(string.IsNullOrEmpty(subtitleText) ? 45 : 62);
            int blockTop = Math.Max(0, (Height - blockHeight) / 2);

            using (Font eyebrowFont = new Font(Font.FontFamily, Math.Max(7F, Font.Size - 1F), FontStyle.Bold))
                TextRenderer.DrawText(e.Graphics, eyebrowText.ToUpperInvariant(), eyebrowFont,
                    new Rectangle(textLeft, blockTop, textWidth, S(16)), theme.Accent,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            using (Font titleFont = new Font(Font.FontFamily, Math.Max(15F, theme.HeadingFontSize - 4F), FontStyle.Bold))
                TextRenderer.DrawText(e.Graphics, titleText, titleFont,
                    new Rectangle(textLeft, blockTop + S(15), textWidth, S(29)), theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(e.Graphics, subtitleText, Font,
                new Rectangle(textLeft, blockTop + S(43), textWidth, S(19)), theme.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            if (contextWidth > 0)
            {
                int contextHeight = S(theme.ControlHeight + theme.SpacingSmall);
                Rectangle context = new Rectangle(Width - contextWidth - horizontalInset,
                    (Height - contextHeight) / 2, contextWidth, contextHeight);
                ZarpaPaint.FillRounded(e.Graphics, theme.SurfaceRaised, context, S(theme.CornerRadius));
                ZarpaPaint.DrawRounded(e.Graphics, theme.BorderStrong, context, S(theme.CornerRadius),
                    dpiScale.Stroke(theme.BorderThickness));
                Rectangle contextIcon = new Rectangle(context.Left + S(11), context.Top + S(10), S(20), S(20));
                FluentIconCatalog.TryDraw(e.Graphics, "ic_fluent_database_24_regular", contextIcon,
                    theme.TextMuted, dpiScale.X(18F));
                TextRenderer.DrawText(e.Graphics, contextText, Font,
                    new Rectangle(contextIcon.Right + S(7), context.Top, context.Right - contextIcon.Right - S(15), context.Height),
                    theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDpiScale(ZarpaDpiScale.FromControl(this));
        }

        internal void ApplyDpiForTest(int dpi)
        {
            ApplyDpiScale(new ZarpaDpiScale(dpi, dpi));
        }

        private void ApplyDpiScale(ZarpaDpiScale value)
        {
            if (value == null || dpiScale.DpiX == value.DpiX && dpiScale.DpiY == value.DpiY) return;
            dpiScale = value;
            Height = S(HeaderLogicalHeight);
            Invalidate();
        }

        private int HeaderLogicalHeight
        {
            get { return ZarpaDensityMetrics.Select(theme, 64, 72, 84, 96); }
        }

        private int S(int logicalPixels) { return dpiScale.X(logicalPixels); }
    }
}
