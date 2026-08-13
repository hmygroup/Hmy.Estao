using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace ZarpaSuite.Controls
{
    public enum ZarpaButtonStyle { Primary, Secondary, Subtle, Danger }

    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(Button))]
    [DefaultEvent("Click")]
    public class ZarpaButton : Button, IZarpaThemeAware
    {
        private ZarpaThemeTokens theme; private ZarpaButtonStyle buttonStyle; private string iconKey = string.Empty; private bool hot, pressed, loading, enabledBeforeLoading = true;
        public ZarpaButton()
        {
            theme = new ZarpaThemeTokens(Invalidate); FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; Size = new Size(120, 34);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }
        [Category("Apariencia"), DefaultValue(ZarpaButtonStyle.Primary)] public ZarpaButtonStyle ButtonStyle { get { return buttonStyle; } set { buttonStyle = value; Invalidate(); } }
        [Category("Icono"), DefaultValue("")]
        [Editor("ZarpaSuite.Controls.Design.FluentIconPickerEditor, Zarpa.Controls", typeof(System.Drawing.Design.UITypeEditor))]
        public string IconKey { get { return iconKey; } set { iconKey = value ?? string.Empty; Invalidate(); } }
        [Category("Estado"), DefaultValue(false)] public bool Loading { get { return loading; } set { if (loading == value) return; if (value) enabledBeforeLoading = Enabled; loading = value; Enabled = value ? false : enabledBeforeLoading; Invalidate(); } }
        public void ApplyTheme(ZarpaThemeTokens value) { if (value == null) return; theme = value; Font = new Font(theme.FontFamily, theme.FontSize); BackColor = theme.Canvas; ForeColor = theme.Text; Height = theme.ControlHeight; Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hot = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = false; pressed = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs mevent) { base.OnMouseDown(mevent); if (Enabled && mevent.Button == MouseButtons.Left) { pressed = true; Capture = true; Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs mevent) { base.OnMouseUp(mevent); if (mevent.Button == MouseButtons.Left) { pressed = false; Capture = false; Invalidate(); } }
        protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if (!Capture) { pressed = false; Invalidate(); } }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); if (!Enabled) { hot = false; pressed = false; Capture = false; } Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); pressed = false; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(ZarpaPaint.EffectiveBackColor(Parent));
            Color fill = buttonStyle == ZarpaButtonStyle.Primary ? theme.Accent : buttonStyle == ZarpaButtonStyle.Danger ? theme.Danger : buttonStyle == ZarpaButtonStyle.Subtle ? theme.Surface : theme.SurfaceRaised;
            Color text = buttonStyle == ZarpaButtonStyle.Primary || buttonStyle == ZarpaButtonStyle.Danger ? Color.White : theme.Text;
            if (hot) fill = ZarpaPaint.Blend(fill, buttonStyle == ZarpaButtonStyle.Primary ? theme.AccentHover : theme.Accent, .16F);
            if (pressed) fill = ZarpaPaint.Blend(fill, theme.AccentPressed, .25F);
            Rectangle bounds = new Rectangle(0, 0, Width - 1, Height - 1); ZarpaPaint.FillRounded(e.Graphics, fill, bounds, theme.CornerRadius);
            if (buttonStyle == ZarpaButtonStyle.Secondary) ZarpaPaint.DrawRounded(e.Graphics, theme.Border, bounds, theme.CornerRadius, theme.BorderThickness);
            if (Focused && ShowFocusCues) ZarpaPaint.DrawRounded(e.Graphics,
                buttonStyle == ZarpaButtonStyle.Primary || buttonStyle == ZarpaButtonStyle.Danger ? Color.White : theme.Accent,
                new Rectangle(2, 2, Math.Max(1, Width - 5), Math.Max(1, Height - 5)), Math.Max(2, theme.CornerRadius - 2), 1);
            int iconWidth = string.IsNullOrEmpty(iconKey) ? 0 : theme.IconSize + theme.SpacingSmall; int contentWidth = TextRenderer.MeasureText(Text, Font).Width + iconWidth; int x = Math.Max(theme.SpacingMedium, (Width - contentWidth) / 2);
            if (!string.IsNullOrEmpty(iconKey)) { Rectangle icon = new Rectangle(x, (Height - theme.IconSize) / 2, theme.IconSize, theme.IconSize); FluentIconCatalog.TryDraw(e.Graphics, loading ? "ic_fluent_spinner_ios_20_regular" : iconKey, icon, text, theme.IconSize - 2F); x = icon.Right + theme.SpacingSmall; }
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(x, 0, Width - x - theme.SpacingMedium, Height), Enabled || loading ? text : theme.TextMuted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(CheckBox))]
    [DefaultEvent("CheckedChanged")]
    public class ZarpaCheckBox : CheckBox, IZarpaThemeAware
    {
        private ZarpaThemeTokens theme; private bool hot;
        public ZarpaCheckBox()
        {
            theme = new ZarpaThemeTokens(Invalidate);
            AutoSize = false;
            Size = new Size(160, 32);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }
        public void ApplyTheme(ZarpaThemeTokens value) { if (value == null) return; theme = value; Font = new Font(theme.FontFamily, theme.FontSize); BackColor = theme.Canvas; ForeColor = theme.Text; Height = theme.ControlHeight; Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hot = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(ZarpaPaint.EffectiveBackColor(Parent));
            int size = Math.Min(20, Height - 8);
            Rectangle box = new Rectangle(2, (Height - size) / 2, size, size);
            bool marked = CheckState != CheckState.Unchecked;
            Color fill = marked ? (hot ? theme.AccentHover : theme.Accent) : hot ? theme.SurfaceRaised : theme.Surface;
            ZarpaPaint.FillRounded(e.Graphics, fill, box, Math.Min(5, theme.CornerRadius));
            ZarpaPaint.DrawRounded(e.Graphics, Focused ? theme.Accent : marked ? theme.Accent : theme.BorderStrong, box, Math.Min(5, theme.CornerRadius), Focused ? 1.5F : theme.BorderThickness);
            if (CheckState == CheckState.Checked)
                using (Font checkFont = new Font(Font, FontStyle.Bold))
                    TextRenderer.DrawText(e.Graphics, "✓", checkFont, box, Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            else if (CheckState == CheckState.Indeterminate)
                using (Pen pen = new Pen(Color.White, 2F)) e.Graphics.DrawLine(pen, box.Left + 5, box.Top + box.Height / 2, box.Right - 5, box.Top + box.Height / 2);
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(box.Right + theme.SpacingMedium, 0, Width - box.Right - theme.SpacingMedium, Height),
                Enabled ? theme.Text : theme.TextMuted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(CheckBox))]
    [DefaultEvent("CheckedChanged")]
    public class ZarpaToggleSwitch : CheckBox, IZarpaThemeAware
    {
        private ZarpaThemeTokens theme; private bool hot;
        private readonly System.Threading.Timer motionTimer;
        private float toggleProgress;
        private long lastMotionTimestamp;
        private bool motionRunning;
        private int motionTickPending;
        public ZarpaToggleSwitch()
        {
            theme = new ZarpaThemeTokens(Invalidate);
            toggleProgress = Checked ? 1F : 0F;
            motionTimer = new System.Threading.Timer(MotionClockPulse, null,
                System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            AutoSize = false;
            Size = new Size(180, 34);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        }
        public void ApplyTheme(ZarpaThemeTokens value) { if (value == null) return; theme = value; Font = new Font(theme.FontFamily, theme.FontSize); BackColor = theme.Canvas; ForeColor = theme.Text; Height = theme.ControlHeight; if (!theme.MotionEnabled) { StopMotion(); toggleProgress = Checked ? 1F : 0F; } Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hot = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = false; Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnCheckedChanged(EventArgs e)
        {
            base.OnCheckedChanged(e);
            if (theme.MotionEnabled && !IsDesignerHosted && IsHandleCreated) StartMotion();
            else { toggleProgress = Checked ? 1F : 0F; InvalidateToggle(); }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(ZarpaPaint.EffectiveBackColor(Parent));
            int trackHeight = Math.Min(22, Height - 8);
            int trackWidth = trackHeight * 2 - 2;
            Rectangle track = new Rectangle(2, (Height - trackHeight) / 2, trackWidth, trackHeight);
            Color offColor = hot ? ZarpaPaint.Blend(theme.BorderStrong, theme.Accent, .18F) : theme.BorderStrong;
            Color onColor = hot ? theme.AccentHover : theme.Accent;
            Color trackColor = ZarpaPaint.Blend(offColor, onColor, toggleProgress);
            ZarpaPaint.FillRounded(e.Graphics, trackColor, track, trackHeight / 2);
            if (Focused) ZarpaPaint.DrawRounded(e.Graphics, theme.Accent, new Rectangle(track.X - 2, track.Y - 2, track.Width + 4, track.Height + 4), trackHeight / 2 + 2, 1);
            int thumb = trackHeight - 8;
            int thumbInset = 4;
            int thumbX = (int)Math.Round(track.Left + thumbInset +
                (track.Width - thumb - thumbInset * 2) * toggleProgress);
            GraphicsState thumbState = e.Graphics.Save();
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.FillEllipse(Brushes.White,
                new Rectangle(thumbX, track.Top + thumbInset, thumb, thumb));
            e.Graphics.Restore(thumbState);
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(track.Right + theme.SpacingMedium, 0, Width - track.Right - theme.SpacingMedium, Height),
                Enabled ? theme.Text : theme.TextMuted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        private bool IsDesignerHosted { get { return Site != null && Site.DesignMode; } }
        private void StartMotion()
        {
            if (motionRunning) return;
            lastMotionTimestamp = Stopwatch.GetTimestamp();
            motionRunning = true;
            motionTimer.Change(0, 8);
        }
        private void MotionClockPulse(object state)
        {
            if (!motionRunning || IsDisposed || !IsHandleCreated ||
                System.Threading.Interlocked.Exchange(ref motionTickPending, 1) != 0) return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    System.Threading.Interlocked.Exchange(ref motionTickPending, 0);
                    if (!motionRunning || IsDisposed) return;
                    if (!theme.MotionEnabled || IsDesignerHosted)
                    {
                        toggleProgress = Checked ? 1F : 0F;
                        StopMotion();
                        InvalidateToggle();
                        return;
                    }
                    long now = Stopwatch.GetTimestamp();
                    float elapsed = (float)Math.Max(0.001, Math.Min(0.050,
                        (now - lastMotionTimestamp) / (double)Stopwatch.Frequency));
                    lastMotionTimestamp = now;
                    float target = Checked ? 1F : 0F;
                    toggleProgress = MoveTowards(toggleProgress, target,
                        elapsed * 1000F / Math.Max(1, theme.PressDuration));
                    InvalidateToggle();
                    if (Math.Abs(toggleProgress - target) <= 0.001F) StopMotion();
                });
            }
            catch (ObjectDisposedException) { System.Threading.Interlocked.Exchange(ref motionTickPending, 0); }
            catch (InvalidOperationException) { System.Threading.Interlocked.Exchange(ref motionTickPending, 0); }
        }
        private void InvalidateToggle()
        {
            int trackHeight = Math.Min(22, Height - 8);
            Invalidate(new Rectangle(0, Math.Max(0, (Height - trackHeight) / 2 - 3),
                trackHeight * 2 + 5, trackHeight + 6));
        }
        private void StopMotion()
        {
            motionRunning = false;
            motionTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            lastMotionTimestamp = 0L;
        }
        private static float MoveTowards(float value, float target, float maximumDelta)
        {
            if (Math.Abs(target - value) <= maximumDelta) return target;
            return value + Math.Sign(target - value) * maximumDelta;
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { motionRunning = false; motionTimer.Dispose(); }
            base.Dispose(disposing);
        }
    }

    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(CheckedListBox))]
    [DefaultProperty("Items")]
    public class ZarpaMultiSelect : ZarpaFieldBase
    {
        private readonly TextBox display;
        private readonly StringCollection items = new StringCollection();
        private readonly CheckedListBox list;
        private readonly ToolStripDropDown dropDown;
        private readonly ZarpaPopupController popupController;
        private string selectedValues = string.Empty;

        public ZarpaMultiSelect()
        {
            display = new TextBox { BorderStyle = BorderStyle.None, ReadOnly = true, Cursor = Cursors.Hand };
            display.Click += ShowDropDown;
            display.KeyDown += DisplayKeyDown;
            list = new CheckedListBox { BorderStyle = BorderStyle.None, CheckOnClick = true, IntegralHeight = false };
            list.ItemCheck += ListItemCheck;
            list.KeyDown += ListKeyDown;
            ToolStripControlHost host = new ToolStripControlHost(list) { AutoSize = false, Margin = Padding.Empty, Padding = Padding.Empty, Size = new Size(260, 180) };
            dropDown = new ToolStripDropDown { Padding = Padding.Empty, AutoClose = true };
            dropDown.Items.Add(host);
            popupController = new ZarpaPopupController(display, dropDown);
            LabelText = "Selección múltiple";
            InitializeEditor(display);
        }

        [Category("Datos")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
        [Editor("System.Windows.Forms.Design.StringCollectionEditor, System.Design", typeof(System.Drawing.Design.UITypeEditor))]
        public StringCollection Items { get { return items; } }

        [Category("Datos"), DefaultValue("")]
        public string SelectedValues
        {
            get { return selectedValues; }
            set { string next = NormalizeSelection(value); if (selectedValues == next) return; selectedValues = next; UpdateDisplay(); OnValueChanged(); }
        }

        [Browsable(false)]
        public string[] SelectedItems { get { return selectedValues.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(v => v.Trim()).ToArray(); } }

        [Browsable(false)] public override object UntypedValue { get { return SelectedItems; } }

        protected override void Dispose(bool disposing) { if (disposing) { popupController.Dispose(); dropDown.Dispose(); } base.Dispose(disposing); }

        protected override void ApplyEditorTheme()
        {
            base.ApplyEditorTheme();
            if (list == null) return;
            list.Font = Font;
            list.BackColor = Theme.SurfaceOverlay;
            list.ForeColor = Theme.Text;
            dropDown.BackColor = Theme.Border;
        }

        protected override void LayoutEditor()
        {
            base.LayoutEditor();
            if (Editor != null) Editor.Width = Math.Max(10, Editor.Width - 24);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Rectangle input = GetInputBounds();
            Rectangle icon = new Rectangle(input.Right - 26, input.Top + (input.Height - 18) / 2, 18, 18);
            FluentIconCatalog.TryDraw(e.Graphics, "ic_fluent_chevron_down_20_regular", icon,
                Enabled ? Theme.TextMuted : Theme.BorderStrong, 14F);
        }

        private void ShowDropDown(object sender, EventArgs e)
        {
            if (LicenseManager.UsageMode == LicenseUsageMode.Designtime || !Enabled || items.Count == 0) return;
            list.BeginUpdate();
            list.Items.Clear();
            string[] selected = SelectedItems;
            foreach (string item in items) list.Items.Add(item, selected.Contains(item));
            list.EndUpdate();
            ToolStripControlHost host = (ToolStripControlHost)dropDown.Items[0];
            Rectangle anchorBounds = GetInputBounds();
            host.Size = new Size(Math.Max(anchorBounds.Width - 2, 220),
                Math.Min(8, Math.Max(1, items.Count)) * Math.Max(Theme.ControlHeight,
                    Font.Height + Theme.SpacingMedium * 2) + Theme.SpacingSmall);
            list.Size = host.Size;
            popupController.Show(this, anchorBounds);
        }

        private void ListItemCheck(object sender, ItemCheckEventArgs e)
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke((MethodInvoker)delegate
            {
                if (IsDisposed || list.IsDisposed) return;
                string next = string.Join("; ", list.CheckedItems.Cast<object>().Select(v => Convert.ToString(v)).ToArray());
                if (selectedValues == next) return;
                selectedValues = next;
                UpdateDisplay();
                OnValueChanged();
            });
        }

        private void DisplayKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F4 || (e.Alt && e.KeyCode == Keys.Down) || e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            { ShowDropDown(sender, EventArgs.Empty); e.Handled = true; e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Escape && popupController.IsOpen) { popupController.Close(); e.Handled = true; e.SuppressKeyPress = true; }
        }

        private void ListKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape || e.KeyCode == Keys.Enter)
            { popupController.Close(); display.Focus(); e.Handled = true; e.SuppressKeyPress = true; }
        }

        private static string NormalizeSelection(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            return string.Join("; ", value.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(v => v.Trim()).Where(v => v.Length > 0).Distinct().ToArray());
        }

        private void UpdateDisplay()
        {
            display.Text = string.IsNullOrEmpty(selectedValues) ? "Seleccionar..." : selectedValues.Replace(";", ",");
        }
    }
}
