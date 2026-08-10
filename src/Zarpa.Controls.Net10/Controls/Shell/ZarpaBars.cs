using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Design;
using System.Windows.Forms;

namespace ZarpaSuite.Controls
{
    public enum ZarpaCommandItemKind { Button, Toggle, Separator }
    public enum ZarpaStatusKind { Neutral, Information, Success, Warning, Error, Busy }

    [ToolboxItem(false)]
    [ToolboxBitmap(typeof(ToolStripButton))]
    public class ZarpaCommandItem : Component
    {
        private string text = "Comando", iconKey = string.Empty, toolTipText = string.Empty;
        private bool enabled = true, visible = true, isChecked;
        private ZarpaCommandItemKind kind;
        public event EventHandler Click;
        public event EventHandler Changed;
        [Category("Datos"), DefaultValue("Comando")]
        public string Text { get { return text; } set { text = value ?? string.Empty; OnChanged(); } }
        [Category("Icono"), DefaultValue("")]
        [Editor("ZarpaSuite.Controls.Design.FluentIconPickerEditor, Zarpa.Controls", typeof(UITypeEditor))]
        public string IconKey { get { return iconKey; } set { iconKey = value ?? string.Empty; OnChanged(); } }
        [Category("Comportamiento"), DefaultValue("")]
        public string ToolTipText { get { return toolTipText; } set { toolTipText = value ?? string.Empty; OnChanged(); } }
        [Category("Comportamiento"), DefaultValue(true)]
        public bool Enabled { get { return enabled; } set { enabled = value; OnChanged(); } }
        [Category("Comportamiento"), DefaultValue(true)]
        public bool Visible { get { return visible; } set { visible = value; OnChanged(); } }
        [Category("Comportamiento"), DefaultValue(false)]
        public bool Checked { get { return isChecked; } set { isChecked = value; OnChanged(); } }
        [Category("Diseño"), DefaultValue(ZarpaCommandItemKind.Button)]
        public ZarpaCommandItemKind Kind { get { return kind; } set { kind = value; OnChanged(); } }
        internal void PerformClick() { if (kind == ZarpaCommandItemKind.Toggle) Checked = !Checked; if (Click != null) Click(this, EventArgs.Empty); }
        private void OnChanged() { if (Changed != null) Changed(this, EventArgs.Empty); }
        public override string ToString() { return kind == ZarpaCommandItemKind.Separator ? "— Separador —" : Text; }
    }

    public sealed class ZarpaCommandItemCollection : Collection<ZarpaCommandItem>
    {
        private readonly ZarpaCommandBar owner;
        internal ZarpaCommandItemCollection(ZarpaCommandBar control) { owner = control; }
        protected override void InsertItem(int index, ZarpaCommandItem item) { if (item == null) throw new ArgumentNullException("item"); base.InsertItem(index, item); item.Changed += Changed; owner.ItemsChanged(); }
        protected override void SetItem(int index, ZarpaCommandItem item) { if (item == null) throw new ArgumentNullException("item"); this[index].Changed -= Changed; base.SetItem(index, item); item.Changed += Changed; owner.ItemsChanged(); }
        protected override void RemoveItem(int index) { this[index].Changed -= Changed; base.RemoveItem(index); owner.ItemsChanged(); }
        protected override void ClearItems() { foreach (ZarpaCommandItem item in this) item.Changed -= Changed; base.ClearItems(); owner.ItemsChanged(); }
        private void Changed(object sender, EventArgs e) { owner.ItemChanged(sender as ZarpaCommandItem); }
        public void AddRange(ZarpaCommandItem[] values) { if (values == null) return; foreach (ZarpaCommandItem value in values) Add(value); }
    }
    [ToolboxItem(true), DefaultProperty("Items"), DefaultEvent("ItemClick")]
    [ToolboxBitmap(typeof(ToolStrip))]
    [Designer("ZarpaSuite.Controls.Design.ZarpaCommandBarDesigner, Zarpa.Controls")]
    public class ZarpaCommandBar : Control, IZarpaThemeAware
    {
        private ZarpaThemeTokens theme;
        private readonly ZarpaCommandItemCollection items;
        private int hotIndex = -1, pressedIndex = -1, keyboardIndex = -1;
        private ZarpaCommandItem designSelectedItem;
        private Rectangle[] itemBounds = new Rectangle[0];
        private bool showText = true;
        private ZarpaDpiScale dpiScale = new ZarpaDpiScale(96, 96);
        internal bool SuppressAccessibilityInterop { get; set; }
        private int S(int logicalPixels) { return dpiScale.X(logicalPixels); }
        private readonly ToolTip toolTip = new ToolTip();
        public ZarpaCommandBar()
        {
            theme = new ZarpaThemeTokens(Invalidate); items = new ZarpaCommandItemCollection(this);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            Height = S(46); Dock = DockStyle.Top; Font = new Font("Segoe UI", 9F); TabStop = true; AccessibleRole = AccessibleRole.ToolBar;
        }
        [Category("Datos"), DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
        [Editor("TestRibbon.Controls.ZarpaCommandCollectionEditor, Zarpa.Controls", typeof(UITypeEditor))]
        public ZarpaCommandItemCollection Items { get { return items; } }
        [Category("Diseño"), DefaultValue(true)]
        public bool ShowText { get { return showText; } set { showText = value; ItemsChanged(); } }
        public event EventHandler ItemClick;
        public void ApplyTheme(ZarpaThemeTokens value) { if (value == null) return; theme = value; BackColor = theme.Surface; ForeColor = theme.Text; Font = new Font(theme.FontFamily, theme.FontSize); Height = S(theme.ControlHeight + theme.SpacingMedium + theme.SpacingSmall); Invalidate(); }
        protected override void Dispose(bool disposing) { if (disposing) toolTip.Dispose(); base.Dispose(disposing); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); e.Graphics.Clear(theme.Surface);
            using (Pen p = new Pen(theme.Border, dpiScale.Stroke(theme.BorderThickness))) e.Graphics.DrawLine(p, 0, Height - 1, Width, Height - 1);
            UpdateItemBounds();
            for (int i = 0; i < items.Count; i++)
            {
                ZarpaCommandItem item = items[i]; if (!item.Visible) continue;
                Rectangle bounds = itemBounds[i];
                if (item.Kind == ZarpaCommandItemKind.Separator)
                {
                    using (Pen p = new Pen(theme.Border, dpiScale.Stroke(theme.BorderThickness)))
                        e.Graphics.DrawLine(p, bounds.Left + S(theme.SpacingSmall), S(theme.SpacingMedium), bounds.Left + S(theme.SpacingSmall), Height - S(theme.SpacingMedium));
                    DrawDesignSelection(e.Graphics, item, bounds);
                    continue;
                }
                if (i == hotIndex || i == pressedIndex || item.Checked)
                {
                    Color c = i == pressedIndex ? theme.Selection : item.Checked ? theme.Selection : theme.SurfaceRaised;
                    ZarpaPaint.FillRounded(e.Graphics, c, bounds, S(theme.CornerRadius));
                    ZarpaPaint.DrawRounded(e.Graphics, item.Checked ? theme.Accent : theme.Border, bounds, S(theme.CornerRadius), dpiScale.Stroke(theme.BorderThickness));
                }
                if (Focused && i == keyboardIndex) ZarpaPaint.DrawRounded(e.Graphics, theme.Accent, bounds, S(theme.CornerRadius), dpiScale.X(1.5F));
                Rectangle icon = new Rectangle(bounds.Left + S(theme.SpacingMedium), bounds.Top + (bounds.Height - S(theme.IconSize)) / 2, S(theme.IconSize), S(theme.IconSize));
                FluentIconCatalog.TryDraw(e.Graphics, item.IconKey, icon, item.Enabled ? (item.Checked ? theme.Accent : theme.TextMuted) : theme.BorderStrong, dpiScale.X(theme.IconSize - 2F));
                if (showText) TextRenderer.DrawText(e.Graphics, item.Text, Font, new Rectangle(icon.Right + S(theme.SpacingSmall), bounds.Top, bounds.Right - icon.Right - S(theme.SpacingMedium), bounds.Height), item.Enabled ? theme.Text : theme.TextMuted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                DrawDesignSelection(e.Graphics, item, bounds);
            }
        }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int next = HitTest(e.Location); if (next != hotIndex) { hotIndex = next; toolTip.SetToolTip(this, next >= 0 ? items[next].ToolTipText : string.Empty); Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hotIndex = -1; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { Focus(); pressedIndex = HitTest(e.Location); SetKeyboardIndex(pressedIndex); Capture = pressedIndex >= 0; Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); int hit = HitTest(e.Location); if (!IsDesignerHosted && e.Button == MouseButtons.Left && hit >= 0 && hit == pressedIndex && items[hit].Enabled) PerformItemClick(hit); pressedIndex = -1; Capture = false; Invalidate(); }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture && pressedIndex >= 0) { pressedIndex = -1; Invalidate(); } }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); if (!Enabled) { pressedIndex = -1; Capture = false; } Invalidate(); }
        protected override bool IsInputKey(Keys keyData) { Keys key = keyData & Keys.KeyCode; return key == Keys.Left || key == Keys.Right || base.IsInputKey(keyData); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); SetKeyboardIndex(FindCommand(-1, 1)); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); SetKeyboardIndex(-1); pressedIndex = -1; Capture = false; Invalidate(); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Left) { SetKeyboardIndex(FindCommand(keyboardIndex, -1)); e.Handled = true; }
            else if (e.KeyCode == Keys.Right) { SetKeyboardIndex(FindCommand(keyboardIndex, 1)); e.Handled = true; }
            else if (e.KeyCode == Keys.Home) { SetKeyboardIndex(FindCommand(-1, 1)); e.Handled = true; }
            else if (e.KeyCode == Keys.End) { SetKeyboardIndex(FindCommand(items.Count, -1)); e.Handled = true; }
            else if ((e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) && keyboardIndex >= 0) { PerformItemClick(keyboardIndex); e.Handled = true; }
            if (e.Handled) { e.SuppressKeyPress = true; Invalidate(); }
        }
        internal void ItemsChanged() { if (keyboardIndex >= items.Count || !IsActionable(keyboardIndex)) keyboardIndex = -1; if (designSelectedItem != null && !items.Contains(designSelectedItem)) designSelectedItem = null; Invalidate(); if (IsHandleCreated && !SuppressAccessibilityInterop) AccessibilityNotifyClients(AccessibleEvents.Reorder, 0); }
        internal void ItemChanged(ZarpaCommandItem item) { int index = items.IndexOf(item); if (keyboardIndex == index && !IsActionable(index)) keyboardIndex = FindCommand(index, 1); Invalidate(); if (IsHandleCreated && !SuppressAccessibilityInterop && index >= 0) { AccessibilityNotifyClients(AccessibleEvents.NameChange, index + 1); AccessibilityNotifyClients(AccessibleEvents.StateChange, index + 1); } }
        private bool IsActionable(int index) { return index >= 0 && index < items.Count && items[index].Visible && items[index].Enabled && items[index].Kind != ZarpaCommandItemKind.Separator; }
        private void SetKeyboardIndex(int value) { if (keyboardIndex == value) return; keyboardIndex = value; Invalidate(); if (IsHandleCreated && !SuppressAccessibilityInterop && value >= 0) AccessibilityNotifyClients(AccessibleEvents.Focus, value + 1); }
        private void PerformItemClick(int index) { if (!Enabled || !IsActionable(index)) return; items[index].PerformClick(); if (ItemClick != null) ItemClick(items[index], EventArgs.Empty); if (IsHandleCreated && !SuppressAccessibilityInterop) AccessibilityNotifyClients(AccessibleEvents.StateChange, index + 1); }
        private int FindCommand(int start, int direction) { int i = start + direction; while (i >= 0 && i < items.Count) { ZarpaCommandItem item = items[i]; if (item.Visible && item.Enabled && item.Kind != ZarpaCommandItemKind.Separator) return i; i += direction; } return start >= 0 && start < items.Count && items[start].Visible && items[start].Enabled && items[start].Kind != ZarpaCommandItemKind.Separator ? start : -1; }
        private void UpdateItemBounds() { int x = S(theme.SpacingMedium); if (itemBounds.Length != items.Count) itemBounds = new Rectangle[items.Count]; else Array.Clear(itemBounds, 0, itemBounds.Length); for (int i = 0; i < items.Count; i++) { ZarpaCommandItem item = items[i]; if (!item.Visible) continue; if (item.Kind == ZarpaCommandItemKind.Separator) { itemBounds[i] = new Rectangle(x, S(theme.SpacingSmall + 1), S(theme.SpacingLarge), Height - S((theme.SpacingSmall + 1) * 2)); x += S(theme.SpacingLarge); continue; } int tw = showText ? Math.Min(S(150), TextRenderer.MeasureText(item.Text, Font).Width + S(theme.SpacingLarge)) : 0; itemBounds[i] = new Rectangle(x, S(theme.SpacingSmall + 1), S(theme.ControlHeight) + tw, Height - S((theme.SpacingSmall + 1) * 2)); x = itemBounds[i].Right + S(theme.SpacingSmall); } }
        private Rectangle GetItemBounds(int index) { UpdateItemBounds(); return index >= 0 && index < itemBounds.Length ? itemBounds[index] : Rectangle.Empty; }
        private int HitTest(Point point) { UpdateItemBounds(); for (int i = 0; i < itemBounds.Length; i++) if (items[i].Kind != ZarpaCommandItemKind.Separator && itemBounds[i].Contains(point)) return i; return -1; }
        internal int DesignHitTest(Point point) { UpdateItemBounds(); for (int i = 0; i < itemBounds.Length; i++) if (items[i].Visible && itemBounds[i].Contains(point)) return i; return -1; }
        internal void ActivateDesignItem(ZarpaCommandItem item) { designSelectedItem = item != null && items.Contains(item) ? item : null; Invalidate(); }
        private void DrawDesignSelection(Graphics graphics, ZarpaCommandItem item, Rectangle itemBoundsValue)
        {
            if (!IsDesignerHosted || item != designSelectedItem || itemBoundsValue.IsEmpty) return;
            Rectangle selectionBounds = itemBoundsValue;
            selectionBounds.Inflate(-1, -1);
            ZarpaPaint.DrawRounded(graphics, theme.Accent, selectionBounds, S(theme.CornerRadius), dpiScale.X(1.5F));
        }
        private bool IsDesignerHosted { get { return Site != null && Site.DesignMode; } }
        protected override AccessibleObject CreateAccessibilityInstance() { return new CommandBarAccessibleObject(this); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ApplyDpiScale(ZarpaDpiScale.FromControl(this)); }
        internal void ApplyDpiForTest(int dpi) { ApplyDpiScale(new ZarpaDpiScale(dpi, dpi)); }
        private void ApplyDpiScale(ZarpaDpiScale value) { if (value == null || (dpiScale.DpiX == value.DpiX && dpiScale.DpiY == value.DpiY)) return; dpiScale = value; Height = S(theme.ControlHeight + theme.SpacingMedium + theme.SpacingSmall); ItemsChanged(); }
        private sealed class CommandBarAccessibleObject : ControlAccessibleObject
        {
            private readonly ZarpaCommandBar bar;
            private readonly Dictionary<ZarpaCommandItem, AccessibleObject> children = new Dictionary<ZarpaCommandItem, AccessibleObject>();
            internal CommandBarAccessibleObject(ZarpaCommandBar owner) : base(owner) { bar = owner; }
            public override string Name { get { return !string.IsNullOrEmpty(bar.AccessibleName) ? bar.AccessibleName : "Barra de comandos"; } set { bar.AccessibleName = value; } }
            public override AccessibleRole Role { get { return bar.AccessibleRole; } }
            public override int GetChildCount() { return bar.items.Count; }
            public override AccessibleObject GetChild(int index) { if (index < 0 || index >= bar.items.Count) return null; ZarpaCommandItem item = bar.items[index]; AccessibleObject child; if (!children.TryGetValue(item, out child)) { child = new CommandItemAccessibleObject(bar, item, this); children[item] = child; } return child; }
        }
        private sealed class CommandItemAccessibleObject : AccessibleObject
        {
            private readonly ZarpaCommandBar bar; private readonly ZarpaCommandItem item; private readonly AccessibleObject parent;
            internal CommandItemAccessibleObject(ZarpaCommandBar owner, ZarpaCommandItem model, AccessibleObject parentObject) { bar = owner; item = model; parent = parentObject; }
            private int Index { get { return bar.items.IndexOf(item); } }
            public override string Name { get { return item.Kind == ZarpaCommandItemKind.Separator ? string.Empty : item.Text; } set { item.Text = value ?? string.Empty; } }
            public override string Description { get { return item.ToolTipText; } }
            public override AccessibleObject Parent { get { return parent; } }
            public override AccessibleRole Role { get { return item.Kind == ZarpaCommandItemKind.Separator ? AccessibleRole.Separator : item.Kind == ZarpaCommandItemKind.Toggle ? AccessibleRole.CheckButton : AccessibleRole.PushButton; } }
            public override string DefaultAction { get { return item.Kind == ZarpaCommandItemKind.Toggle ? (item.Checked ? "Desmarcar" : "Marcar") : item.Kind == ZarpaCommandItemKind.Button ? "Presionar" : string.Empty; } }
            public override Rectangle Bounds { get { int index = Index; if (index < 0 || !item.Visible || !bar.Visible || !bar.IsHandleCreated || bar.IsDisposed) return Rectangle.Empty; Rectangle bounds = Rectangle.Intersect(bar.ClientRectangle, bar.GetItemBounds(index)); return bounds.IsEmpty ? Rectangle.Empty : bar.RectangleToScreen(bounds); } }
            public override AccessibleStates State { get { int index = Index; AccessibleStates state = item.Kind == ZarpaCommandItemKind.Separator ? AccessibleStates.ReadOnly : AccessibleStates.Focusable; if (index < 0 || !item.Enabled || !bar.Enabled || bar.IsDisposed) state |= AccessibleStates.Unavailable; if (!item.Visible || !bar.Visible || index < 0) state |= AccessibleStates.Invisible; else if (Bounds.IsEmpty) state |= AccessibleStates.Offscreen; if (item.Kind == ZarpaCommandItemKind.Toggle && item.Checked) state |= AccessibleStates.Checked; if (bar.Focused && index == bar.keyboardIndex) state |= AccessibleStates.Focused; if (index == bar.pressedIndex) state |= AccessibleStates.Pressed; if (index == bar.hotIndex) state |= AccessibleStates.HotTracked; return state; } }
            public override void DoDefaultAction() { int index = Index; if (!bar.IsActionable(index) || !bar.Enabled || bar.IsDisposed) return; if (!bar.SuppressAccessibilityInterop) bar.Focus(); bar.SetKeyboardIndex(index); bar.PerformItemClick(index); }
            public override AccessibleObject Navigate(AccessibleNavigation navdir) { int index = Index; int direction = navdir == AccessibleNavigation.Next || navdir == AccessibleNavigation.Right ? 1 : navdir == AccessibleNavigation.Previous || navdir == AccessibleNavigation.Left ? -1 : 0; int next = index + direction; return direction != 0 && next >= 0 && next < bar.items.Count ? parent.GetChild(next) : null; }
        }
    }

    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(StatusStrip))]
    public class ZarpaStatusBar : Control, IZarpaThemeAware
    {
        private ZarpaThemeTokens theme;
        private string statusText = "Listo", detailText = string.Empty;
        private ZarpaStatusKind statusKind;
        private int progress = -1;
        private ZarpaDpiScale dpiScale = new ZarpaDpiScale(96, 96);
        internal bool SuppressAccessibilityInterop { get; set; }
        private int S(int logicalPixels) { return dpiScale.X(logicalPixels); }
        public ZarpaStatusBar() { theme = new ZarpaThemeTokens(Invalidate); Height = S(28); Dock = DockStyle.Bottom; AccessibleRole = AccessibleRole.StatusBar; SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true); }
        [Category("Estado"), DefaultValue("Listo")]
        public string StatusText { get { return statusText; } set { string next = value ?? string.Empty; if (statusText == next) return; statusText = next; Invalidate(); NotifyChild(AccessibleEvents.NameChange, 1); } }
        [Category("Estado"), DefaultValue("")]
        public string DetailText { get { return detailText; } set { string next = value ?? string.Empty; if (detailText == next) return; detailText = next; Invalidate(); NotifyChild(AccessibleEvents.NameChange, 2); } }
        [Category("Estado"), DefaultValue(ZarpaStatusKind.Neutral)]
        public ZarpaStatusKind StatusKind { get { return statusKind; } set { if (statusKind == value) return; statusKind = value; Invalidate(); NotifyChild(AccessibleEvents.StateChange, 1); } }
        [Category("Estado"), DefaultValue(-1)]
        public int Progress { get { return progress; } set { int next = value < -1 ? -1 : value > 100 ? 100 : value; if (progress == next) return; progress = next; Invalidate(); NotifyChild(AccessibleEvents.ValueChange, 3); } }
        public void ApplyTheme(ZarpaThemeTokens value) { if (value == null) return; theme = value; Font = new Font(theme.FontFamily, theme.FontSize); Height = Math.Max(S(28), S(theme.ControlHeight - theme.SpacingSmall)); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); e.Graphics.Clear(theme.Surface); using (Pen p = new Pen(theme.Border, dpiScale.Stroke(1))) e.Graphics.DrawLine(p, 0, 0, Width, 0); Color state = statusKind == ZarpaStatusKind.Success ? theme.Success : statusKind == ZarpaStatusKind.Warning ? theme.Warning : statusKind == ZarpaStatusKind.Error ? theme.Danger : statusKind == ZarpaStatusKind.Information || statusKind == ZarpaStatusKind.Busy ? theme.Information : theme.TextMuted; using (SolidBrush b = new SolidBrush(state)) e.Graphics.FillEllipse(b, S(12), S(10), S(7), S(7)); TextRenderer.DrawText(e.Graphics, statusText, Font, new Rectangle(S(26), S(1), Math.Max(S(20), Width / 2), Height - S(2)), theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis); TextRenderer.DrawText(e.Graphics, detailText, Font, new Rectangle(Width / 2, S(1), Width / 2 - S(14), Height - S(2)), theme.TextMuted, TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis); if (progress >= 0) using (SolidBrush b = new SolidBrush(theme.Accent)) e.Graphics.FillRectangle(b, 0, 0, (int)(Width * progress / 100F), S(2)); }
        private void NotifyChild(AccessibleEvents accessibleEvent, int childId) { if (IsHandleCreated && !SuppressAccessibilityInterop) AccessibilityNotifyClients(accessibleEvent, childId); }
        protected override AccessibleObject CreateAccessibilityInstance() { return new StatusBarAccessibleObject(this); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); ApplyDpiScale(ZarpaDpiScale.FromControl(this)); }
        internal void ApplyDpiForTest(int dpi) { ApplyDpiScale(new ZarpaDpiScale(dpi, dpi)); }
        private void ApplyDpiScale(ZarpaDpiScale value) { if (value == null || (dpiScale.DpiX == value.DpiX && dpiScale.DpiY == value.DpiY)) return; dpiScale = value; Height = Math.Max(S(28), S(theme.ControlHeight - theme.SpacingSmall)); Invalidate(); }
        private sealed class StatusBarAccessibleObject : ControlAccessibleObject
        {
            private readonly ZarpaStatusBar bar; private readonly AccessibleObject[] children;
            internal StatusBarAccessibleObject(ZarpaStatusBar owner) : base(owner) { bar = owner; children = new AccessibleObject[] { new StatusItemAccessibleObject(owner, this, 0), new StatusItemAccessibleObject(owner, this, 1), new StatusItemAccessibleObject(owner, this, 2) }; }
            public override string Name { get { return !string.IsNullOrEmpty(bar.AccessibleName) ? bar.AccessibleName : "Barra de estado"; } set { bar.AccessibleName = value; } }
            public override AccessibleRole Role { get { return AccessibleRole.StatusBar; } }
            public override int GetChildCount() { return children.Length; }
            public override AccessibleObject GetChild(int index) { return index >= 0 && index < children.Length ? children[index] : null; }
        }
        private sealed class StatusItemAccessibleObject : AccessibleObject
        {
            private readonly ZarpaStatusBar bar; private readonly AccessibleObject parent; private readonly int kind;
            internal StatusItemAccessibleObject(ZarpaStatusBar owner, AccessibleObject parentObject, int itemKind) { bar = owner; parent = parentObject; kind = itemKind; }
            public override string Name { get { return kind == 0 ? bar.statusText : kind == 1 ? bar.detailText : "Progreso"; } set { } }
            public override string Description { get { if (kind != 0) return string.Empty; switch (bar.statusKind) { case ZarpaStatusKind.Information: return "Información"; case ZarpaStatusKind.Success: return "Correcto"; case ZarpaStatusKind.Warning: return "Advertencia"; case ZarpaStatusKind.Error: return "Error"; case ZarpaStatusKind.Busy: return "Ocupado"; default: return "Neutral"; } } }
            public override string Value { get { return kind == 2 && bar.progress >= 0 ? bar.progress + " %" : string.Empty; } set { } }
            public override AccessibleRole Role { get { return kind == 2 ? AccessibleRole.ProgressBar : AccessibleRole.StaticText; } }
            public override AccessibleObject Parent { get { return parent; } }
            public override Rectangle Bounds { get { if (!bar.Visible || !bar.IsHandleCreated || bar.IsDisposed || (kind == 1 && string.IsNullOrEmpty(bar.detailText)) || (kind == 2 && bar.progress < 0)) return Rectangle.Empty; Rectangle bounds = kind == 0 ? new Rectangle(bar.S(8), bar.S(1), Math.Max(bar.S(20), bar.Width / 2), bar.Height - bar.S(2)) : kind == 1 ? new Rectangle(bar.Width / 2, bar.S(1), Math.Max(bar.S(1), bar.Width / 2 - bar.S(14)), bar.Height - bar.S(2)) : new Rectangle(0, 0, bar.Width, bar.S(2)); bounds = Rectangle.Intersect(bar.ClientRectangle, bounds); return bounds.IsEmpty ? Rectangle.Empty : bar.RectangleToScreen(bounds); } }
            public override AccessibleStates State { get { AccessibleStates state = AccessibleStates.ReadOnly; if (!bar.Enabled || bar.IsDisposed) state |= AccessibleStates.Unavailable; if (!bar.Visible || (kind == 1 && string.IsNullOrEmpty(bar.detailText)) || (kind == 2 && bar.progress < 0)) state |= AccessibleStates.Invisible; else if (Bounds.IsEmpty) state |= AccessibleStates.Offscreen; if (kind == 0 && bar.statusKind == ZarpaStatusKind.Busy) state |= AccessibleStates.Busy; return state; } }
        }
    }

    [ToolboxItem(false)]
    [ToolboxBitmap(typeof(ToolStripMenuItem))]
    public class ZarpaBreadcrumbItem : Component
    {
        private string text = "Nivel", key = string.Empty;
        public event EventHandler Changed;
        [Category("Datos"), DefaultValue("Nivel")] public string Text { get { return text; } set { text = value ?? string.Empty; if (Changed != null) Changed(this, EventArgs.Empty); } }
        [Category("Datos"), DefaultValue("")] public string Key { get { return key; } set { key = value ?? string.Empty; } }
        public override string ToString() { return Text; }
    }
    public sealed class ZarpaBreadcrumbCollection : Collection<ZarpaBreadcrumbItem>
    {
        private readonly ZarpaBreadcrumb owner; internal ZarpaBreadcrumbCollection(ZarpaBreadcrumb c) { owner = c; }
        protected override void InsertItem(int i, ZarpaBreadcrumbItem v) { if (v == null) throw new ArgumentNullException("v"); base.InsertItem(i, v); v.Changed += Changed; owner.Invalidate(); }
        protected override void SetItem(int i, ZarpaBreadcrumbItem v) { if (v == null) throw new ArgumentNullException("v"); this[i].Changed -= Changed; base.SetItem(i, v); v.Changed += Changed; owner.Invalidate(); }
        protected override void RemoveItem(int i) { this[i].Changed -= Changed; base.RemoveItem(i); owner.Invalidate(); }
        protected override void ClearItems() { foreach (ZarpaBreadcrumbItem v in this) v.Changed -= Changed; base.ClearItems(); owner.Invalidate(); }
        private void Changed(object s, EventArgs e) { owner.Invalidate(); }
        public void AddRange(ZarpaBreadcrumbItem[] values) { if (values != null) foreach (ZarpaBreadcrumbItem v in values) Add(v); }
    }
    [ToolboxItem(true), DefaultProperty("Items"), DefaultEvent("ItemClick")]
    [ToolboxBitmap(typeof(ToolStrip))]
    [Designer("ZarpaSuite.Controls.Design.ZarpaBreadcrumbDesigner, Zarpa.Controls")]
    public class ZarpaBreadcrumb : Control, IZarpaThemeAware
    {
        private ZarpaThemeTokens theme; private readonly ZarpaBreadcrumbCollection items; private int hotIndex = -1;
        public ZarpaBreadcrumb() { theme = new ZarpaThemeTokens(Invalidate); items = new ZarpaBreadcrumbCollection(this); Height = 38; Dock = DockStyle.Top; SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true); }
        [Category("Datos"), DesignerSerializationVisibility(DesignerSerializationVisibility.Content), Editor("TestRibbon.Controls.ZarpaBreadcrumbCollectionEditor, Zarpa.Controls", typeof(UITypeEditor))]
        public ZarpaBreadcrumbCollection Items { get { return items; } }
        public event EventHandler ItemClick;
        public void ApplyTheme(ZarpaThemeTokens value) { if (value == null) return; theme = value; Font = new Font(theme.FontFamily, theme.FontSize); Height = theme.ControlHeight + theme.SpacingSmall; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); e.Graphics.Clear(theme.Canvas); int x = theme.SpacingMedium; for (int i = 0; i < items.Count; i++) { int w = Math.Min(180, TextRenderer.MeasureText(items[i].Text, Font).Width + theme.SpacingLarge); Rectangle b = new Rectangle(x, theme.SpacingSmall, w, Height - theme.SpacingMedium); if (i == hotIndex) ZarpaPaint.FillRounded(e.Graphics, theme.SurfaceRaised, b, theme.CornerRadius); TextRenderer.DrawText(e.Graphics, items[i].Text, Font, b, i == items.Count - 1 ? theme.Text : theme.TextMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis); x = b.Right; if (i < items.Count - 1) { TextRenderer.DrawText(e.Graphics, ">", Font, new Rectangle(x, theme.SpacingSmall, theme.SpacingLarge, Height - theme.SpacingMedium), theme.BorderStrong, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); x += theme.SpacingLarge; } } }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int n = HitTest(e.Location); if (n != hotIndex) { hotIndex = n; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hotIndex = -1; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); int h = HitTest(e.Location); if (!IsDesignerHosted && e.Button == MouseButtons.Left && h >= 0 && ItemClick != null) ItemClick(items[h], EventArgs.Empty); }
        private int HitTest(Point p) { int x = theme.SpacingMedium; for (int i = 0; i < items.Count; i++) { int w = Math.Min(180, TextRenderer.MeasureText(items[i].Text, Font).Width + theme.SpacingLarge); Rectangle b = new Rectangle(x, theme.SpacingSmall, w, Height - theme.SpacingMedium); if (b.Contains(p)) return i; x += w + (i < items.Count - 1 ? theme.SpacingLarge : 0); } return -1; }
        internal int DesignHitTest(Point point) { return HitTest(point); }
        private bool IsDesignerHosted { get { return Site != null && Site.DesignMode; } }
    }
}
