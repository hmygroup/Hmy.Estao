using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Design;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ZarpaSuite.Controls
{
    [ToolboxItem(false)]
    [ToolboxBitmap(typeof(TabPage))]
    public class ZarpaWizardStep : Component
    {
        private string text = "Paso";
        private string description = string.Empty;
        private string iconKey = string.Empty;
        private string key = string.Empty;
        private bool enabled = true;
        private bool completed;
        private ZarpaNavigationPage page;

        public event EventHandler Changed;

        [Category("Datos"), DefaultValue("Paso")]
        public string Text { get { return text; } set { string next = value ?? string.Empty; if (text == next) return; text = next; OnChanged(); } }

        [Category("Datos"), DefaultValue("")]
        public string Description { get { return description; } set { string next = value ?? string.Empty; if (description == next) return; description = next; OnChanged(); } }

        [Category("Datos"), DefaultValue("")]
        public string Key { get { return key; } set { string next = value ?? string.Empty; if (key == next) return; key = next; OnChanged(); } }

        [Category("Icono"), DefaultValue("")]
        [Editor("ZarpaSuite.Controls.Design.FluentIconPickerEditor, Zarpa.Controls", typeof(UITypeEditor))]
        public string IconKey { get { return iconKey; } set { string next = value ?? string.Empty; if (iconKey == next) return; iconKey = next; OnChanged(); } }

        [Category("Estado"), DefaultValue(true)]
        public bool Enabled { get { return enabled; } set { if (enabled == value) return; enabled = value; OnChanged(); } }

        [Category("Estado"), DefaultValue(false)]
        public bool Completed { get { return completed; } set { if (completed == value) return; completed = value; OnChanged(); } }

        [Category("Navegación"), DefaultValue(null)]
        public ZarpaNavigationPage Page { get { return page; } set { if (ReferenceEquals(page, value)) return; page = value; OnChanged(); } }

        private void OnChanged() { if (Changed != null) Changed(this, EventArgs.Empty); }
        public override string ToString() { return Text; }
    }

    public sealed class ZarpaWizardStepCollection : Collection<ZarpaWizardStep>
    {
        private readonly ZarpaWizardStepper owner;
        internal ZarpaWizardStepCollection(ZarpaWizardStepper value) { owner = value; }
        protected override void InsertItem(int index, ZarpaWizardStep item) { if (item == null) throw new ArgumentNullException("item"); base.InsertItem(index, item); item.Changed += ItemChanged; owner.RefreshSteps(); }
        protected override void SetItem(int index, ZarpaWizardStep item) { if (item == null) throw new ArgumentNullException("item"); this[index].Changed -= ItemChanged; base.SetItem(index, item); item.Changed += ItemChanged; owner.RefreshSteps(); }
        protected override void RemoveItem(int index) { this[index].Changed -= ItemChanged; base.RemoveItem(index); owner.RefreshSteps(); }
        protected override void ClearItems() { foreach (ZarpaWizardStep item in this) item.Changed -= ItemChanged; base.ClearItems(); owner.RefreshSteps(); }
        private void ItemChanged(object sender, EventArgs e) { owner.RefreshSteps(); }
        public void AddRange(ZarpaWizardStep[] values) { if (values != null) foreach (ZarpaWizardStep value in values) Add(value); }
    }

    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(TabControl))]
    [DefaultProperty("Steps")]
    [DefaultEvent("SelectedStepChanged")]
    [Designer("ZarpaSuite.Controls.Design.ZarpaWizardStepperDesigner, Zarpa.Controls")]
    public class ZarpaWizardStepper : Control, IZarpaThemeAware
    {
        private ZarpaThemeTokens theme;
        private readonly ZarpaWizardStepCollection steps;
        private ZarpaDpiScale dpiScale = new ZarpaDpiScale(96, 96);
        private int selectedIndex = -1;
        private int hotIndex = -1;
        private Control pageHost;
        private ZarpaWizardStep designSelectedStep;

        public ZarpaWizardStepper()
        {
            theme = new ZarpaThemeTokens(Invalidate);
            steps = new ZarpaWizardStepCollection(this);
            Height = 78;
            Dock = DockStyle.Top;
            Font = new Font("Segoe UI", 9F);
            TabStop = true;
            AccessibleRole = AccessibleRole.List;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        }

        [Category("Datos"), DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
        [Editor("TestRibbon.Controls.ZarpaWizardStepCollectionEditor, Zarpa.Controls", typeof(UITypeEditor))]
        public ZarpaWizardStepCollection Steps { get { return steps; } }

        [Category("Navegación"), DefaultValue(null)]
        public Control PageHost
        {
            get { return pageHost; }
            set { pageHost = value; UpdatePageVisibility(); }
        }

        [Category("Estado"), DefaultValue(-1)]
        public int SelectedIndex
        {
            get { return selectedIndex; }
            set
            {
                int next = value < -1 ? -1 : value >= steps.Count ? steps.Count - 1 : value;
                if (next >= 0 && !steps[next].Enabled) return;
                if (selectedIndex == next) { UpdatePageVisibility(); return; }
                selectedIndex = next;
                UpdatePageVisibility();
                Invalidate();
                if (SelectedStepChanged != null) SelectedStepChanged(this, EventArgs.Empty);
            }
        }

        [Browsable(false)]
        public ZarpaWizardStep SelectedStep { get { return selectedIndex >= 0 && selectedIndex < steps.Count ? steps[selectedIndex] : null; } }

        public event EventHandler SelectedStepChanged;

        public void ApplyTheme(ZarpaThemeTokens value)
        {
            if (value == null) return;
            theme = value;
            Font = new Font(theme.FontFamily, theme.FontSize);
            BackColor = theme.Surface;
            ForeColor = theme.Text;
            Height = S(78);
            Invalidate();
        }

        internal void RefreshSteps()
        {
            if (selectedIndex >= steps.Count) selectedIndex = steps.Count - 1;
            UpdatePageVisibility();
            Invalidate();
        }

        internal int DesignHitTest(Point point) { return HitTest(point); }
        internal ZarpaWizardStep DesignSelectedStep { get { return designSelectedStep; } set { designSelectedStep = value; Invalidate(); } }
        internal void ActivateDesignStep(ZarpaWizardStep step)
        {
            int index = steps.IndexOf(step);
            if (index < 0) return;
            designSelectedStep = step;
            selectedIndex = index;
            UpdatePageVisibility();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.Clear(theme.Surface);
            if (steps.Count == 0) return;
            int stepWidth = Math.Max(S(120), Width / steps.Count);
            float circleSize = dpiScale.X(28F);
            float circleTop = dpiScale.X(8F);
            float lineY = circleTop + circleSize / 2F;
            for (int index = 0; index < steps.Count; index++)
            {
                float center = Math.Min(Width - stepWidth / 2F, index * stepWidth + stepWidth / 2F);
                if (index > 0)
                {
                    float previousCenter = Math.Min(Width - stepWidth / 2F, (index - 1) * stepWidth + stepWidth / 2F);
                    Color connector = steps[index - 1].Completed ? theme.Success : theme.BorderStrong;
                    using (Pen pen = new Pen(connector, dpiScale.X(1.5F)))
                    {
                        pen.StartCap = LineCap.Round;
                        pen.EndCap = LineCap.Round;
                        e.Graphics.DrawLine(pen, previousCenter + circleSize / 2F + S(5), lineY,
                            center - circleSize / 2F - S(5), lineY);
                    }
                }

                ZarpaWizardStep step = steps[index];
                bool selected = index == selectedIndex;
                RectangleF circle = new RectangleF(center - circleSize / 2F, circleTop, circleSize, circleSize);
                Color fill = step.Completed ? theme.Success : selected ? theme.Accent : index == hotIndex && step.Enabled ? theme.SurfaceRaised : theme.Surface;
                Color border = step.Completed ? theme.Success : selected ? theme.Accent : theme.BorderStrong;
                using (SolidBrush brush = new SolidBrush(fill)) e.Graphics.FillEllipse(brush, circle);
                float stroke = selected ? dpiScale.X(2F) : dpiScale.Stroke(1);
                using (Pen pen = new Pen(border, stroke))
                    e.Graphics.DrawEllipse(pen, circle.X + stroke / 2F, circle.Y + stroke / 2F,
                        Math.Max(1F, circle.Width - stroke), Math.Max(1F, circle.Height - stroke));
                Color iconColor = step.Completed || selected ? Color.White : step.Enabled ? theme.TextMuted : theme.BorderStrong;
                Rectangle glyphBounds = Rectangle.Round(circle);
                using (Font glyphFont = new Font(Font.FontFamily, Math.Max(9F, Font.Size), FontStyle.Bold))
                    TextRenderer.DrawText(e.Graphics, step.Completed ? "✓" : (index + 1).ToString(), glyphFont, glyphBounds, iconColor,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

                int left = index * stepWidth + S(8);
                int width = Math.Min(stepWidth - S(16), Width - left - S(4));
                Color text = step.Enabled ? theme.Text : theme.TextMuted;
                using (Font titleFont = new Font(Font, selected ? FontStyle.Bold : FontStyle.Regular))
                    TextRenderer.DrawText(e.Graphics, step.Text, titleFont,
                        new Rectangle(left, S(39), Math.Max(1, width), S(19)), text,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                if (stepWidth >= S(180))
                    using (Font detailFont = new Font(Font.FontFamily, Math.Max(7F, Font.Size - 1F)))
                        TextRenderer.DrawText(e.Graphics, step.Description, detailFont,
                            new Rectangle(left, S(57), Math.Max(1, width), S(17)), theme.TextMuted,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            using (Pen border = new Pen(theme.Border, dpiScale.Stroke(1)))
                e.Graphics.DrawLine(border, 0, Height - 1, Width, Height - 1);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int next = HitTest(e.Location);
            if (next == hotIndex) return;
            hotIndex = next;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hotIndex = -1; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) Focus(); }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            int index = HitTest(e.Location);
            if (index >= 0 && steps[index].Enabled) SelectedIndex = index;
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Left || key == Keys.Right || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            int direction = e.KeyCode == Keys.Left ? -1 : e.KeyCode == Keys.Right ? 1 : 0;
            if (direction != 0)
            {
                int index = selectedIndex + direction;
                while (index >= 0 && index < steps.Count && !steps[index].Enabled) index += direction;
                if (index >= 0 && index < steps.Count) SelectedIndex = index;
                e.Handled = true;
            }
            else if ((e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) && hotIndex >= 0 && steps[hotIndex].Enabled)
            { SelectedIndex = hotIndex; e.Handled = true; }
            if (e.Handled) e.SuppressKeyPress = true;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ZarpaDpiScale value = ZarpaDpiScale.FromControl(this);
            if (value.DpiX != dpiScale.DpiX || value.DpiY != dpiScale.DpiY) { dpiScale = value; Height = S(78); }
        }

        private int HitTest(Point point)
        {
            if (steps.Count == 0 || point.Y < 0 || point.Y > Height) return -1;
            int stepWidth = Math.Max(S(120), Width / steps.Count);
            int index = Math.Min(steps.Count - 1, point.X / stepWidth);
            return index >= 0 && index < steps.Count ? index : -1;
        }

        private void UpdatePageVisibility()
        {
            foreach (ZarpaWizardStep step in steps)
                if (step.Page != null) step.Page.SetActive(false);
            ZarpaWizardStep selected = SelectedStep;
            if (selected != null && selected.Page != null)
            {
                selected.Page.SetActive(true);
                selected.Page.BringToFront();
            }
        }

        private int S(int logicalPixels) { return dpiScale.X(logicalPixels); }
    }

    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(Button))]
    [DefaultEvent("Click")]
    [DefaultProperty("TitleText")]
    public class ZarpaChoiceCard : Control, IZarpaThemeAware
    {
        private ZarpaThemeTokens theme;
        private string titleText = "Opción";
        private string descriptionText = string.Empty;
        private string iconKey = string.Empty;
        private string badgeText = string.Empty;
        private bool selected;
        private bool hot;
        private bool pressed;

        public ZarpaChoiceCard()
        {
            theme = new ZarpaThemeTokens(Invalidate);
            Size = new Size(320, 118);
            Font = new Font("Segoe UI", 9F);
            TabStop = true;
            AccessibleRole = AccessibleRole.PushButton;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
        }

        [Category("Contenido"), DefaultValue("Opción")]
        public string TitleText { get { return titleText; } set { titleText = value ?? string.Empty; AccessibleName = titleText; Invalidate(); } }
        [Category("Contenido"), DefaultValue("")]
        public string DescriptionText { get { return descriptionText; } set { descriptionText = value ?? string.Empty; AccessibleDescription = descriptionText; Invalidate(); } }
        [Category("Contenido"), DefaultValue("")]
        public string BadgeText { get { return badgeText; } set { badgeText = value ?? string.Empty; Invalidate(); } }
        [Category("Icono"), DefaultValue("")]
        [Editor("ZarpaSuite.Controls.Design.FluentIconPickerEditor, Zarpa.Controls", typeof(UITypeEditor))]
        public string IconKey { get { return iconKey; } set { iconKey = value ?? string.Empty; Invalidate(); } }
        [Category("Estado"), DefaultValue(false)]
        public bool Selected { get { return selected; } set { selected = value; Invalidate(); } }

        public void ApplyTheme(ZarpaThemeTokens value)
        {
            if (value == null) return;
            theme = value;
            Font = new Font(theme.FontFamily, theme.FontSize);
            BackColor = theme.Canvas;
            ForeColor = theme.Text;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.Clear(ZarpaPaint.EffectiveBackColor(Parent));
            Rectangle card = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
            Color fill = selected ? theme.Selection : pressed ? theme.SurfaceRaised : hot ?
                ZarpaPaint.Blend(theme.Surface, theme.SurfaceRaised, .55F) : theme.Surface;
            ZarpaPaint.FillRounded(e.Graphics, fill, card, theme.GroupCornerRadius);
            ZarpaPaint.DrawRounded(e.Graphics, selected ? theme.Accent : hot ? theme.BorderStrong : theme.Border,
                card, theme.GroupCornerRadius, selected ? 2F : 1F);

            Rectangle iconSurface = new Rectangle(18, 18, 46, 46);
            ZarpaPaint.FillRounded(e.Graphics, selected ? theme.Accent : theme.SurfaceRaised, iconSurface, theme.CornerRadius);
            Rectangle icon = new Rectangle(iconSurface.Left + 11, iconSurface.Top + 11, 24, 24);
            FluentIconCatalog.TryDraw(e.Graphics, iconKey, icon, selected ? Color.White : theme.Accent, 22F);

            int badgeWidth = string.IsNullOrEmpty(badgeText) ? 0 : Math.Min(110, TextRenderer.MeasureText(badgeText, Font).Width + 18);
            int textLeft = iconSurface.Right + 16;
            using (Font titleFont = new Font(Font, FontStyle.Bold))
                TextRenderer.DrawText(e.Graphics, titleText, titleFont,
                    new Rectangle(textLeft, 16, Math.Max(1, Width - textLeft - badgeWidth - 24), 25), theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(e.Graphics, descriptionText, Font,
                new Rectangle(textLeft, 45, Math.Max(1, Width - textLeft - 22), Math.Max(25, Height - 58)), theme.TextMuted,
                TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            if (badgeWidth > 0)
            {
                Rectangle badge = new Rectangle(Width - badgeWidth - 16, 15, badgeWidth, 25);
                ZarpaPaint.FillRounded(e.Graphics, theme.SurfaceRaised, badge, theme.CornerRadius);
                TextRenderer.DrawText(e.Graphics, badgeText, Font, badge, theme.TextMuted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            if (selected)
            {
                Rectangle check = new Rectangle(Width - 34, Height - 34, 20, 20);
                FluentIconCatalog.TryDraw(e.Graphics, "ic_fluent_checkmark_circle_24_regular", check, theme.Accent, 19F);
            }
            if (Focused && ShowFocusCues) ZarpaPaint.DrawRounded(e.Graphics, theme.Accent,
                new Rectangle(4, 4, Math.Max(1, Width - 9), Math.Max(1, Height - 9)), Math.Max(2, theme.GroupCornerRadius - 2), 1F);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hot = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = false; pressed = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (Enabled && e.Button == MouseButtons.Left) { pressed = true; Focus(); Invalidate(); } }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); bool click = pressed && ClientRectangle.Contains(e.Location); pressed = false; Invalidate(); if (click) OnClick(EventArgs.Empty); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); pressed = false; Invalidate(); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) { OnClick(EventArgs.Empty); e.Handled = true; e.SuppressKeyPress = true; }
        }
    }
}
