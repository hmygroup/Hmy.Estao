using System;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;

namespace ZarpaSuite.Controls
{
    internal sealed class ZarpaPopupController : IDisposable
    {
        private readonly Control owner;
        private readonly ToolStripDropDown popup;
        private readonly System.Collections.Generic.List<Control> observed = new System.Collections.Generic.List<Control>();
        private Form form;
        private bool tracking, disposed;

        internal ZarpaPopupController(Control owner, ToolStripDropDown popup)
        {
            this.owner = owner;
            this.popup = popup;
            popup.Closed += PopupClosed;
        }

        internal bool IsOpen { get { return popup.Visible; } }

        internal void Show(Control anchor, Rectangle anchorBounds)
        {
            if (disposed || owner.IsDisposed || anchor == null || anchor.IsDisposed || !owner.Enabled || !owner.Visible) return;
            Close();
            BeginTracking();
            Size popupSize = popup.GetPreferredSize(Size.Empty);
            if (popupSize.Width <= 0 || popupSize.Height <= 0) popupSize = popup.Size;
            Point below = anchor.PointToScreen(new Point(anchorBounds.Left, anchorBounds.Bottom + 2));
            Rectangle work = Screen.FromControl(anchor).WorkingArea;
            int x = Math.Max(work.Left, Math.Min(below.X, work.Right - Math.Max(1, popupSize.Width)));
            int y = below.Y;
            if (y + popupSize.Height > work.Bottom)
            {
                Point above = anchor.PointToScreen(new Point(anchorBounds.Left, anchorBounds.Top - popupSize.Height - 2));
                y = Math.Max(work.Top, above.Y);
            }
            popup.Show(anchor, anchor.PointToClient(new Point(x, y)));
        }

        internal void Close()
        {
            if (popup.Visible) popup.Close(ToolStripDropDownCloseReason.AppClicked);
            EndTracking();
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Close();
            popup.Closed -= PopupClosed;
        }

        private void BeginTracking()
        {
            if (tracking) return;
            tracking = true;
            Control current = owner;
            while (current != null)
            {
                observed.Add(current);
                current.LocationChanged += OwnerGeometryChanged;
                current.SizeChanged += OwnerGeometryChanged;
                current.VisibleChanged += OwnerStateChanged;
                current.ParentChanged += OwnerGeometryChanged;
                ScrollableControl scrollable = current as ScrollableControl;
                if (scrollable != null) scrollable.Scroll += AncestorScrolled;
                current = current.Parent;
            }
            owner.MouseWheel += OwnerMouseWheel;
            owner.EnabledChanged += OwnerStateChanged;
            owner.HandleDestroyed += OwnerHandleDestroyed;
            form = owner.FindForm();
            if (form != null) form.FormClosed += FormClosed;
        }

        private void EndTracking()
        {
            if (!tracking) return;
            tracking = false;
            foreach (Control control in observed)
            {
                control.LocationChanged -= OwnerGeometryChanged;
                control.SizeChanged -= OwnerGeometryChanged;
                control.VisibleChanged -= OwnerStateChanged;
                control.ParentChanged -= OwnerGeometryChanged;
                ScrollableControl scrollable = control as ScrollableControl;
                if (scrollable != null) scrollable.Scroll -= AncestorScrolled;
            }
            observed.Clear();
            owner.MouseWheel -= OwnerMouseWheel;
            owner.EnabledChanged -= OwnerStateChanged;
            owner.HandleDestroyed -= OwnerHandleDestroyed;
            if (form != null) { form.FormClosed -= FormClosed; form = null; }
        }

        private void PopupClosed(object sender, ToolStripDropDownClosedEventArgs e) { EndTracking(); }
        private void OwnerGeometryChanged(object sender, EventArgs e) { Close(); }
        private void OwnerStateChanged(object sender, EventArgs e) { if (!owner.Visible || !owner.Enabled) Close(); }
        private void OwnerMouseWheel(object sender, MouseEventArgs e) { Close(); }
        private void AncestorScrolled(object sender, ScrollEventArgs e) { Close(); }
        private void OwnerHandleDestroyed(object sender, EventArgs e) { Close(); }
        private void FormClosed(object sender, FormClosedEventArgs e) { Close(); }
    }

    internal abstract class ZarpaPopupEditor : Control
    {
        protected ZarpaThemeTokens theme;
        protected ZarpaPopupEditor()
        {
            theme = new ZarpaThemeTokens(Invalidate);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.Selectable | ControlStyles.UserPaint, true);
            TabStop = true;
            Cursor = Cursors.Hand;
        }

        internal virtual void ApplyTheme(ZarpaThemeTokens value)
        {
            if (value == null) return;
            theme = value;
            Font = new Font(theme.FontFamily, theme.FontSize);
            BackColor = theme.Surface;
            ForeColor = theme.Text;
            Invalidate();
        }

        protected void DrawChevron(Graphics graphics)
        {
            int size = Math.Min(18, Height - 4);
            Rectangle icon = new Rectangle(Width - size - 2, (Height - size) / 2, size, size);
            if (!FluentIconCatalog.TryDraw(graphics, "ic_fluent_chevron_down_20_regular", icon,
                Enabled ? theme.TextMuted : theme.BorderStrong, Math.Max(12F, size - 3F)))
                TextRenderer.DrawText(graphics, "⌄", Font, icon, theme.TextMuted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected void ResolvePopupAnchor(out Control anchor, out Rectangle bounds)
        {
            ZarpaFieldBase field = Parent as ZarpaFieldBase;
            if (field != null) { anchor = field; bounds = field.PopupAnchorBounds; return; }
            if (Parent != null) { anchor = Parent; bounds = new Rectangle(1, 1, Math.Max(1, Parent.ClientSize.Width - 2), Math.Max(1, Parent.ClientSize.Height - 2)); return; }
            anchor = this; bounds = ClientRectangle;
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Up || key == Keys.Down || key == Keys.Home || key == Keys.End || base.IsInputKey(keyData);
        }
    }

    internal sealed class ZarpaComboEditor : ZarpaPopupEditor
    {
        private readonly ComboBox store;
        private readonly TextBox textEditor;
        private readonly ListBox list;
        private readonly ToolStripDropDown dropDown;
        private readonly ToolStripControlHost filterHost;
        private readonly ToolStripControlHost listHost;
        private readonly ZarpaPopupController popupController;
        private int hotIndex = -1;
        private bool syncingText;

        internal ZarpaComboEditor()
        {
            store = new ComboBox
            {
                BindingContext = new BindingContext(),
                FlatStyle = FlatStyle.Flat
            };
            store.SelectedIndexChanged += delegate { Invalidate(); if (SelectedIndexChanged != null) SelectedIndexChanged(this, EventArgs.Empty); };
            store.TextChanged += delegate { Invalidate(); if (TextChanged != null) TextChanged(this, EventArgs.Empty); };
            textEditor = new TextBox
            {
                BorderStyle = BorderStyle.None,
                Margin = Padding.Empty
            };
            textEditor.TextChanged += TextEditorTextChanged;
            textEditor.KeyDown += TextEditorKeyDown;
            list = new ListBox
            {
                BorderStyle = BorderStyle.None,
                DrawMode = DrawMode.OwnerDrawFixed,
                IntegralHeight = false
            };
            list.DrawItem += DrawListItem;
            list.MouseMove += ListMouseMove;
            list.MouseLeave += delegate { SetHotIndex(-1); };
            list.MouseUp += ListMouseUp;
            filterHost = new ToolStripControlHost(textEditor) { AutoSize = false, Margin = Padding.Empty, Padding = new Padding(8, 7, 8, 5) };
            listHost = new ToolStripControlHost(list) { AutoSize = false, Margin = Padding.Empty, Padding = Padding.Empty };
            dropDown = new ToolStripDropDown { AutoClose = true, Padding = new Padding(1) };
            dropDown.Items.Add(filterHost);
            dropDown.Items.Add(listHost);
            popupController = new ZarpaPopupController(this, dropDown);
            list.KeyDown += ListKeyDown;
            AccessibleRole = AccessibleRole.ComboBox;
        }

        internal ComboBox.ObjectCollection Items { get { return store.Items; } }
        internal object DataSource { get { return store.DataSource; } set { store.DataSource = value; } }
        internal string DisplayMember { get { return store.DisplayMember; } set { store.DisplayMember = value ?? string.Empty; list.DisplayMember = store.DisplayMember; } }
        internal string ValueMember { get { return store.ValueMember; } set { store.ValueMember = value ?? string.Empty; list.ValueMember = store.ValueMember; } }
        internal int SelectedIndex { get { return store.SelectedIndex; } set { store.SelectedIndex = value; } }
        internal object SelectedItem { get { return store.SelectedItem; } }
        internal ComboBoxStyle DropDownStyle { get { return store.DropDownStyle; } set { store.DropDownStyle = value; Invalidate(); } }
        internal new string Text { get { return store.Text; } set { store.Text = value ?? string.Empty; Invalidate(); } }
        internal event EventHandler SelectedIndexChanged;
        internal new event EventHandler TextChanged;
        internal void BeginUpdate() { store.BeginUpdate(); }
        internal void EndUpdate() { store.EndUpdate(); Invalidate(); }

        internal override void ApplyTheme(ZarpaThemeTokens value)
        {
            base.ApplyTheme(value);
            list.Font = Font;
            textEditor.Font = Font;
            textEditor.BackColor = BackColor;
            textEditor.ForeColor = ForeColor;
            list.BackColor = theme.SurfaceOverlay;
            list.ForeColor = theme.Text;
            dropDown.BackColor = theme.BorderStrong;
            filterHost.BackColor = theme.Surface;
            listHost.BackColor = theme.SurfaceOverlay;
            list.Invalidate();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { popupController.Dispose(); dropDown.Dispose(); store.Dispose(); }
            base.Dispose(disposing);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            string text = store.SelectedItem == null ? store.Text : store.GetItemText(store.SelectedItem);
            TextRenderer.DrawText(e.Graphics, text, Font, new Rectangle(1, 0, Math.Max(1, Width - 25), Height),
                Enabled ? ForeColor : theme.TextMuted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            DrawChevron(e.Graphics);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        { base.OnMouseDown(e); if (Enabled && e.Button == MouseButtons.Left) Focus(); }
        protected override void OnMouseUp(MouseEventArgs e)
        { base.OnMouseUp(e); if (Enabled && e.Button == MouseButtons.Left) ShowDropDown(); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.F4 || (e.Alt && e.KeyCode == Keys.Down) || e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
            { ShowDropDown(); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape && popupController.IsOpen) { popupController.Close(); e.Handled = true; }
            else if (e.KeyCode == Keys.Home && store.Items.Count > 0) { store.SelectedIndex = 0; e.Handled = true; }
            else if (e.KeyCode == Keys.End && store.Items.Count > 0) { store.SelectedIndex = store.Items.Count - 1; e.Handled = true; }
            else if (e.KeyCode == Keys.Up && store.Items.Count > 0) { store.SelectedIndex = Math.Max(0, store.SelectedIndex - 1); e.Handled = true; }
            else if (e.KeyCode == Keys.Down && store.Items.Count > 0) { store.SelectedIndex = Math.Min(store.Items.Count - 1, store.SelectedIndex + 1); e.Handled = true; }
        }

        internal void OpenDropDown()
        {
            if (!popupController.IsOpen) ShowDropDown(string.Empty, true);
        }

        private void ShowDropDown()
        {
            ShowDropDown(string.Empty, true);
        }

        private void ShowDropDown(string filter, bool focusList)
        {
            if (LicenseManager.UsageMode == LicenseUsageMode.Designtime || store.Items.Count == 0) return;
            if (popupController.IsOpen) return;
            syncingText = true;
            try { textEditor.Text = filter ?? string.Empty; }
            finally { syncingText = false; }
            PopulateList(filter);
            ShowPopulatedDropDown(focusList);
        }

        private void ShowPopulatedDropDown(bool focusList)
        {
            list.ItemHeight = Math.Max(30, Font.Height + 13);
            int visible = Math.Min(8, Math.Max(1, list.Items.Count));
            Control anchor; Rectangle anchorBounds; ResolvePopupAnchor(out anchor, out anchorBounds);
            int popupWidth = Math.Max(anchorBounds.Width - 2, 160);
            filterHost.Size = new Size(popupWidth, Math.Max(34, textEditor.PreferredHeight + 12));
            listHost.Size = new Size(popupWidth, visible * list.ItemHeight + 4);
            popupController.Show(anchor, anchorBounds);
            if (list.SelectedIndex >= 0)
                list.TopIndex = Math.Max(0, Math.Min(list.SelectedIndex, list.Items.Count - visible));
            if (focusList) { textEditor.Focus(); textEditor.SelectAll(); }
        }

        private void SelectFromList(object sender, EventArgs e)
        {
            if (list.SelectedIndex >= 0) store.SelectedItem = list.SelectedItem;
            dropDown.Close(ToolStripDropDownCloseReason.ItemClicked);
            Focus();
        }

        private void ListMouseUp(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            int index = list.IndexFromPoint(e.Location);
            if (index < 0 || index >= list.Items.Count) return;
            list.SelectedIndex = index;
            SelectFromList(sender, EventArgs.Empty);
        }

        private void ListMouseMove(object sender, MouseEventArgs e)
        {
            int index = list.IndexFromPoint(e.Location);
            SetHotIndex(index >= 0 && index < list.Items.Count ? index : -1);
        }

        private void SetHotIndex(int value)
        {
            if (hotIndex == value) return;
            int previous = hotIndex;
            hotIndex = value;
            if (previous >= 0) list.Invalidate(list.GetItemRectangle(previous));
            if (hotIndex >= 0) list.Invalidate(list.GetItemRectangle(hotIndex));
        }

        private void DrawListItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= list.Items.Count) return;
            bool selected = (e.State & DrawItemState.Selected) == DrawItemState.Selected;
            bool hot = e.Index == hotIndex;
            Color background = selected ? theme.Accent : hot ? theme.SurfaceRaised : theme.SurfaceOverlay;
            Color foreground = selected ? Color.White : theme.Text;
            using (SolidBrush fill = new SolidBrush(background)) e.Graphics.FillRectangle(fill, e.Bounds);
            Rectangle checkBounds = new Rectangle(e.Bounds.Left + 7, e.Bounds.Top + (e.Bounds.Height - 18) / 2, 18, 18);
            if (selected)
                FluentIconCatalog.TryDraw(e.Graphics, "ic_fluent_checkmark_20_regular", checkBounds, Color.White, 16F);
            Rectangle textBounds = new Rectangle(e.Bounds.Left + 31, e.Bounds.Top,
                Math.Max(1, e.Bounds.Width - 39), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, list.GetItemText(list.Items[e.Index]), Font, textBounds, foreground,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (!selected && e.Index < list.Items.Count - 1)
                using (Pen separator = new Pen(theme.Border)) e.Graphics.DrawLine(separator,
                    e.Bounds.Left + 31, e.Bounds.Bottom - 1, e.Bounds.Right - 7, e.Bounds.Bottom - 1);
        }

        private void ListKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { SelectFromList(sender, EventArgs.Empty); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape) { popupController.Close(); Focus(); e.Handled = true; }
        }

        private void TextEditorTextChanged(object sender, EventArgs e)
        {
            if (syncingText) return;
            PopulateList(textEditor.Text);
            if (!popupController.IsOpen) return;
            int visible = Math.Min(8, Math.Max(1, list.Items.Count));
            listHost.Height = visible * list.ItemHeight + 4;
            dropDown.PerformLayout();
        }

        private void TextEditorKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Down && list.Items.Count > 0)
            {
                list.SelectedIndex = Math.Max(0, list.SelectedIndex);
                list.Focus();
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Enter && list.Items.Count > 0)
            {
                if (list.SelectedIndex < 0) list.SelectedIndex = 0;
                SelectFromList(sender, EventArgs.Empty);
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Escape && popupController.IsOpen) { popupController.Close(); e.Handled = true; }
        }

        private void PopulateList(string filter)
        {
            object selected = store.SelectedItem;
            list.BeginUpdate();
            list.Items.Clear();
            foreach (object item in store.Items)
            {
                string itemText = store.GetItemText(item);
                if (string.IsNullOrEmpty(filter) || itemText.IndexOf(filter, StringComparison.CurrentCultureIgnoreCase) >= 0)
                    list.Items.Add(item);
            }
            list.SelectedItem = selected;
            list.EndUpdate();
        }

    }

    internal sealed class ZarpaDateEditor : ZarpaPopupEditor
    {
        private readonly MonthCalendar calendar;
        private readonly ToolStripDropDown dropDown;
        private readonly ZarpaPopupController popupController;
        private DateTime value = DateTime.Today, minDate = DateTimePicker.MinimumDateTime, maxDate = DateTimePicker.MaximumDateTime;
        private string customFormat = "dd/MM/yyyy";
        private DateTimePickerFormat format = DateTimePickerFormat.Custom;
        private bool showCheckBox, isChecked = true;

        internal ZarpaDateEditor()
        {
            calendar = new MonthCalendar { MaxSelectionCount = 1, ShowTodayCircle = true };
            calendar.DateSelected += CalendarDateSelected;
            ToolStripControlHost host = new ToolStripControlHost(calendar) { AutoSize = true, Margin = Padding.Empty, Padding = Padding.Empty };
            dropDown = new ToolStripDropDown { AutoClose = true, Padding = new Padding(1) };
            dropDown.Items.Add(host);
            popupController = new ZarpaPopupController(this, dropDown);
            AccessibleRole = AccessibleRole.DropList;
        }

        internal DateTime Value { get { return value; } set { DateTime next = Clamp(value); if (this.value != next) { this.value = next; Invalidate(); if (ValueChanged != null) ValueChanged(this, EventArgs.Empty); } } }
        internal DateTime MinDate { get { return minDate; } set { if (value > maxDate) throw new ArgumentOutOfRangeException("value", "MinDate no puede ser posterior a MaxDate."); minDate = value; calendar.MinDate = value; Value = this.value; } }
        internal DateTime MaxDate { get { return maxDate; } set { if (value < minDate) throw new ArgumentOutOfRangeException("value", "MaxDate no puede ser anterior a MinDate."); maxDate = value; calendar.MaxDate = value; Value = this.value; } }
        internal string CustomFormat { get { return customFormat; } set { customFormat = string.IsNullOrEmpty(value) ? "dd/MM/yyyy" : value; Invalidate(); } }
        internal DateTimePickerFormat Format { get { return format; } set { format = value; Invalidate(); } }
        internal bool ShowCheckBox { get { return showCheckBox; } set { showCheckBox = value; Invalidate(); } }
        internal bool Checked { get { return isChecked; } set { if (isChecked != value) { isChecked = value; Invalidate(); if (ValueChanged != null) ValueChanged(this, EventArgs.Empty); } } }
        internal event EventHandler ValueChanged;

        internal override void ApplyTheme(ZarpaThemeTokens value)
        {
            base.ApplyTheme(value);
            calendar.BackColor = theme.SurfaceOverlay;
            calendar.ForeColor = theme.Text;
            calendar.TitleBackColor = theme.Accent;
            calendar.TitleForeColor = Color.White;
            calendar.TrailingForeColor = theme.TextMuted;
            dropDown.BackColor = theme.Border;
        }

        protected override void Dispose(bool disposing) { if (disposing) { popupController.Dispose(); dropDown.Dispose(); } base.Dispose(disposing); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor);
            int left = 1;
            if (showCheckBox)
            {
                Rectangle check = new Rectangle(1, (Height - 18) / 2, 18, 18);
                ZarpaPaint.FillRounded(e.Graphics, isChecked ? theme.Accent : theme.Surface, check, 4);
                ZarpaPaint.DrawRounded(e.Graphics, isChecked ? theme.Accent : theme.BorderStrong, check, 4, 1);
                if (isChecked) TextRenderer.DrawText(e.Graphics, "✓", Font, check, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                left = 25;
            }
            string pattern = format == DateTimePickerFormat.Long ? "D" : format == DateTimePickerFormat.Short ? "d" :
                format == DateTimePickerFormat.Time ? "t" : customFormat;
            string text = !showCheckBox || isChecked ? value.ToString(pattern, CultureInfo.CurrentCulture) : "Sin fecha";
            TextRenderer.DrawText(e.Graphics, text, Font, new Rectangle(left, 0, Math.Max(1, Width - left - 26), Height),
                Enabled ? ForeColor : theme.TextMuted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            Rectangle icon = new Rectangle(Width - 21, (Height - 18) / 2, 18, 18);
            FluentIconCatalog.TryDraw(e.Graphics, "ic_fluent_calendar_24_regular", icon, theme.TextMuted, 16F);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e); if (Enabled && e.Button == MouseButtons.Left) Focus();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e); if (!Enabled || e.Button != MouseButtons.Left) return;
            if (showCheckBox && e.X < 24) Checked = !Checked; else ShowCalendar();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.F4 || (e.Alt && e.KeyCode == Keys.Down) || e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
            { ShowCalendar(); e.Handled = true; }
            else if (e.KeyCode == Keys.Escape && popupController.IsOpen) { popupController.Close(); e.Handled = true; }
            else if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Right) { Value = SafeAddDays(1); e.Handled = true; }
            else if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Left) { Value = SafeAddDays(-1); e.Handled = true; }
            else if (e.KeyCode == Keys.PageUp) { Value = SafeAddMonths(-1); e.Handled = true; }
            else if (e.KeyCode == Keys.PageDown) { Value = SafeAddMonths(1); e.Handled = true; }
            else if (e.KeyCode == Keys.Home) { Value = Clamp(DateTime.Today); e.Handled = true; }
        }

        private DateTime Clamp(DateTime candidate) { return candidate < minDate ? minDate : candidate > maxDate ? maxDate : candidate; }
        private DateTime SafeAddDays(int days) { try { return Clamp(value.AddDays(days)); } catch (ArgumentOutOfRangeException) { return days > 0 ? maxDate : minDate; } }
        private DateTime SafeAddMonths(int months) { try { return Clamp(value.AddMonths(months)); } catch (ArgumentOutOfRangeException) { return months > 0 ? maxDate : minDate; } }
        private void ShowCalendar()
        {
            if (LicenseManager.UsageMode == LicenseUsageMode.Designtime) return;
            calendar.SetDate(value);
            Control anchor; Rectangle bounds; ResolvePopupAnchor(out anchor, out bounds);
            popupController.Show(anchor, bounds);
        }
        private void CalendarDateSelected(object sender, DateRangeEventArgs e)
        { Value = e.Start; if (showCheckBox) Checked = true; popupController.Close(); Focus(); }
    }

    internal sealed class ZarpaNumericEditor : UserControl
    {
        private readonly TextBox textBox;
        private ZarpaThemeTokens theme;
        private decimal value, minimum, maximum = 100M, increment = 1M;
        private int decimalPlaces;
        private string prefix = string.Empty, suffix = string.Empty;
        private Rectangle upBounds, downBounds;

        internal ZarpaNumericEditor()
        {
            theme = new ZarpaThemeTokens(Invalidate);
            textBox = new TextBox { BorderStyle = BorderStyle.None, TextAlign = HorizontalAlignment.Right };
            textBox.KeyDown += TextBoxKeyDown;
            textBox.MouseWheel += TextBoxMouseWheel;
            textBox.Enter += delegate { textBox.SelectAll(); };
            textBox.Leave += delegate { CommitText(); };
            Controls.Add(textBox);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            UpdateText();
        }

        internal decimal Value { get { CommitText(); return value; } set { SetValue(value, true); } }
        internal decimal Minimum { get { return minimum; } set { minimum = value; if (maximum < minimum) maximum = minimum; SetValue(this.value, false); } }
        internal decimal Maximum { get { return maximum; } set { maximum = value; if (minimum > maximum) minimum = maximum; SetValue(this.value, false); } }
        internal decimal Increment { get { return increment; } set { increment = value <= 0 ? 1M : value; } }
        internal int DecimalPlaces { get { return decimalPlaces; } set { decimalPlaces = Math.Max(0, Math.Min(12, value)); UpdateText(); } }
        internal string Prefix { get { return prefix; } set { prefix = value ?? string.Empty; UpdateText(); } }
        internal string Suffix { get { return suffix; } set { suffix = value ?? string.Empty; UpdateText(); } }
        internal event EventHandler ValueChanged;

        internal void ApplyTheme(ZarpaThemeTokens value)
        {
            if (value == null) return; theme = value;
            Font = textBox.Font = new Font(theme.FontFamily, theme.FontSize);
            BackColor = textBox.BackColor = theme.Surface; ForeColor = textBox.ForeColor = theme.Text;
            PerformLayout(); Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e); int buttons = Math.Min(24, Math.Max(18, Height));
            textBox.Bounds = new Rectangle(0, Math.Max(0, (Height - textBox.PreferredHeight) / 2), Math.Max(8, Width - buttons - 4), textBox.PreferredHeight);
            upBounds = new Rectangle(Width - buttons, 0, buttons, Height / 2);
            downBounds = new Rectangle(Width - buttons, Height / 2, buttons, Height - Height / 2);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            using (Pen pen = new Pen(theme.Border)) e.Graphics.DrawLine(pen, upBounds.Left, 2, upBounds.Left, Height - 3);
            DrawArrow(e.Graphics, upBounds, true); DrawArrow(e.Graphics, downBounds, false);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Enabled || e.Button != MouseButtons.Left) return;
            if (upBounds.Contains(e.Location)) Step(1); else if (downBounds.Contains(e.Location)) Step(-1); else textBox.Focus();
        }

        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); textBox.Enabled = Enabled; Invalidate(); }

        private void DrawArrow(Graphics graphics, Rectangle bounds, bool up)
        {
            string icon = up ? "ic_fluent_chevron_up_20_regular" : "ic_fluent_chevron_down_20_regular";
            FluentIconCatalog.TryDraw(graphics, icon, bounds, Enabled ? theme.TextMuted : theme.BorderStrong, 12F);
        }
        private void TextBoxKeyDown(object sender, KeyEventArgs e)
        { if (e.KeyCode == Keys.Up) { Step(1); e.Handled = true; } else if (e.KeyCode == Keys.Down) { Step(-1); e.Handled = true; } else if (e.KeyCode == Keys.Enter) { CommitText(); e.Handled = true; } }
        private void TextBoxMouseWheel(object sender, MouseEventArgs e)
        {
            HandledMouseEventArgs handled = e as HandledMouseEventArgs;
            if (!textBox.Focused) return;
            Step(e.Delta > 0 ? 1 : -1);
            if (handled != null) handled.Handled = true;
        }
        private void Step(int direction)
        {
            CommitText();
            decimal candidate;
            try { candidate = checked(value + increment * direction); }
            catch (OverflowException) { candidate = direction > 0 ? maximum : minimum; }
            SetValue(candidate, true); textBox.SelectAll();
        }
        private void CommitText()
        {
            string raw = textBox.Text.Trim();
            if (!string.IsNullOrEmpty(prefix) && raw.StartsWith(prefix)) raw = raw.Substring(prefix.Length).Trim();
            if (!string.IsNullOrEmpty(suffix) && raw.EndsWith(suffix)) raw = raw.Substring(0, raw.Length - suffix.Length).Trim();
            decimal parsed; if (decimal.TryParse(raw, NumberStyles.Number, CultureInfo.CurrentCulture, out parsed)) SetValue(parsed, true); else UpdateText();
        }
        private void SetValue(decimal candidate, bool raise)
        {
            decimal next = candidate < minimum ? minimum : candidate > maximum ? maximum : candidate;
            bool changed = next != value; value = next; UpdateText();
            if (changed && raise && ValueChanged != null) ValueChanged(this, EventArgs.Empty);
        }
        private void UpdateText()
        {
            if (textBox == null) return;
            string number = value.ToString("N" + decimalPlaces, CultureInfo.CurrentCulture);
            string display = prefix + number + suffix;
            if (textBox.Text != display) { int start = textBox.SelectionStart; textBox.Text = display; textBox.SelectionStart = Math.Min(start, textBox.TextLength); }
        }
    }
}
