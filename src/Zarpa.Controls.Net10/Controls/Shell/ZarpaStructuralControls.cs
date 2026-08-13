using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Design;
using System.Runtime.InteropServices;
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
            Height = SectionLogicalHeight;
            MinimumSize = new Size(120, 52);
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
            Height = S(SectionLogicalHeight);
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
            Height = S(SectionLogicalHeight);
            Invalidate();
        }

        private int SectionLogicalHeight
        {
            get { return ZarpaDensityMetrics.Select(theme, 58, 68, 82, 94); }
        }

        private int S(int logicalPixels) { return dpiScale.X(logicalPixels); }
    }

    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(Panel))]
    [DefaultProperty("TitleText")]
    [Designer("ZarpaSuite.Controls.Design.ZarpaCardPanelDesigner, Zarpa.Controls")]
    public class ZarpaCardPanel : Panel, IZarpaThemeAware
    {
        private ZarpaThemeTokens theme;
        private ZarpaDpiScale dpiScale = new ZarpaDpiScale(96, 96);
        private readonly Dictionary<Control, Region> contentRegions = new Dictionary<Control, Region>();
        private string titleText = "Tarjeta";
        private string descriptionText = string.Empty;
        private string iconKey = string.Empty;
        private bool compact;
        private bool roundContentCorners = true;

        public ZarpaCardPanel()
        {
            theme = new ZarpaThemeTokens(Invalidate);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = theme.Surface;
            ForeColor = theme.Text;
            Font = new Font("Segoe UI", 9F);
            UpdatePadding();
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

        [Category("Diseño"), DefaultValue(false)]
        public bool Compact
        {
            get { return compact; }
            set
            {
                if (compact == value) return;
                compact = value;
                UpdatePadding();
                PerformLayout();
                Invalidate();
            }
        }

        [Category("Apariencia"), DefaultValue(true)]
        public bool RoundContentCorners
        {
            get { return roundContentCorners; }
            set
            {
                if (roundContentCorners == value) return;
                roundContentCorners = value;
                UpdateContentRegions();
                Invalidate(true);
            }
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

            int headerHeight = HeaderHeight;
            int separatorY = headerHeight - S(8);
            int horizontalPadding = HorizontalPadding;
            int textLeft = horizontalPadding;
            if (!string.IsNullOrEmpty(iconKey))
            {
                int iconSurfaceSize = compact ? S(32) : S(36);
                Rectangle iconSurface = new Rectangle(horizontalPadding,
                    (separatorY - iconSurfaceSize) / 2, iconSurfaceSize, iconSurfaceSize);
                ZarpaPaint.FillRounded(e.Graphics, theme.SurfaceRaised, iconSurface, S(theme.CornerRadius));
                int iconInset = compact ? S(6) : S(8);
                Rectangle icon = new Rectangle(iconSurface.Left + iconInset, iconSurface.Top + iconInset,
                    iconSurface.Width - iconInset * 2, iconSurface.Height - iconInset * 2);
                FluentIconCatalog.TryDraw(e.Graphics, iconKey, icon, theme.Accent,
                    dpiScale.X(18F));
                textLeft = iconSurface.Right + S(theme.SpacingMedium);
            }
            int textWidth = Math.Max(1, Width - textLeft - horizontalPadding);
            bool hasDescription = !string.IsNullOrEmpty(descriptionText);
            int titleTop = compact ? S(hasDescription ? 4 : 10) : S(10);
            int titleHeight = compact ? S(20) : S(24);
            using (Font titleFont = new Font(Font, FontStyle.Bold))
                TextRenderer.DrawText(e.Graphics, titleText, titleFont,
                    new Rectangle(textLeft, titleTop, textWidth, titleHeight), theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (hasDescription)
            {
                int descriptionTop = compact ? S(23) : S(32);
                TextRenderer.DrawText(e.Graphics, descriptionText, Font,
                    new Rectangle(textLeft, descriptionTop, textWidth,
                        Math.Max(S(12), separatorY - descriptionTop - S(2))), theme.TextMuted,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            using (Pen separator = new Pen(theme.Border, dpiScale.Stroke(1)))
                e.Graphics.DrawLine(separator, horizontalPadding, separatorY,
                    Width - horizontalPadding, separatorY);
        }

        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            if (e.Control != null) e.Control.SizeChanged += ContentControlSizeChanged;
            UpdateContentRegions();
        }

        protected override void OnControlRemoved(ControlEventArgs e)
        {
            if (e.Control != null)
            {
                e.Control.SizeChanged -= ContentControlSizeChanged;
                ReleaseContentRegion(e.Control);
            }
            base.OnControlRemoved(e);
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            UpdateContentRegions();
        }

        protected override void OnResize(EventArgs eventargs)
        {
            base.OnResize(eventargs);
            UpdateContentRegions();
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
            int horizontal = compact ? S(12) : S(theme.SpacingLarge);
            int bottom = compact ? S(12) : S(theme.SpacingLarge);
            Padding = new Padding(horizontal, HeaderHeight, horizontal, bottom);
            UpdateContentRegions();
        }

        private void ContentControlSizeChanged(object sender, EventArgs e)
        {
            UpdateContentRegion(sender as Control);
        }

        private void UpdateContentRegions()
        {
            List<Control> stale = new List<Control>();
            foreach (Control control in contentRegions.Keys)
                if (!Controls.Contains(control) || !ShouldRoundContent(control)) stale.Add(control);
            foreach (Control control in stale) ReleaseContentRegion(control);
            foreach (Control control in Controls) UpdateContentRegion(control);
        }

        private void UpdateContentRegion(Control control)
        {
            if (control == null || !ShouldRoundContent(control))
            {
                ReleaseContentRegion(control);
                return;
            }
            Region previous;
            if (contentRegions.TryGetValue(control, out previous))
            {
                if (ReferenceEquals(control.Region, previous)) control.Region = null;
                previous.Dispose();
                contentRegions.Remove(control);
            }
            if (control.Region != null) return;
            Rectangle bounds = new Rectangle(0, 0, Math.Max(1, control.Width), Math.Max(1, control.Height));
            Region region;
            using (GraphicsPath path = ZarpaPaint.RoundedPath(bounds, S(theme.CornerRadius)))
                region = new Region(path);
            control.Region = region;
            contentRegions.Add(control, region);
        }

        private bool ShouldRoundContent(Control control)
        {
            return roundContentCorners && control != null && control.Dock == DockStyle.Fill && control is Panel;
        }

        private void ReleaseContentRegion(Control control)
        {
            if (control == null) return;
            Region region;
            if (!contentRegions.TryGetValue(control, out region)) return;
            if (ReferenceEquals(control.Region, region)) control.Region = null;
            region.Dispose();
            contentRegions.Remove(control);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (Control control in new List<Control>(contentRegions.Keys))
                    ReleaseContentRegion(control);
            }
            base.Dispose(disposing);
        }

        private int HeaderHeight
        {
            get
            {
                return S(compact
                    ? ZarpaDensityMetrics.Select(theme, 44, 46, 48, 54)
                    : ZarpaDensityMetrics.Select(theme, 56, 62, 68, 78));
            }
        }
        private int HorizontalPadding { get { return compact ? S(12) : S(theme.SpacingLarge); } }

        private int S(int logicalPixels) { return dpiScale.X(logicalPixels); }
    }

    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(ListView))]
    [DefaultProperty("Items")]
    [DefaultEvent("SelectedIndexChanged")]
    public class ZarpaDetailsList : ListView, IZarpaThemeAware
    {
        private const int LvmFirst = 0x1000;
        private const int LvmInsertItem = LvmFirst + 7;
        private const int LvmDeleteItem = LvmFirst + 8;
        private const int LvmDeleteAllItems = LvmFirst + 9;
        private const int LvmGetItemCount = LvmFirst + 4;
        private const int LvmScroll = LvmFirst + 20;
        private const int LvmGetTopIndex = LvmFirst + 39;
        private const int SbVertical = 1;
        private const int WmSize = 0x0005;
        private const int WmStyleChanged = 0x007D;
        private ZarpaThemeTokens theme;
        private readonly ZarpaScrollBar scrollBar;
        private bool autoFillLastColumn;
        private bool updatingColumnWidth;
        private bool synchronizingScroll;

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
            Scrollable = true;
            scrollBar = new ZarpaScrollBar { Width = 9, WheelChange = 3, TabStop = false };
            scrollBar.ValueChanged += ScrollBarValueChanged;
            Controls.Add(scrollBar);
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
            scrollBar.ApplyTheme(theme);
            UpdateScrollBar();
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
            UpdateScrollBar();
            UpdateLastColumnWidth();
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            UpdateScrollBar();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            HideNativeScrollBar();
            UpdateScrollBar();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            UpdateScrollBar();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (scrollBar.Enabled) scrollBar.ScrollByWheel(e.Delta);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            UpdateScrollBar();
        }

        protected override void WndProc(ref Message m)
        {
            int message = m.Msg;
            base.WndProc(ref m);
            bool contentChanged = message == LvmInsertItem || message == LvmDeleteItem ||
                message == LvmDeleteAllItems;
            if (scrollBar != null && contentChanged) UpdateScrollBar();
            if (scrollBar != null && (contentChanged || message == WmSize ||
                message == WmStyleChanged)) HideNativeScrollBar();
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
            int width = Math.Max(80, ClientSize.Width - occupied - scrollBar.Width - 2);
            if (Columns[Columns.Count - 1].Width == width) return;
            updatingColumnWidth = true;
            try { Columns[Columns.Count - 1].Width = width; }
            finally { updatingColumnWidth = false; }
        }

        private void UpdateScrollBar()
        {
            if (scrollBar == null || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            HideNativeScrollBar();
            scrollBar.Bounds = new Rectangle(Math.Max(0, ClientSize.Width - scrollBar.Width), 0,
                scrollBar.Width, ClientSize.Height);
            int itemCount = GetCurrentItemCount();
            int rowHeight = GetRowHeight();
            int topIndex = GetTopIndex();
            int headerHeight = 0;
            if (itemCount > 0)
            {
                try { headerHeight = Math.Max(0, GetItemRect(Math.Min(topIndex, itemCount - 1)).Top); }
                catch (ArgumentException) { headerHeight = 0; }
            }
            int viewportRows = Math.Max(1, (ClientSize.Height - headerHeight) / Math.Max(1, rowHeight));
            synchronizingScroll = true;
            try
            {
                scrollBar.SetRange(itemCount, viewportRows);
                scrollBar.Enabled = itemCount > viewportRows;
                scrollBar.Value = scrollBar.Enabled ? topIndex : 0;
            }
            finally { synchronizingScroll = false; }
            scrollBar.BringToFront();
        }

        private void ScrollBarValueChanged(object sender, EventArgs e)
        {
            if (synchronizingScroll || !IsHandleCreated || GetCurrentItemCount() == 0) return;
            int current = GetTopIndex();
            int deltaRows = scrollBar.Value - current;
            if (deltaRows == 0) return;
            synchronizingScroll = true;
            try
            {
                SendMessage(Handle, LvmScroll, IntPtr.Zero,
                    new IntPtr(deltaRows * GetRowHeight()));
            }
            finally { synchronizingScroll = false; }
            Invalidate();
        }

        private int GetTopIndex()
        {
            int itemCount = GetCurrentItemCount();
            if (!IsHandleCreated || itemCount == 0) return 0;
            return Math.Max(0, Math.Min(itemCount - 1,
                SendMessage(Handle, LvmGetTopIndex, IntPtr.Zero, IntPtr.Zero).ToInt32()));
        }

        private int GetRowHeight()
        {
            int itemCount = GetCurrentItemCount();
            if (itemCount == 0) return Math.Max(18, Font.Height + 4);
            try { return Math.Max(1, GetItemRect(Math.Min(GetTopIndex(), itemCount - 1)).Height); }
            catch (ArgumentException) { return Math.Max(18, Font.Height + 4); }
        }

        private int GetCurrentItemCount()
        {
            return IsHandleCreated
                ? Math.Max(0, SendMessage(Handle, LvmGetItemCount, IntPtr.Zero, IntPtr.Zero).ToInt32())
                : Items.Count;
        }

        private void HideNativeScrollBar()
        {
            if (IsHandleCreated) ShowScrollBar(Handle, SbVertical, false);
        }

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool ShowScrollBar(IntPtr window, int bar, bool show);
    }
}
