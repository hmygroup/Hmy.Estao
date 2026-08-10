using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ZarpaSuite.Controls
{
    public enum ZarpaFeedbackKind { Neutral, Information, Success, Warning, Error }
    public enum ZarpaStateKind { Empty, Error, Success, Loading }

    internal static class ZarpaFeedbackPalette
    {
        internal static Color Get(ZarpaThemeTokens theme, ZarpaFeedbackKind kind)
        {
            switch (kind)
            {
                case ZarpaFeedbackKind.Success: return theme.Success;
                case ZarpaFeedbackKind.Warning: return theme.Warning;
                case ZarpaFeedbackKind.Error: return theme.Danger;
                case ZarpaFeedbackKind.Information: return theme.Information;
                default: return theme.TextMuted;
            }
        }
        internal static string Icon(ZarpaFeedbackKind kind)
        {
            switch (kind)
            {
                case ZarpaFeedbackKind.Success: return "ic_fluent_checkmark_circle_24_regular";
                case ZarpaFeedbackKind.Warning: return "ic_fluent_warning_24_regular";
                case ZarpaFeedbackKind.Error: return "ic_fluent_error_circle_24_regular";
                case ZarpaFeedbackKind.Information: return "ic_fluent_info_24_regular";
                default: return "ic_fluent_alert_24_regular";
            }
        }
    }

    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(Label))]
    [DefaultEvent("ActionClick")]
    public class ZarpaBanner : Control, IZarpaThemeAware
    {
        private ZarpaThemeTokens theme;
        private string titleText = "Información", messageText = "Mensaje contextual.", actionText = string.Empty;
        private ZarpaFeedbackKind kind = ZarpaFeedbackKind.Information;
        private bool dismissible = true;
        private Rectangle actionBounds, closeBounds;
        public ZarpaBanner()
        {
            theme = new ZarpaThemeTokens(Invalidate);
            Height = 64; MinimumSize = new Size(240, 52);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            TabStop = true;
        }
        [Category("Contenido"), DefaultValue("Información")] public string TitleText { get { return titleText; } set { titleText = value ?? string.Empty; Invalidate(); } }
        [Category("Contenido"), DefaultValue("Mensaje contextual.")] public string MessageText { get { return messageText; } set { messageText = value ?? string.Empty; Invalidate(); } }
        [Category("Contenido"), DefaultValue("")] public string ActionText { get { return actionText; } set { actionText = value ?? string.Empty; Invalidate(); } }
        [Category("Estado"), DefaultValue(ZarpaFeedbackKind.Information)] public ZarpaFeedbackKind Kind { get { return kind; } set { kind = value; Invalidate(); } }
        [Category("Comportamiento"), DefaultValue(true)] public bool Dismissible { get { return dismissible; } set { dismissible = value; Invalidate(); } }
        public event EventHandler ActionClick;
        public event EventHandler Dismissed;
        public void ApplyTheme(ZarpaThemeTokens value) { if (value == null) return; theme = value; Font = new Font(theme.FontFamily, theme.FontSize); BackColor = theme.Canvas; ForeColor = theme.Text; Height = Math.Max(58, theme.ControlHeight + theme.SpacingLarge + theme.SpacingMedium); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); Color accent = ZarpaFeedbackPalette.Get(theme, kind);
            Rectangle card = new Rectangle(0, 0, Width - 1, Height - 1);
            ZarpaPaint.FillRounded(e.Graphics, ZarpaPaint.Blend(theme.Surface, accent, .08F), card, theme.CornerRadius);
            ZarpaPaint.DrawRounded(e.Graphics, ZarpaPaint.Blend(theme.Border, accent, .35F), card, theme.CornerRadius, theme.BorderThickness);
            Rectangle icon = new Rectangle(theme.SpacingLarge, (Height - theme.IconSize) / 2, theme.IconSize, theme.IconSize);
            FluentIconCatalog.TryDraw(e.Graphics, ZarpaFeedbackPalette.Icon(kind), icon, accent, theme.IconSize - 1F);
            int right = Width - theme.SpacingMedium;
            closeBounds = dismissible ? new Rectangle(right - 30, (Height - 30) / 2, 30, 30) : Rectangle.Empty;
            if (dismissible) { using (Font closeFont = new Font(Font.FontFamily, Font.Size + 2F)) TextRenderer.DrawText(e.Graphics, "×", closeFont, closeBounds, theme.TextMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); right = closeBounds.Left - theme.SpacingSmall; }
            int actionWidth = string.IsNullOrEmpty(actionText) ? 0 : TextRenderer.MeasureText(actionText, Font).Width + theme.SpacingLarge;
            actionBounds = actionWidth > 0 ? new Rectangle(right - actionWidth, (Height - theme.ControlHeight) / 2, actionWidth, theme.ControlHeight) : Rectangle.Empty;
            if (!actionBounds.IsEmpty) { ZarpaPaint.DrawRounded(e.Graphics, accent, actionBounds, theme.CornerRadius, theme.BorderThickness); TextRenderer.DrawText(e.Graphics, actionText, Font, actionBounds, accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); right = actionBounds.Left - theme.SpacingMedium; }
            int textLeft = icon.Right + theme.SpacingMedium;
            using (Font titleFont = new Font(Font, FontStyle.Bold)) TextRenderer.DrawText(e.Graphics, titleText, titleFont, new Rectangle(textLeft, 9, Math.Max(20, right - textLeft), 20), theme.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(e.Graphics, messageText, Font, new Rectangle(textLeft, 29, Math.Max(20, right - textLeft), Height - 34), theme.TextMuted, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button != MouseButtons.Left || !Enabled) return; if (actionBounds.Contains(e.Location)) { if (ActionClick != null) ActionClick(this, EventArgs.Empty); } else if (closeBounds.Contains(e.Location)) Dismiss(); }
        protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if ((e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) && !string.IsNullOrEmpty(actionText)) { if (ActionClick != null) ActionClick(this, EventArgs.Empty); e.Handled = true; } else if (e.KeyCode == Keys.Escape && dismissible) { Dismiss(); e.Handled = true; } }
        private void Dismiss() { if (!Visible) return; Visible = false; if (Dismissed != null) Dismissed(this, EventArgs.Empty); }
    }

    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(ProgressBar))]
    public class ZarpaProgressBar : Control, IZarpaThemeAware
    {
        private ZarpaThemeTokens theme; private readonly ZarpaPaintAnimator animator; private int value; private float offset; private bool indeterminate;
        public ZarpaProgressBar()
        {
            theme = new ZarpaThemeTokens(Invalidate);
            Height = 8;
            MinimumSize = new Size(80, 6);
            animator = new ZarpaPaintAnimator(this, AdvanceFrame);
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }
        [Category("Estado"), DefaultValue(0)] public int Value { get { return value; } set { this.value = Math.Max(0, Math.Min(100, value)); Invalidate(); } }
        [Category("Estado"), DefaultValue(false)] public bool Indeterminate { get { return indeterminate; } set { indeterminate = value; UpdateTimer(); Invalidate(); } }
        public void ApplyTheme(ZarpaThemeTokens value) { if (value == null) return; theme = value; UpdateTimer(); Invalidate(); }
        protected override void Dispose(bool disposing) { if (disposing) animator.Dispose(); base.Dispose(disposing); }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); UpdateTimer(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); UpdateTimer(); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UpdateTimer(); }
        protected override void OnHandleDestroyed(EventArgs e) { animator.Stop(); base.OnHandleDestroyed(e); }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(BackColor == Color.Transparent ? ZarpaPaint.EffectiveBackColor(Parent) : BackColor);
        }
        private void UpdateTimer() { animator.Update(indeterminate && Visible && Enabled && IsHandleCreated && theme.MotionEnabled && !IsDesignerHosted); }
        private bool IsDesignerHosted { get { return DesignMode || (Site != null && Site.DesignMode) || LicenseManager.UsageMode == LicenseUsageMode.Designtime; } }
        private void AdvanceFrame(float elapsed)
        {
            if (!indeterminate || !Visible || !Enabled || !theme.MotionEnabled || IsDesignerHosted) { animator.Stop(); return; }
            Rectangle previous = GetAnimatedBounds();
            offset = (offset + 210F * elapsed) % Math.Max(1, Width + 80);
            Rectangle dirty = Rectangle.Union(previous, GetAnimatedBounds());
            dirty.Intersect(ClientRectangle);
            if (!dirty.IsEmpty) Invalidate(dirty);
        }
        private Rectangle GetAnimatedBounds() { return new Rectangle((int)Math.Round(offset) - 80, 0, 80, Math.Max(0, Height)); }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); Rectangle track = new Rectangle(0, 0, Width - 1, Height - 1); ZarpaPaint.FillRounded(e.Graphics, theme.SurfaceRaised, track, Height / 2); Rectangle fill = indeterminate ? GetAnimatedBounds() : new Rectangle(0, 0, (int)((Width - 1) * value / 100F), Height - 1); e.Graphics.SetClip(track); ZarpaPaint.FillRounded(e.Graphics, theme.Accent, fill, Height / 2); e.Graphics.ResetClip(); }
    }

    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(Panel))]
    public class ZarpaSkeleton : Control, IZarpaThemeAware
    {
        private ZarpaThemeTokens theme; private readonly ZarpaPaintAnimator animator; private LinearGradientBrush shimmerBrush; private float offset;
        public ZarpaSkeleton() { theme = new ZarpaThemeTokens(Invalidate); Size = new Size(260, 18); animator = new ZarpaPaintAnimator(this, AdvanceFrame); UpdateShimmerBrush(); SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true); }
        [Category("Apariencia"), DefaultValue(8)] public int Radius { get; set; } = 8;
        public void ApplyTheme(ZarpaThemeTokens value) { if (value == null) return; theme = value; UpdateShimmerBrush(); UpdateTimer(); Invalidate(); }
        protected override void Dispose(bool disposing) { if (disposing) { animator.Dispose(); shimmerBrush.Dispose(); } base.Dispose(disposing); }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); UpdateTimer(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); UpdateTimer(); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UpdateTimer(); }
        protected override void OnHandleDestroyed(EventArgs e) { animator.Stop(); base.OnHandleDestroyed(e); }
        private void UpdateTimer() { animator.Update(Visible && Enabled && IsHandleCreated && theme.MotionEnabled && !IsDesignerHosted); }
        private bool IsDesignerHosted { get { return DesignMode || (Site != null && Site.DesignMode) || LicenseManager.UsageMode == LicenseUsageMode.Designtime; } }
        private void AdvanceFrame(float elapsed)
        {
            if (!Visible || !Enabled || !theme.MotionEnabled || IsDesignerHosted) { animator.Stop(); return; }
            Rectangle previous = GetAnimatedBounds();
            offset = (offset + 235F * elapsed) % Math.Max(1, Width + 100);
            Rectangle dirty = Rectangle.Union(previous, GetAnimatedBounds());
            dirty.Intersect(ClientRectangle);
            if (!dirty.IsEmpty) Invalidate(dirty);
        }
        private Rectangle GetAnimatedBounds() { return new Rectangle((int)Math.Round(offset) - 100, 0, 100, Math.Max(0, Height)); }
        private void UpdateShimmerBrush()
        {
            if (shimmerBrush != null) shimmerBrush.Dispose();
            shimmerBrush = new LinearGradientBrush(new Rectangle(0, 0, 100, 1),
                Color.Transparent, Color.FromArgb(95, theme.Surface), LinearGradientMode.Horizontal);
        }
        protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); Rectangle b = new Rectangle(0, 0, Width - 1, Height - 1); ZarpaPaint.FillRounded(e.Graphics, theme.SurfaceRaised, b, Radius); Rectangle shimmer = GetAnimatedBounds(); shimmerBrush.ResetTransform(); shimmerBrush.TranslateTransform(shimmer.Left, 0); e.Graphics.SetClip(b); e.Graphics.FillRectangle(shimmerBrush, shimmer); e.Graphics.ResetClip(); }
    }

    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(Panel))]
    [DefaultEvent("ActionClick")]
    public class ZarpaStatePanel : Control, IZarpaThemeAware
    {
        private ZarpaThemeTokens theme; private ZarpaStateKind stateKind; private string titleText = "No hay información", messageText = "Todavía no existen elementos para mostrar.", actionText = "Crear elemento"; private Rectangle actionBounds;
        public ZarpaStatePanel() { theme = new ZarpaThemeTokens(Invalidate); Size = new Size(420, 240); TabStop = true; SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true); }
        [Category("Estado"), DefaultValue(ZarpaStateKind.Empty)] public ZarpaStateKind StateKind { get { return stateKind; } set { stateKind = value; Invalidate(); } }
        [Category("Contenido"), DefaultValue("No hay información")] public string TitleText { get { return titleText; } set { titleText = value ?? string.Empty; Invalidate(); } }
        [Category("Contenido"), DefaultValue("Todavía no existen elementos para mostrar.")] public string MessageText { get { return messageText; } set { messageText = value ?? string.Empty; Invalidate(); } }
        [Category("Contenido"), DefaultValue("Crear elemento")] public string ActionText { get { return actionText; } set { actionText = value ?? string.Empty; Invalidate(); } }
        public event EventHandler ActionClick;
        public void ApplyTheme(ZarpaThemeTokens value) { if (value == null) return; theme = value; Font = new Font(theme.FontFamily, theme.FontSize); Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); e.Graphics.Clear(theme.Canvas); string iconKey = stateKind == ZarpaStateKind.Error ? "ic_fluent_error_circle_24_regular" : stateKind == ZarpaStateKind.Success ? "ic_fluent_checkmark_circle_24_regular" : stateKind == ZarpaStateKind.Loading ? "ic_fluent_spinner_ios_20_regular" : "ic_fluent_box_24_regular"; Color accent = stateKind == ZarpaStateKind.Error ? theme.Danger : stateKind == ZarpaStateKind.Success ? theme.Success : stateKind == ZarpaStateKind.Loading ? theme.Information : theme.TextMuted;
            Rectangle icon = new Rectangle((Width - 44) / 2, Math.Max(16, Height / 2 - 82), 44, 44); FluentIconCatalog.TryDraw(e.Graphics, iconKey, icon, accent, 40F);
            using (Font titleFont = new Font(theme.FontFamily, Math.Max(12F, theme.FontSize + 3F), FontStyle.Bold)) TextRenderer.DrawText(e.Graphics, titleText, titleFont, new Rectangle(20, icon.Bottom + 10, Width - 40, 28), theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(e.Graphics, messageText, Font, new Rectangle(30, icon.Bottom + 40, Width - 60, 42), theme.TextMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
            int width = string.IsNullOrEmpty(actionText) ? 0 : Math.Max(110, TextRenderer.MeasureText(actionText, Font).Width + theme.SpacingLarge * 2); actionBounds = width == 0 ? Rectangle.Empty : new Rectangle((Width - width) / 2, icon.Bottom + 88, width, theme.ControlHeight); if (!actionBounds.IsEmpty) { ZarpaPaint.FillRounded(e.Graphics, theme.Accent, actionBounds, theme.CornerRadius); TextRenderer.DrawText(e.Graphics, actionText, Font, actionBounds, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter); }
        }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (Enabled && e.Button == MouseButtons.Left && actionBounds.Contains(e.Location) && ActionClick != null) ActionClick(this, EventArgs.Empty); }
        protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if (Enabled && !actionBounds.IsEmpty && (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)) { if (ActionClick != null) ActionClick(this, EventArgs.Empty); e.Handled = true; } }
    }
}
