using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Design;
using System.Windows.Forms;

namespace ZarpaSuite.Controls
{
    public enum ZarpaNavigationItemKind { Item, Header, Separator }

    [ToolboxItem(false)]
    [ToolboxBitmap(typeof(TreeView))]
    public class ZarpaNavigationItem : Component
    {
        private string text = "Elemento", iconKey = string.Empty, badgeText = string.Empty, key = string.Empty;
        private bool enabled = true, visible = true;
        private ZarpaNavigationItemKind kind;
        private ZarpaNavigationPage page;
        public event EventHandler Changed;
        [Category("Datos"), DefaultValue("Elemento")]
        public string Text { get { return text; } set { text = value ?? string.Empty; OnChanged(); } }
        [Category("Datos"), DefaultValue("")]
        public string Key { get { return key; } set { key = value ?? string.Empty; OnChanged(); } }
        [Category("Icono"), DefaultValue("")]
        [Editor("ZarpaSuite.Controls.Design.FluentIconPickerEditor, Zarpa.Controls", typeof(UITypeEditor))]
        public string IconKey { get { return iconKey; } set { iconKey = value ?? string.Empty; OnChanged(); } }
        [Category("Estado"), DefaultValue("")]
        public string BadgeText { get { return badgeText; } set { badgeText = value ?? string.Empty; OnChanged(); } }
        [Category("Estado"), DefaultValue(true)]
        public bool Enabled { get { return enabled; } set { enabled = value; OnChanged(); } }
        [Category("Estado"), DefaultValue(true)]
        public bool Visible { get { return visible; } set { visible = value; OnChanged(); } }
        [Category("Diseño"), DefaultValue(ZarpaNavigationItemKind.Item)]
        public ZarpaNavigationItemKind Kind { get { return kind; } set { kind = value; OnChanged(); } }
        [Category("Navegación"), DefaultValue(null)]
        public ZarpaNavigationPage Page { get { return page; } set { page = value; OnChanged(); } }
        private void OnChanged() { if (Changed != null) Changed(this, EventArgs.Empty); }
        public override string ToString() { return Kind == ZarpaNavigationItemKind.Separator ? "— Separador —" : Text; }
    }

    [ToolboxItem(false)]
    [ToolboxBitmap(typeof(Panel))]
    public class ZarpaNavigationPage : Panel, IZarpaThemeAware
    {
        public ZarpaNavigationPage()
        {
            Dock = DockStyle.Fill;
        }

        [Browsable(false), EditorBrowsable(EditorBrowsableState.Never)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public new bool Visible { get { return base.Visible; } set { base.Visible = value; } }

        internal void SetActive(bool active) { base.Visible = active; }
        public void ApplyTheme(ZarpaThemeTokens value)
        {
            if (value == null) return;
            BackColor = value.Canvas;
            ForeColor = value.Text;
            Font = new Font(value.FontFamily, value.FontSize);
            Invalidate(true);
        }
    }

    public sealed class ZarpaNavigationItemCollection : Collection<ZarpaNavigationItem>
    {
        private readonly ZarpaNavigationView owner;
        internal ZarpaNavigationItemCollection(ZarpaNavigationView control) { owner = control; }
        protected override void InsertItem(int index, ZarpaNavigationItem item) { if (item == null) throw new ArgumentNullException("item"); base.InsertItem(index, item); item.Changed += ItemChanged; owner.RefreshItems(); }
        protected override void SetItem(int index, ZarpaNavigationItem item) { if (item == null) throw new ArgumentNullException("item"); this[index].Changed -= ItemChanged; base.SetItem(index, item); item.Changed += ItemChanged; owner.RefreshItems(); }
        protected override void RemoveItem(int index) { this[index].Changed -= ItemChanged; base.RemoveItem(index); owner.RefreshItems(); }
        protected override void ClearItems() { foreach (ZarpaNavigationItem item in this) item.Changed -= ItemChanged; base.ClearItems(); owner.RefreshItems(); }
        private void ItemChanged(object sender, EventArgs e) { owner.RefreshItems(); }
        public void AddRange(ZarpaNavigationItem[] items) { if (items == null) return; foreach (ZarpaNavigationItem item in items) Add(item); }
    }

    [ToolboxItem(true), DefaultEvent("SelectedItemChanged"), DefaultProperty("Items")]
    [ToolboxBitmap(typeof(TreeView))]
    [Designer("ZarpaSuite.Controls.Design.ZarpaNavigationViewDesigner, Zarpa.Controls")]
    public class ZarpaNavigationView : Control, IZarpaThemeAware
    {
        private ZarpaThemeTokens theme;
        private readonly ZarpaNavigationItemCollection items;
        private int selectedIndex = -1, hotIndex = -1, keyboardIndex = -1;
        internal bool SuppressAccessibilityInterop { get; set; }
        private bool compact;
        private int expandedWidth = 240, compactWidth = 56;
        private string headerText = "NAVEGACIÓN";
        private Rectangle collapseBounds;
        private Rectangle[] itemBounds = new Rectangle[0];
        private bool collapseHot;
        private ZarpaNavigationItem designSelectedItem;
        private readonly ZarpaSizeAnimator sizeAnimator;
        private ZarpaDpiScale dpiScale = new ZarpaDpiScale(96, 96);
        private int SX(int value) { return dpiScale.X(value); }
        private int SY(int value) { return dpiScale.Y(value); }
        private float SX(float value) { return dpiScale.X(value); }
        public ZarpaNavigationView()
        {
            theme = new ZarpaThemeTokens(Invalidate);
            items = new ZarpaNavigationItemCollection(this);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            Width = expandedWidth; Dock = DockStyle.Left; Font = new Font("Segoe UI", 9F);
            TabStop = true; AccessibleRole = AccessibleRole.List;
            sizeAnimator = new ZarpaSizeAnimator(this, delegate { return Width; },
                delegate(int value) { Width = value; });
        }
        [Category("Datos"), DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
        [Editor("TestRibbon.Controls.ZarpaNavigationCollectionEditor, Zarpa.Controls", typeof(UITypeEditor))]
        public ZarpaNavigationItemCollection Items { get { return items; } }
        [Category("Diseño"), DefaultValue("NAVEGACIÓN")]
        public string HeaderText { get { return headerText; } set { headerText = value ?? string.Empty; Invalidate(); } }
        [Category("Diseño"), DefaultValue(false)]
        public bool Compact { get { return compact; } set { if (compact == value) return; compact = value; sizeAnimator.Start(SX(value ? compactWidth : expandedWidth), Math.Max(240, theme.TabDuration), theme.MotionEnabled && !IsDesignerHosted); Invalidate(); if (CompactChanged != null) CompactChanged(this, EventArgs.Empty); } }
        [Category("Diseño"), DefaultValue(240)]
        public int ExpandedWidth { get { return expandedWidth; } set { expandedWidth = Math.Max(160, value); if (!compact) Width = SX(expandedWidth); } }
        [Category("Diseño"), DefaultValue(56)]
        public int CompactWidth { get { return compactWidth; } set { compactWidth = Math.Max(44, value); if (compact) Width = SX(compactWidth); } }
        [Category("Estado"), DefaultValue(-1)]
        public int SelectedIndex { get { return selectedIndex; } set { int next = value < -1 ? -1 : value >= items.Count ? items.Count - 1 : value; if (selectedIndex == next) { UpdatePageVisibility(); return; } int previous = selectedIndex; selectedIndex = next; UpdatePageVisibility(); Invalidate(); if (SelectedItemChanged != null) SelectedItemChanged(this, EventArgs.Empty); NotifyAccessibleChild(AccessibleEvents.StateChange, previous); NotifyAccessibleChild(AccessibleEvents.Selection, selectedIndex); } }
        [Browsable(false)] public ZarpaNavigationItem SelectedItem { get { return selectedIndex >= 0 && selectedIndex < items.Count ? items[selectedIndex] : null; } }
        public event EventHandler SelectedItemChanged;
        public event EventHandler CompactChanged;
        public void ApplyTheme(ZarpaThemeTokens value) { if (value == null) return; theme = value; if (!theme.MotionEnabled) sizeAnimator.Start(SX(compact ? compactWidth : expandedWidth), 1, false); BackColor = theme.Surface; ForeColor = theme.Text; Font = new Font(theme.FontFamily, theme.FontSize); Invalidate(); }
        internal bool IsDesignerHosted { get { return Site != null && Site.DesignMode; } }
        internal ZarpaNavigationItem DesignSelectedItem { get { return designSelectedItem; } set { designSelectedItem = value; Invalidate(); } }
        internal void RefreshItems() { int old = selectedIndex; if (selectedIndex >= items.Count) selectedIndex = FindSelectable(items.Count - 1, -1); if (keyboardIndex >= items.Count) keyboardIndex = -1; UpdatePageVisibility(); Invalidate(); if (old != selectedIndex && SelectedItemChanged != null) SelectedItemChanged(this, EventArgs.Empty); if (IsHandleCreated && !SuppressAccessibilityInterop) AccessibilityNotifyClients(AccessibleEvents.Reorder, 0); if (old != selectedIndex) NotifyAccessibleChild(AccessibleEvents.Selection, selectedIndex); }
        internal int DesignHitTest(Point point) { return HitTest(point, true); }
        internal void ActivateDesignItem(ZarpaNavigationItem item)
        {
            int index = items.IndexOf(item);
            if (index >= 0 && item.Kind == ZarpaNavigationItemKind.Item) SelectedIndex = index;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); e.Graphics.Clear(theme.Surface);
            using (Pen pen = new Pen(theme.Border, dpiScale.Stroke(theme.BorderThickness))) e.Graphics.DrawLine(pen, Width - dpiScale.Stroke(1), 0, Width - dpiScale.Stroke(1), Height);
            if (!compact) using (Font f = new Font(Font.FontFamily, 8F, FontStyle.Bold)) TextRenderer.DrawText(e.Graphics, headerText, f, new Rectangle(SX(16), 0, Width - SX(64), SY(48)), theme.TextMuted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            collapseBounds = new Rectangle(Width - SX(42), SY(8), SX(34), SY(32));
            ZarpaPaint.FillRounded(e.Graphics, theme.SurfaceOverlay,
                collapseBounds, SX(theme.CornerRadius));
            ZarpaPaint.DrawRounded(e.Graphics, collapseHot ? theme.Accent : theme.BorderStrong,
                collapseBounds, SX(theme.CornerRadius), dpiScale.Stroke(1));
            Rectangle collapseIcon = new Rectangle(collapseBounds.Left + SX(7), collapseBounds.Top + SY(6), SX(20), SY(20));
            Color collapseColor = collapseHot ? theme.Accent : theme.Text;
            DrawChevron(e.Graphics, collapseIcon, compact, collapseColor);
            UpdateItemBounds();
            for (int i = 0; i < items.Count; i++)
            {
                ZarpaNavigationItem item = items[i]; if (!item.Visible) continue;
                Rectangle bounds = itemBounds[i];
                if (item.Kind == ZarpaNavigationItemKind.Separator) { using (Pen p = new Pen(theme.Border)) e.Graphics.DrawLine(p, SX(12), bounds.Top + SY(8), Width - SX(12), bounds.Top + SY(8)); DrawDesignSelection(e.Graphics, item, bounds); continue; }
                if (item.Kind == ZarpaNavigationItemKind.Header) { if (!compact) using (Font f = new Font(Font.FontFamily, 8F, FontStyle.Bold)) TextRenderer.DrawText(e.Graphics, item.Text.ToUpperInvariant(), f, new Rectangle(SX(16), bounds.Top, Width - SX(24), SY(30)), theme.TextMuted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis); DrawDesignSelection(e.Graphics, item, bounds); continue; }
                if (i == selectedIndex) ZarpaPaint.FillRounded(e.Graphics, theme.Selection, bounds, SX(theme.CornerRadius));
                else if (i == hotIndex) ZarpaPaint.FillRounded(e.Graphics, theme.SurfaceRaised, bounds, SX(theme.CornerRadius));
                if (Focused && i == keyboardIndex) ZarpaPaint.DrawRounded(e.Graphics, theme.Accent, bounds, SX(theme.CornerRadius), SX(1.5F));
                if (i == selectedIndex) using (SolidBrush b = new SolidBrush(theme.Accent)) e.Graphics.FillRectangle(b, bounds.Left, bounds.Top + SY(7), SX(3), bounds.Height - SY(14));
                int iconSize = SX(theme.IconSize);
                Rectangle icon = new Rectangle(bounds.Left + (compact ? (bounds.Width - iconSize) / 2 : SX(12)), bounds.Top + (bounds.Height - iconSize) / 2, iconSize, iconSize);
                FluentIconCatalog.TryDraw(e.Graphics, item.IconKey, icon, item.Enabled ? (i == selectedIndex ? theme.Accent : theme.TextMuted) : theme.BorderStrong, SX(theme.IconSize - 2F));
                if (!compact)
                {
                    Rectangle text = new Rectangle(icon.Right + SX(10), bounds.Top, Math.Max(SX(10), bounds.Width - SX(78)), bounds.Height);
                    TextRenderer.DrawText(e.Graphics, item.Text, Font, text, item.Enabled ? theme.Text : theme.TextMuted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    if (!string.IsNullOrEmpty(item.BadgeText))
                    {
                        Size badgeSize = TextRenderer.MeasureText(item.BadgeText, Font);
                        Rectangle badge = new Rectangle(bounds.Right - badgeSize.Width - SX(15), bounds.Top + SY(10), badgeSize.Width + SX(8), SY(20));
                        ZarpaPaint.FillRounded(e.Graphics, theme.Accent, badge, SX(Math.Min(theme.CornerRadius, 8)));
                        TextRenderer.DrawText(e.Graphics, item.BadgeText, Font, badge, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                    }
                }
                DrawDesignSelection(e.Graphics, item, bounds);
            }
        }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int next = HitTest(e.Location, false); bool nextCollapseHot = collapseBounds.Contains(e.Location); if (next != hotIndex || nextCollapseHot != collapseHot) { hotIndex = next; collapseHot = nextCollapseHot; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hotIndex = -1; collapseHot = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) Focus(); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e); if (e.Button != MouseButtons.Left || IsDesignerHosted) return;
            if (collapseBounds.Contains(e.Location)) { Compact = !Compact; return; }
            int hit = HitTest(e.Location, false); if (hit >= 0 && items[hit].Enabled && items[hit].Kind == ZarpaNavigationItemKind.Item) { SetKeyboardIndex(hit); SelectedIndex = hit; }
        }
        protected override bool IsInputKey(Keys keyData) { Keys key = keyData & Keys.KeyCode; return key == Keys.Up || key == Keys.Down || key == Keys.Left || key == Keys.Right || base.IsInputKey(keyData); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); SetKeyboardIndex(selectedIndex >= 0 && selectedIndex < items.Count && items[selectedIndex].Visible && items[selectedIndex].Enabled && items[selectedIndex].Kind == ZarpaNavigationItemKind.Item ? selectedIndex : FindSelectable(-1, 1)); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); SetKeyboardIndex(-1); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Up) { SetKeyboardIndex(FindSelectable(keyboardIndex, -1)); e.Handled = true; }
            else if (e.KeyCode == Keys.Down) { SetKeyboardIndex(FindSelectable(keyboardIndex, 1)); e.Handled = true; }
            else if (e.KeyCode == Keys.Home) { SetKeyboardIndex(FindSelectable(-1, 1)); e.Handled = true; }
            else if (e.KeyCode == Keys.End) { SetKeyboardIndex(FindSelectable(items.Count, -1)); e.Handled = true; }
            else if (e.KeyCode == Keys.Left && !compact) { Compact = true; e.Handled = true; }
            else if (e.KeyCode == Keys.Right && compact) { Compact = false; e.Handled = true; }
            else if ((e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) && keyboardIndex >= 0) { SelectedIndex = keyboardIndex; e.Handled = true; }
            if (e.Handled) { e.SuppressKeyPress = true; Invalidate(); }
        }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDpiScale(ZarpaDpiScale.FromControl(this));
        }
        internal void ApplyDpiForTest(int dpi) { ApplyDpiScale(new ZarpaDpiScale(dpi, dpi)); }
        private void ApplyDpiScale(ZarpaDpiScale value)
        {
            if (value == null || (dpiScale.DpiX == value.DpiX && dpiScale.DpiY == value.DpiY)) return;
            dpiScale = value;
            sizeAnimator.Start(SX(compact ? compactWidth : expandedWidth), 1, false);
            collapseBounds = Rectangle.Empty;
            itemBounds = new Rectangle[items.Count];
            PerformLayout();
            Invalidate();
        }
        private int FindSelectable(int start, int direction)
        {
            int index = start + direction;
            while (index >= 0 && index < items.Count)
            {
                ZarpaNavigationItem item = items[index];
                if (item.Visible && item.Enabled && item.Kind == ZarpaNavigationItemKind.Item) return index;
                index += direction;
            }
            return start >= 0 && start < items.Count && items[start].Visible && items[start].Enabled && items[start].Kind == ZarpaNavigationItemKind.Item ? start : -1;
        }
        private void SetKeyboardIndex(int value)
        {
            if (keyboardIndex == value) return;
            keyboardIndex = value;
            NotifyAccessibleChild(AccessibleEvents.Focus, keyboardIndex);
            Invalidate();
        }
        private void NotifyAccessibleChild(AccessibleEvents accessibleEvent, int modelIndex)
        {
            if (!SuppressAccessibilityInterop && IsHandleCreated && modelIndex >= 0 && modelIndex < items.Count)
                AccessibilityNotifyClients(accessibleEvent, modelIndex + 1);
        }
        private void UpdateItemBounds()
        {
            int y = SY(52);
            if (itemBounds.Length != items.Count) itemBounds = new Rectangle[items.Count];
            else Array.Clear(itemBounds, 0, itemBounds.Length);
            for (int index = 0; index < items.Count; index++)
            {
                ZarpaNavigationItem item = items[index];
                if (!item.Visible) continue;
                if (item.Kind == ZarpaNavigationItemKind.Separator)
                {
                    itemBounds[index] = new Rectangle(SX(8), y, Math.Max(1, Width - SX(16)), SY(18));
                    y += SY(18);
                }
                else if (item.Kind == ZarpaNavigationItemKind.Header)
                {
                    itemBounds[index] = new Rectangle(SX(8), y, Math.Max(1, Width - SX(16)), SY(30));
                    y += SY(32);
                }
                else
                {
                    int itemHeight = SY(theme.ControlHeight + 6);
                    itemBounds[index] = new Rectangle(SX(theme.SpacingMedium), y, Width - SX(theme.SpacingMedium * 2), itemHeight);
                    y += itemHeight + SY(theme.SpacingSmall);
                }
            }
        }
        private Rectangle GetItemBounds(int index)
        {
            UpdateItemBounds();
            return index >= 0 && index < itemBounds.Length ? itemBounds[index] : Rectangle.Empty;
        }
        private int HitTest(Point point, bool includeStructure)
        {
            UpdateItemBounds();
            for (int i = 0; i < itemBounds.Length; i++)
                if (!itemBounds[i].IsEmpty && itemBounds[i].Contains(point) &&
                    (includeStructure || items[i].Kind == ZarpaNavigationItemKind.Item)) return i;
            return -1;
        }
        private void DrawDesignSelection(Graphics graphics, ZarpaNavigationItem item, Rectangle bounds)
        {
            if (!IsDesignerHosted || item != designSelectedItem) return;
            using (Pen pen = new Pen(theme.Accent, dpiScale.Stroke(1))) { pen.DashStyle = System.Drawing.Drawing2D.DashStyle.Dash; graphics.DrawRectangle(pen, bounds); }
        }
        private void DrawChevron(Graphics graphics, Rectangle bounds, bool right, Color color)
        {
            int cx = bounds.Left + bounds.Width / 2, cy = bounds.Top + bounds.Height / 2;
            using (Pen pen = new Pen(color, SX(1.8F)))
                graphics.DrawLines(pen, right
                    ? new[] { new Point(cx - SX(3), cy - SY(5)), new Point(cx + SX(2), cy), new Point(cx - SX(3), cy + SY(5)) }
                    : new[] { new Point(cx + SX(3), cy - SY(5)), new Point(cx - SX(2), cy), new Point(cx + SX(3), cy + SY(5)) });
        }
        private void UpdatePageVisibility()
        {
            ZarpaNavigationPage selectedPage = SelectedItem == null ? null : SelectedItem.Page;
            foreach (ZarpaNavigationItem item in items)
                if (item.Page != null) item.Page.SetActive(item.Page == selectedPage);
            if (selectedPage != null) selectedPage.BringToFront();
        }
        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new NavigationAccessibleObject(this);
        }
        private sealed class NavigationAccessibleObject : ControlAccessibleObject
        {
            private readonly ZarpaNavigationView navigation;
            private readonly System.Collections.Generic.Dictionary<ZarpaNavigationItem, AccessibleObject> children = new System.Collections.Generic.Dictionary<ZarpaNavigationItem, AccessibleObject>();
            internal NavigationAccessibleObject(ZarpaNavigationView owner) : base(owner) { navigation = owner; }
            public override string Name
            {
                get { return !string.IsNullOrEmpty(navigation.AccessibleName) ? navigation.AccessibleName :
                    !string.IsNullOrEmpty(navigation.HeaderText) ? navigation.HeaderText : "Navegación"; }
                set { navigation.AccessibleName = value; }
            }
            public override AccessibleRole Role { get { return navigation.AccessibleRole; } }
            public override int GetChildCount() { return navigation.items.Count; }
            public override AccessibleObject GetChild(int index)
            {
                if (index < 0 || index >= navigation.items.Count) return null;
                ZarpaNavigationItem item = navigation.items[index];
                AccessibleObject child;
                if (!children.TryGetValue(item, out child))
                {
                    child = new NavigationItemAccessibleObject(navigation, item);
                    children[item] = child;
                }
                return child;
            }
            public override AccessibleObject Navigate(AccessibleNavigation navdir)
            {
                if (navigation.items.Count == 0) return null;
                if (navdir == AccessibleNavigation.FirstChild) return GetChild(0);
                if (navdir == AccessibleNavigation.LastChild) return GetChild(navigation.items.Count - 1);
                return base.Navigate(navdir);
            }
        }
        private sealed class NavigationItemAccessibleObject : AccessibleObject
        {
            private readonly ZarpaNavigationView navigation;
            private readonly ZarpaNavigationItem item;
            internal NavigationItemAccessibleObject(ZarpaNavigationView owner, ZarpaNavigationItem model) { navigation = owner; item = model; }
            private int Index { get { return navigation.items.IndexOf(item); } }
            public override string Name { get { return item.Kind == ZarpaNavigationItemKind.Separator ? string.Empty : item.Text; } set { item.Text = value ?? string.Empty; } }
            public override AccessibleRole Role
            {
                get { return item.Kind == ZarpaNavigationItemKind.Header ? AccessibleRole.StaticText :
                    item.Kind == ZarpaNavigationItemKind.Separator ? AccessibleRole.Separator : AccessibleRole.ListItem; }
            }
            public override AccessibleObject Parent { get { return navigation.AccessibilityObject; } }
            public override string DefaultAction
            {
                get { return item.Kind == ZarpaNavigationItemKind.Item && item.Enabled && item.Visible ? "Seleccionar" : string.Empty; }
            }
            public override Rectangle Bounds
            {
                get
                {
                    int index = Index;
                    if (index < 0 || !navigation.IsHandleCreated || navigation.IsDisposed || !navigation.Visible || !item.Visible)
                        return Rectangle.Empty;
                    Rectangle bounds = Rectangle.Intersect(navigation.ClientRectangle, navigation.GetItemBounds(index));
                    return bounds.IsEmpty ? Rectangle.Empty : navigation.RectangleToScreen(bounds);
                }
            }
            public override AccessibleStates State
            {
                get
                {
                    int index = Index;
                    bool actionable = item.Kind == ZarpaNavigationItemKind.Item;
                    AccessibleStates state = actionable
                        ? AccessibleStates.Selectable | AccessibleStates.Focusable : AccessibleStates.ReadOnly;
                    if (index < 0 || navigation.IsDisposed) return state | AccessibleStates.Invisible | AccessibleStates.Unavailable;
                    if (!navigation.Enabled || !item.Enabled) state |= AccessibleStates.Unavailable;
                    if (!navigation.Visible || !item.Visible) state |= AccessibleStates.Invisible;
                    else if (Bounds.IsEmpty) state |= AccessibleStates.Offscreen;
                    if (index == navigation.selectedIndex) state |= AccessibleStates.Selected;
                    if (navigation.Focused && index == navigation.keyboardIndex) state |= AccessibleStates.Focused;
                    return state;
                }
            }
            public override void DoDefaultAction()
            {
                int index = Index;
                if (index < 0 || navigation.IsDisposed || !navigation.Enabled || !item.Enabled || !item.Visible ||
                    item.Kind != ZarpaNavigationItemKind.Item) return;
                if (!navigation.SuppressAccessibilityInterop) navigation.Focus();
                navigation.SetKeyboardIndex(index);
                navigation.SelectedIndex = index;
            }
            public override AccessibleObject Navigate(AccessibleNavigation navdir)
            {
                int index = Index;
                int direction = navdir == AccessibleNavigation.Next || navdir == AccessibleNavigation.Down ? 1 :
                    navdir == AccessibleNavigation.Previous || navdir == AccessibleNavigation.Up ? -1 : 0;
                if (direction == 0) return null;
                int next = index + direction;
                while (next >= 0 && next < navigation.items.Count && !navigation.items[next].Visible) next += direction;
                return next >= 0 && next < navigation.items.Count ? navigation.AccessibilityObject.GetChild(next) : null;
            }
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) sizeAnimator.Dispose();
            base.Dispose(disposing);
        }
    }
}
