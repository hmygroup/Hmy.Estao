using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Design;
using System.Windows.Forms;

namespace ZarpaSuite.Controls
{
    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(Label))]
    [DefaultProperty("TitleText")]
    public class ZarpaSectionHeader : Control, IZarpaThemeAware
    {
        private ZarpaThemeTokens theme;
        private ZarpaDpiScale dpiScale = new ZarpaDpiScale(96, 96);
        private string titleText = "Título de sección";
        private string descriptionText = string.Empty;
        private string iconKey = string.Empty;

        public ZarpaSectionHeader()
        {
            theme = new ZarpaThemeTokens(Invalidate);
            Dock = DockStyle.Top;
            Height = 82;
            MinimumSize = new Size(120, 58);
            Font = new Font("Segoe UI", 9F);
            TabStop = false;
            AccessibleRole = AccessibleRole.StaticText;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        [Category("Contenido"), DefaultValue("Título de sección")]
        public string TitleText
        {
            get { return titleText; }
            set { titleText = value ?? string.Empty; AccessibleName = titleText; Invalidate(); }
        }

        [Category("Contenido"), DefaultValue("")]
        public string DescriptionText
        {
            get { return descriptionText; }
            set { descriptionText = value ?? string.Empty; AccessibleDescription = descriptionText; Invalidate(); }
        }

        [Category("Icono"), DefaultValue("")]
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
            ForeColor = theme.Text;
            Height = S(82);
            Invalidate();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(ZarpaPaint.EffectiveBackColor(Parent));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Color text = SystemInformation.HighContrast ? SystemColors.WindowText : theme.Text;
            Color muted = SystemInformation.HighContrast ? SystemColors.GrayText : theme.TextMuted;
            int textLeft = 0;
            if (!string.IsNullOrEmpty(iconKey))
            {
                Rectangle tile = new Rectangle(0, S(8), S(42), S(42));
                ZarpaPaint.FillRounded(e.Graphics, theme.SurfaceRaised, tile, S(theme.CornerRadius));
                ZarpaPaint.DrawRounded(e.Graphics, theme.Border, tile, S(theme.CornerRadius),
                    dpiScale.Stroke(theme.BorderThickness));
                Rectangle icon = new Rectangle(tile.Left + S(10), tile.Top + S(10), S(22), S(22));
                FluentIconCatalog.TryDraw(e.Graphics, iconKey, icon, theme.Accent, dpiScale.X(20F));
                textLeft = tile.Right + S(12);
            }
            using (Font titleFont = new Font(Font.FontFamily, Math.Max(15F, theme.HeadingFontSize - 5F), FontStyle.Bold))
                TextRenderer.DrawText(e.Graphics, titleText, titleFont,
                    new Rectangle(textLeft, S(1), Math.Max(1, Width - textLeft), S(34)), text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(e.Graphics, descriptionText, Font,
                new Rectangle(textLeft, S(35), Math.Max(1, Width - textLeft), Math.Max(S(20), Height - S(37))), muted,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDpiScale(ZarpaDpiScale.FromControl(this));
        }

        internal void ApplyDpiForTest(int dpi) { ApplyDpiScale(new ZarpaDpiScale(dpi, dpi)); }

        private void ApplyDpiScale(ZarpaDpiScale value)
        {
            if (value == null || dpiScale.DpiX == value.DpiX && dpiScale.DpiY == value.DpiY) return;
            dpiScale = value;
            Height = S(82);
            Invalidate();
        }

        private int S(int logicalPixels) { return dpiScale.X(logicalPixels); }
    }

    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(Panel))]
    [DefaultProperty("TitleText")]
    [Designer("System.Windows.Forms.Design.PanelDesigner, System.Design")]
    public class ZarpaCardPanel : Panel, IZarpaThemeAware
    {
        private ZarpaThemeTokens theme;
        private ZarpaDpiScale dpiScale = new ZarpaDpiScale(96, 96);
        private string titleText = "Tarjeta";
        private string descriptionText = string.Empty;
        private string iconKey = string.Empty;

        public ZarpaCardPanel()
        {
            theme = new ZarpaThemeTokens(Invalidate);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = theme.Surface;
            ForeColor = theme.Text;
            Font = new Font("Segoe UI", 9F);
            Padding = new Padding(16, 68, 16, 16);
            AccessibleRole = AccessibleRole.Grouping;
        }

        [Category("Contenido"), DefaultValue("Tarjeta")]
        public string TitleText
        {
            get { return titleText; }
            set { titleText = value ?? string.Empty; AccessibleName = titleText; Invalidate(); }
        }

        [Category("Contenido"), DefaultValue("")]
        public string DescriptionText
        {
            get { return descriptionText; }
            set { descriptionText = value ?? string.Empty; AccessibleDescription = descriptionText; Invalidate(); }
        }

        [Category("Icono"), DefaultValue("")]
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
            UpdatePadding();
            PerformLayout();
            Invalidate();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(ZarpaPaint.EffectiveBackColor(Parent));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Rectangle card = new Rectangle(0, 0, Math.Max(1, Width - 1), Math.Max(1, Height - 1));
            ZarpaPaint.FillRounded(e.Graphics, theme.Surface, card, S(theme.GroupCornerRadius));
            ZarpaPaint.DrawRounded(e.Graphics, theme.Border, card, S(theme.GroupCornerRadius),
                dpiScale.Stroke(theme.BorderThickness));

            int textLeft = S(theme.SpacingLarge);
            if (!string.IsNullOrEmpty(iconKey))
            {
                Rectangle iconSurface = new Rectangle(S(theme.SpacingLarge), S(14), S(36), S(36));
                ZarpaPaint.FillRounded(e.Graphics, theme.SurfaceRaised, iconSurface, S(theme.CornerRadius));
                Rectangle icon = new Rectangle(iconSurface.Left + S(8), iconSurface.Top + S(8), S(20), S(20));
                FluentIconCatalog.TryDraw(e.Graphics, iconKey, icon, theme.Accent, dpiScale.X(18F));
                textLeft = iconSurface.Right + S(theme.SpacingMedium);
            }
            using (Font titleFont = new Font(Font, FontStyle.Bold))
                TextRenderer.DrawText(e.Graphics, titleText, titleFont,
                    new Rectangle(textLeft, S(10), Math.Max(1, Width - textLeft - S(theme.SpacingLarge)), S(24)), theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(e.Graphics, descriptionText, Font,
                new Rectangle(textLeft, S(32), Math.Max(1, Width - textLeft - S(theme.SpacingLarge)), S(22)), theme.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            using (Pen separator = new Pen(theme.Border, dpiScale.Stroke(1)))
                e.Graphics.DrawLine(separator, S(theme.SpacingLarge), S(59), Width - S(theme.SpacingLarge), S(59));
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDpiScale(ZarpaDpiScale.FromControl(this));
        }

        internal void ApplyDpiForTest(int dpi) { ApplyDpiScale(new ZarpaDpiScale(dpi, dpi)); }

        private void ApplyDpiScale(ZarpaDpiScale value)
        {
            if (value == null || dpiScale.DpiX == value.DpiX && dpiScale.DpiY == value.DpiY) return;
            dpiScale = value;
            UpdatePadding();
            PerformLayout();
            Invalidate();
        }

        private void UpdatePadding()
        {
            Padding = new Padding(S(theme.SpacingLarge), S(68), S(theme.SpacingLarge), S(theme.SpacingLarge));
        }

        private int S(int logicalPixels) { return dpiScale.X(logicalPixels); }
    }

    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(ListView))]
    [DefaultProperty("Items")]
    [DefaultEvent("SelectedIndexChanged")]
    public class ZarpaDetailsList : ListView, IZarpaThemeAware
    {
        private ZarpaThemeTokens theme;
        private bool autoFillLastColumn;
        private bool updatingColumnWidth;

        public ZarpaDetailsList()
        {
            theme = new ZarpaThemeTokens(Invalidate);
            View = View.Details;
            FullRowSelect = true;
            HideSelection = false;
            UseCompatibleStateImageBehavior = false;
            BorderStyle = BorderStyle.FixedSingle;
            OwnerDraw = true;
            DoubleBuffered = true;
        }

        [Category("Diseño"), DefaultValue(false)]
        public bool AutoFillLastColumn
        {
            get { return autoFillLastColumn; }
            set { autoFillLastColumn = value; UpdateLastColumnWidth(); }
        }

        public void ApplyTheme(ZarpaThemeTokens value)
        {
            if (value == null) return;
            theme = value;
            Font = new Font(theme.FontFamily, theme.FontSize);
            BackColor = SystemInformation.HighContrast ? SystemColors.Window : theme.Surface;
            ForeColor = SystemInformation.HighContrast ? SystemColors.WindowText : theme.Text;
            Invalidate();
        }

        protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
        {
            Color surface = SystemInformation.HighContrast ? SystemColors.Control : theme.SurfaceRaised;
            Color text = SystemInformation.HighContrast ? SystemColors.ControlText : theme.Text;
            Color border = SystemInformation.HighContrast ? SystemColors.ControlDark : theme.Border;
            using (SolidBrush fill = new SolidBrush(surface)) e.Graphics.FillRectangle(fill, e.Bounds);
            using (Pen pen = new Pen(border))
            {
                e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
                e.Graphics.DrawLine(pen, e.Bounds.Right - 1, e.Bounds.Top, e.Bounds.Right - 1, e.Bounds.Bottom);
            }
            TextFormatFlags alignment = e.Header.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right :
                e.Header.TextAlign == HorizontalAlignment.Center ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left;
            using (Font headerFont = new Font(Font, FontStyle.Bold))
                TextRenderer.DrawText(e.Graphics, e.Header.Text, headerFont,
                    new Rectangle(e.Bounds.Left + 8, e.Bounds.Top, Math.Max(1, e.Bounds.Width - 16), e.Bounds.Height), text,
                    alignment | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        protected override void OnDrawItem(DrawListViewItemEventArgs e)
        {
            e.DrawDefault = true;
        }

        protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
        {
            e.DrawDefault = true;
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateLastColumnWidth();
        }

        protected override void OnColumnWidthChanged(ColumnWidthChangedEventArgs e)
        {
            base.OnColumnWidthChanged(e);
            if (!updatingColumnWidth) UpdateLastColumnWidth();
        }

        private void UpdateLastColumnWidth()
        {
            if (!autoFillLastColumn || Columns.Count == 0 || ClientSize.Width <= 0) return;
            int occupied = 0;
            for (int index = 0; index < Columns.Count - 1; index++) occupied += Columns[index].Width;
            int width = Math.Max(80, ClientSize.Width - occupied - SystemInformation.VerticalScrollBarWidth - 2);
            if (Columns[Columns.Count - 1].Width == width) return;
            updatingColumnWidth = true;
            try { Columns[Columns.Count - 1].Width = width; }
            finally { updatingColumnWidth = false; }
        }
    }
}
