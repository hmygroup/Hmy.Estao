using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Design;
using System.Windows.Forms;

namespace ZarpaSuite.Controls
{
    [ToolboxItem(false)]
    [ToolboxBitmap(typeof(TabPage))]
    public class ZarpaDocumentTab : Panel, IZarpaThemeAware
    {
        private string key = string.Empty, iconKey = "ic_fluent_document_24_regular";
        private bool canClose = true, isDirty;
        public event EventHandler Changed;
        public ZarpaDocumentTab() { Text = "Documento"; Padding = new Padding(18); }
        [Category("Datos"), DefaultValue("")] public string Key { get { return key; } set { key = value ?? string.Empty; } }
        [Category("Icono"), DefaultValue("ic_fluent_document_24_regular"), Editor("ZarpaSuite.Controls.Design.FluentIconPickerEditor, Zarpa.Controls", typeof(UITypeEditor))]
        public string IconKey { get { return iconKey; } set { iconKey = value ?? string.Empty; OnChanged(); } }
        [Category("Estado"), DefaultValue(true)] public bool CanClose { get { return canClose; } set { canClose = value; OnChanged(); } }
        [Category("Estado"), DefaultValue(false)] public bool IsDirty { get { return isDirty; } set { isDirty = value; OnChanged(); } }
        public void ApplyTheme(ZarpaThemeTokens value) { if (value == null) return; BackColor = value.Canvas; ForeColor = value.Text; Font = new Font(value.FontFamily, value.FontSize); Invalidate(true); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); OnChanged(); }
        private void OnChanged() { if (Changed != null) Changed(this, EventArgs.Empty); }
        public override string ToString() { return Text; }
    }
    public sealed class ZarpaDocumentTabCollection : Collection<ZarpaDocumentTab>
    {
        private readonly ZarpaDocumentTabs owner; internal ZarpaDocumentTabCollection(ZarpaDocumentTabs c) { owner = c; }
        protected override void InsertItem(int i, ZarpaDocumentTab v) { if (v == null) throw new ArgumentNullException("v"); base.InsertItem(i, v); v.Changed += Changed; owner.InsertTabPage(i, v); owner.RefreshTabs(); }
        protected override void SetItem(int i, ZarpaDocumentTab v) { if (v == null) throw new ArgumentNullException("v"); ZarpaDocumentTab previous = this[i]; previous.Changed -= Changed; base.SetItem(i, v); v.Changed += Changed; owner.ReplaceTabPage(i, previous, v); owner.RefreshTabs(); }
        protected override void RemoveItem(int i) { ZarpaDocumentTab removed = this[i]; removed.Changed -= Changed; base.RemoveItem(i); owner.RemoveTabPage(removed); owner.RefreshTabs(); }
        protected override void ClearItems() { ZarpaDocumentTab[] removed = new ZarpaDocumentTab[Count]; CopyTo(removed, 0); foreach (ZarpaDocumentTab v in removed) v.Changed -= Changed; base.ClearItems(); owner.ClearTabPages(removed); owner.RefreshTabs(); }
        private void Changed(object s, EventArgs e) { owner.RefreshTabs(); }
        public void AddRange(ZarpaDocumentTab[] values) { if (values != null) foreach (ZarpaDocumentTab v in values) Add(v); }
    }
    [ToolboxItem(true), DefaultProperty("Tabs"), DefaultEvent("SelectedTabChanged")]
    [ToolboxBitmap(typeof(TabControl))]
    [Designer("ZarpaSuite.Controls.Design.ZarpaDocumentTabsDesigner, Zarpa.Controls")]
    public class ZarpaDocumentTabs : Control, IZarpaThemeAware
    {
        private ZarpaThemeTokens theme; private readonly ZarpaDocumentTabCollection tabs; private int hotIndex = -1, selectedIndex = -1; private Size itemSize = new Size(150, 34); private ZarpaDocumentTab designSelectedTab;
        public ZarpaDocumentTabs() { theme = new ZarpaThemeTokens(Invalidate); tabs = new ZarpaDocumentTabCollection(this); Size = new Size(520, 280); TabStop = true; AccessibleRole = AccessibleRole.PageTabList; SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true); }
        [Category("Datos"), DesignerSerializationVisibility(DesignerSerializationVisibility.Content), Editor("TestRibbon.Controls.ZarpaDocumentTabCollectionEditor, Zarpa.Controls", typeof(UITypeEditor))]
        public ZarpaDocumentTabCollection Tabs { get { return tabs; } }
        [Browsable(false), EditorBrowsable(EditorBrowsableState.Never), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public ControlCollection TabPages { get { return Controls; } }
        [Browsable(false), EditorBrowsable(EditorBrowsableState.Never), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public TabAppearance Appearance { get { return TabAppearance.Normal; } set { } }
        [Browsable(false), EditorBrowsable(EditorBrowsableState.Never), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public TabDrawMode DrawMode { get { return TabDrawMode.OwnerDrawFixed; } set { } }
        [Category("Apariencia"), DefaultValue(typeof(Size), "150, 34")] public Size ItemSize { get { return itemSize; } set { itemSize = new Size(Math.Max(40, value.Width), Math.Max(24, value.Height)); LayoutTabPages(); Invalidate(); } }
        [Browsable(false), EditorBrowsable(EditorBrowsableState.Never), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public TabSizeMode SizeMode { get { return TabSizeMode.Fixed; } set { } }
        [Category("Estado"), DefaultValue(-1)] public int SelectedIndex { get { return selectedIndex; } set { SelectTab(value); } }
        [Browsable(false)] public ZarpaDocumentTab SelectedTab { get { return selectedIndex >= 0 && selectedIndex < tabs.Count ? tabs[selectedIndex] : null; } }
        public event EventHandler SelectedTabChanged; public event EventHandler TabCloseRequested;
        public void ApplyTheme(ZarpaThemeTokens value) { if (value == null) return; theme = value; Font = new Font(theme.FontFamily, theme.FontSize); ItemSize = new Size(ItemSize.Width, Math.Max(28, theme.TabHeight)); foreach (ZarpaDocumentTab tab in tabs) tab.ApplyTheme(value); BackColor = theme.Canvas; Invalidate(true); }
        internal void RefreshTabs() { if (designSelectedTab != null && !tabs.Contains(designSelectedTab)) designSelectedTab = null; if (selectedIndex >= tabs.Count) selectedIndex = tabs.Count - 1; if (selectedIndex < 0 && tabs.Count > 0) selectedIndex = 0; LayoutTabPages(); Invalidate(true); }
        private void DrawTab(Graphics graphics, int index)
        {
            if (index < 0 || index >= tabs.Count) return;
            ZarpaDocumentTab tab = tabs[index];
            Rectangle tabBounds = GetTabRect(index);
            Rectangle bounds = tabBounds; bounds.Inflate(-2, -2);
            bool selected = index == selectedIndex;
            if (selected) ZarpaPaint.FillRounded(graphics, theme.Surface, bounds, theme.CornerRadius);
            else if (index == hotIndex) ZarpaPaint.FillRounded(graphics, ZarpaPaint.Blend(theme.SurfaceRaised, theme.Surface, .5F), bounds, theme.CornerRadius);
            if (selected) using (SolidBrush brush = new SolidBrush(theme.Accent)) graphics.FillRectangle(brush, bounds.Left + theme.CornerRadius, bounds.Bottom - 3, Math.Max(1, bounds.Width - theme.CornerRadius * 2), 3);
            Rectangle icon = new Rectangle(bounds.Left + theme.SpacingMedium, bounds.Top + (bounds.Height - theme.IconSize) / 2, theme.IconSize, theme.IconSize);
            FluentIconCatalog.TryDraw(graphics, tab.IconKey, icon, theme.TextMuted, theme.IconSize - 2F);
            int closeWidth = tab.CanClose ? theme.SpacingLarge + theme.SpacingSmall : theme.SpacingSmall;
            TextRenderer.DrawText(graphics, tab.Text + (tab.IsDirty ? " •" : ""), Font, new Rectangle(icon.Right + theme.SpacingSmall, bounds.Top, Math.Max(1, bounds.Right - icon.Right - closeWidth - theme.SpacingSmall), bounds.Height), theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (tab.CanClose) using (Font closeFont = new Font(Font.FontFamily, Font.Size + 2F)) TextRenderer.DrawText(graphics, "×", closeFont, new Rectangle(bounds.Right - closeWidth, bounds.Top, closeWidth, bounds.Height), theme.TextMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (IsDesignerHosted && tab == designSelectedTab) ZarpaPaint.DrawRounded(graphics, theme.Accent, bounds, theme.CornerRadius, 1.5F);
        }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using (SolidBrush stripBrush = new SolidBrush(theme.Surface)) e.Graphics.FillRectangle(stripBrush, 0, 0, Width, HeaderHeight); for (int index = 0; index < tabs.Count; index++) DrawTab(e.Graphics, index); }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int n = HitTest(e.Location); if (n != hotIndex) { hotIndex = n; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hotIndex = -1; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) Focus(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (IsDesignerHosted || e.Button != MouseButtons.Left) return; int h = HitTest(e.Location); if (h < 0) return; Rectangle tabBounds = GetTabRect(h); if (tabs[h].CanClose && e.X >= tabBounds.Right - 30) { if (TabCloseRequested != null) TabCloseRequested(tabs[h], EventArgs.Empty); } else SelectedIndex = h; }
        protected override bool IsInputKey(Keys keyData) { Keys key = keyData & Keys.KeyCode; return key == Keys.Left || key == Keys.Right || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e); if (tabs.Count == 0) return;
            if (e.KeyCode == Keys.Left) { SelectedIndex = selectedIndex <= 0 ? tabs.Count - 1 : selectedIndex - 1; e.Handled = true; }
            else if (e.KeyCode == Keys.Right || (e.Control && e.KeyCode == Keys.Tab)) { SelectedIndex = selectedIndex < 0 || selectedIndex >= tabs.Count - 1 ? 0 : selectedIndex + 1; e.Handled = true; }
            else if (e.KeyCode == Keys.Home) { SelectedIndex = 0; e.Handled = true; }
            else if (e.KeyCode == Keys.End) { SelectedIndex = tabs.Count - 1; e.Handled = true; }
            else if ((e.KeyCode == Keys.Delete || (e.Control && e.KeyCode == Keys.W)) && SelectedTab != null && SelectedTab.CanClose) { if (TabCloseRequested != null) TabCloseRequested(SelectedTab, EventArgs.Empty); e.Handled = true; }
            if (e.Handled) e.SuppressKeyPress = true;
        }
        private int HeaderHeight { get { return itemSize.Height + 4; } }
        private Rectangle GetTabRect(int index) { return new Rectangle(index * itemSize.Width, 0, itemSize.Width, itemSize.Height); }
        private int HitTest(Point p) { for (int i = 0; i < tabs.Count; i++) if (GetTabRect(i).Contains(p)) return i; return -1; }
        internal int DesignHitTest(Point point) { return HitTest(point); }
        internal bool DesignHeaderContains(Point point) { return HitTest(point) >= 0 || point.Y >= 0 && point.Y <= ItemSize.Height + 8; }
        internal void ActivateDesignTab(ZarpaDocumentTab tab) { designSelectedTab = tab != null && tabs.Contains(tab) ? tab : null; if (designSelectedTab != null) SelectTab(tabs.IndexOf(designSelectedTab)); Invalidate(); }
        internal ZarpaDocumentTab FindTabForControl(Control control)
        {
            for (Control current = control; current != null; current = current.Parent)
                foreach (ZarpaDocumentTab tab in tabs)
                    if (tab == current) return tab;
            return null;
        }
        internal void InsertTabPage(int index, ZarpaDocumentTab tab) { Controls.Add(tab); Controls.SetChildIndex(tab, Math.Max(0, Math.Min(index, Controls.Count - 1))); }
        internal void ReplaceTabPage(int index, ZarpaDocumentTab previous, ZarpaDocumentTab tab) { Controls.Remove(previous); Controls.Add(tab); Controls.SetChildIndex(tab, Math.Max(0, Math.Min(index, Controls.Count - 1))); }
        internal void RemoveTabPage(ZarpaDocumentTab tab) { Controls.Remove(tab); }
        internal void ClearTabPages(ZarpaDocumentTab[] removed) { foreach (ZarpaDocumentTab tab in removed) Controls.Remove(tab); }
        protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutTabPages(); }
        private void LayoutTabPages() { Rectangle bounds = new Rectangle(0, HeaderHeight, Width, Math.Max(0, Height - HeaderHeight)); for (int index = 0; index < tabs.Count; index++) { ZarpaDocumentTab tab = tabs[index]; tab.Bounds = bounds; tab.Visible = index == selectedIndex; } }
        private void SelectTab(int value) { int next = value < -1 ? -1 : value >= tabs.Count ? tabs.Count - 1 : value; if (selectedIndex == next) { LayoutTabPages(); return; } selectedIndex = next; LayoutTabPages(); Invalidate(); if (SelectedTabChanged != null) SelectedTabChanged(this, EventArgs.Empty); }
        private bool IsDesignerHosted { get { return Site != null && Site.DesignMode; } }
    }

    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(Panel))]
    public class ZarpaDockPanel : Panel, IZarpaThemeAware
    {
        private ZarpaThemeTokens theme; private string titleText = "Panel"; private string iconKey = "ic_fluent_panel_left_24_regular"; private bool canClose = true, canCollapse = true, collapsed, closeHot, collapseHot; private int expandedSize = 260; private Rectangle closeBounds, collapseBounds; private readonly ZarpaSizeAnimator sizeAnimator;
        public ZarpaDockPanel() { theme = new ZarpaThemeTokens(Invalidate); SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true); Padding = new Padding(1, 41, 1, 1); Width = expandedSize; Dock = DockStyle.Right; sizeAnimator = new ZarpaSizeAnimator(this, delegate { return Dock == DockStyle.Top || Dock == DockStyle.Bottom ? Height : Width; }, delegate(int value) { if (Dock == DockStyle.Top || Dock == DockStyle.Bottom) Height = value; else Width = value; }); }
        [Category("Cabecera"), DefaultValue("Panel")] public string TitleText { get { return titleText; } set { titleText = value ?? string.Empty; Invalidate(); } }
        [Category("Cabecera"), DefaultValue("ic_fluent_panel_left_24_regular"), Editor("ZarpaSuite.Controls.Design.FluentIconPickerEditor, Zarpa.Controls", typeof(UITypeEditor))] public string IconKey { get { return iconKey; } set { iconKey = value ?? string.Empty; Invalidate(); } }
        [Category("Comportamiento"), DefaultValue(true)] public bool CanClose { get { return canClose; } set { canClose = value; Invalidate(); } }
        [Category("Comportamiento"), DefaultValue(true)] public bool CanCollapse { get { return canCollapse; } set { canCollapse = value; Invalidate(); } }
        [Category("Comportamiento"), DefaultValue(false)] public bool Collapsed { get { return collapsed; } set { if (collapsed == value) return; collapsed = value; bool vertical = Dock == DockStyle.Top || Dock == DockStyle.Bottom; if (value) expandedSize = vertical ? Height : Width; sizeAnimator.Start(value ? (vertical ? 38 : 42) : expandedSize, Math.Max(240, theme.TabDuration), theme.MotionEnabled && !IsDesignerHosted); Invalidate(); if (CollapsedChanged != null) CollapsedChanged(this, EventArgs.Empty); } }
        public event EventHandler CloseRequested; public event EventHandler CollapsedChanged;
        public void ApplyTheme(ZarpaThemeTokens value) { if (value == null) return; theme = value; if (!theme.MotionEnabled) { bool vertical = Dock == DockStyle.Top || Dock == DockStyle.Bottom; sizeAnimator.Start(collapsed ? (vertical ? 38 : 42) : expandedSize, 1, false); } BackColor = theme.Surface; ForeColor = theme.Text; Font = new Font(theme.FontFamily, theme.FontSize); Padding = new Padding(theme.BorderThickness, theme.HeaderHeight + theme.BorderThickness, theme.BorderThickness, theme.BorderThickness); Invalidate(true); }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); using (SolidBrush b = new SolidBrush(theme.SurfaceRaised)) e.Graphics.FillRectangle(b, 0, 0, Width, theme.HeaderHeight); using (Pen p = new Pen(theme.Border, theme.BorderThickness)) e.Graphics.DrawRectangle(p, 0, 0, Math.Max(0, Width - theme.BorderThickness), Math.Max(0, Height - theme.BorderThickness)); Rectangle icon = new Rectangle(theme.SpacingMedium, (theme.HeaderHeight - theme.IconSize) / 2, theme.IconSize, theme.IconSize); FluentIconCatalog.TryDraw(e.Graphics, iconKey, icon, theme.TextMuted, theme.IconSize - 2F); int actionSize = theme.ControlHeight - theme.SpacingSmall; using (Font titleFont = new Font(Font, FontStyle.Bold)) if (!collapsed) TextRenderer.DrawText(e.Graphics, titleText, titleFont, new Rectangle(icon.Right + theme.SpacingMedium, 0, Math.Max(10, Width - icon.Right - actionSize * 2 - theme.SpacingLarge), theme.HeaderHeight), theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis); closeBounds = canClose ? new Rectangle(Width - actionSize - theme.SpacingSmall, (theme.HeaderHeight - actionSize) / 2, actionSize, actionSize) : Rectangle.Empty; collapseBounds = canCollapse ? new Rectangle(Width - (canClose ? actionSize * 2 + theme.SpacingSmall * 2 : actionSize + theme.SpacingSmall), (theme.HeaderHeight - actionSize) / 2, actionSize, actionSize) : Rectangle.Empty; if (canCollapse) { ZarpaPaint.FillRounded(e.Graphics, theme.SurfaceOverlay, collapseBounds, theme.CornerRadius); ZarpaPaint.DrawRounded(e.Graphics, collapseHot ? theme.Accent : theme.BorderStrong, collapseBounds, theme.CornerRadius, 1F); } if (canClose && closeHot) ZarpaPaint.FillRounded(e.Graphics, theme.SurfaceOverlay, closeBounds, theme.CornerRadius); if (canCollapse) { Rectangle actionIcon = new Rectangle(collapseBounds.Left + (collapseBounds.Width - 20) / 2, collapseBounds.Top + (collapseBounds.Height - 20) / 2, 20, 20); Color actionColor = collapseHot ? theme.Accent : theme.Text; DrawChevron(e.Graphics, actionIcon, collapsed, actionColor); } if (canClose) FluentIconCatalog.TryDraw(e.Graphics, "ic_fluent_dismiss_20_regular", new Rectangle(closeBounds.Left + (closeBounds.Width - 20) / 2, closeBounds.Top + (closeBounds.Height - 20) / 2, 20, 20), closeHot ? theme.Danger : theme.TextMuted, 18F); }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); bool nextClose = closeBounds.Contains(e.Location), nextCollapse = collapseBounds.Contains(e.Location); if (nextClose != closeHot || nextCollapse != collapseHot) { closeHot = nextClose; collapseHot = nextCollapse; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); closeHot = false; collapseHot = false; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button != MouseButtons.Left) return; if (closeBounds.Contains(e.Location) && CloseRequested != null) CloseRequested(this, EventArgs.Empty); else if (collapseBounds.Contains(e.Location)) Collapsed = !Collapsed; }
        private bool IsDesignerHosted { get { return Site != null && Site.DesignMode; } }
        private static void DrawChevron(Graphics graphics, Rectangle bounds, bool right, Color color) { int cx = bounds.Left + bounds.Width / 2, cy = bounds.Top + bounds.Height / 2; using (Pen pen = new Pen(color, 1.8F)) graphics.DrawLines(pen, right ? new[] { new Point(cx - 3, cy - 5), new Point(cx + 2, cy), new Point(cx - 3, cy + 5) } : new[] { new Point(cx + 3, cy - 5), new Point(cx - 2, cy), new Point(cx + 3, cy + 5) }); }
        protected override void Dispose(bool disposing) { if (disposing) sizeAnimator.Dispose(); base.Dispose(disposing); }
    }
}
