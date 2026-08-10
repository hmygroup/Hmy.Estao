using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace ZarpaSuite.Controls
{
    public sealed class ZarpaToast
    {
        internal string Title, Message, ActionText;
        internal ZarpaFeedbackKind Kind;
        internal DateTime ExpiresAt;
        internal Action Changed;
        private bool dismissed;
        public event EventHandler ActionClick;
        public event EventHandler Dismissed;
        public void Dismiss() { if (dismissed) return; dismissed = true; if (Changed != null) Changed(); if (Dismissed != null) Dismissed(this, EventArgs.Empty); }
        internal void PerformAction() { if (ActionClick != null) ActionClick(this, EventArgs.Empty); }
    }

    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(NotifyIcon))]
    public sealed class ZarpaToastManager : Component
    {
        private int defaultDuration = 4500, maxVisible = 4;
        private readonly Dictionary<Control, ZarpaToastOverlay> overlays = new Dictionary<Control, ZarpaToastOverlay>();
        public ZarpaToastManager() { }
        public ZarpaToastManager(IContainer container) { if (container != null) container.Add(this); }
        [Category("Comportamiento"), DefaultValue(4500)] public int DefaultDuration { get { return defaultDuration; } set { defaultDuration = Math.Max(1000, value); } }
        [Category("Comportamiento"), DefaultValue(4)] public int MaxVisible { get { return maxVisible; } set { maxVisible = Math.Max(1, Math.Min(8, value)); } }

        public ZarpaToast Show(Control owner, string title, string message, ZarpaFeedbackKind kind)
        {
            return Show(owner, title, message, kind, defaultDuration, string.Empty, null);
        }
        public ZarpaToast Show(Control owner, string title, string message, ZarpaFeedbackKind kind, int duration, string actionText, ZarpaThemeTokens theme)
        {
            if (owner == null) throw new ArgumentNullException("owner");
            Control root = owner.FindForm() as Control ?? owner;
            ZarpaToastOverlay overlay;
            if (!overlays.TryGetValue(root, out overlay) || overlay.IsDisposed)
            {
                overlay = new ZarpaToastOverlay();
                overlay.Disposed += delegate { overlays.Remove(root); };
                root.Controls.Add(overlay);
                overlays[root] = overlay;
            }
            overlay.MaxVisible = maxVisible;
            if (theme != null) overlay.ApplyTheme(theme);
            overlay.BringToFront();
            ZarpaToast toast = new ZarpaToast { Title = title ?? string.Empty, Message = message ?? string.Empty,
                Kind = kind, ActionText = actionText ?? string.Empty, ExpiresAt = DateTime.UtcNow.AddMilliseconds(Math.Max(1000, duration)) };
            overlay.Add(toast);
            return toast;
        }
        protected override void Dispose(bool disposing) { if (disposing) foreach (ZarpaToastOverlay value in new List<ZarpaToastOverlay>(overlays.Values)) value.Dispose(); overlays.Clear(); base.Dispose(disposing); }
    }

    internal sealed class ZarpaToastOverlay : Control, IZarpaThemeAware
    {
        private readonly List<ZarpaToast> toasts = new List<ZarpaToast>();
        private readonly Dictionary<ZarpaToast, float> toastProgress = new Dictionary<ZarpaToast, float>();
        private readonly Timer timer; private readonly ZarpaPaintAnimator motionAnimator; private ZarpaThemeTokens theme; private readonly List<Rectangle> closeBounds = new List<Rectangle>(), actionBounds = new List<Rectangle>();
        internal int MaxVisible { get; set; }
        internal ZarpaToastOverlay()
        {
            theme = new ZarpaThemeTokens(Invalidate); MaxVisible = 4; Width = 390; Height = 1;
            Anchor = AnchorStyles.Top | AnchorStyles.Right;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            SetStyle(ControlStyles.Opaque, false);
            BackColor = Color.Transparent;
            timer = new Timer { Interval = 100, Enabled = false }; timer.Tick += TimerTick;
            motionAnimator = new ZarpaPaintAnimator(this, AdvanceMotion);
            Visible = false;
        }
        internal void Add(ZarpaToast toast)
        {
            toast.Changed = delegate { Remove(toast); };
            toastProgress[toast] = ShouldAnimate ? 0F : 1F;
            toasts.Insert(0, toast); while (toasts.Count > MaxVisible) Remove(toasts[toasts.Count - 1]);
            UpdateOverlayState();
            motionAnimator.Update(ShouldAnimate && Visible);
            Invalidate();
        }
        public void ApplyTheme(ZarpaThemeTokens value) { if (value == null) return; theme = value; Font = new Font(theme.FontFamily, theme.FontSize); if (!ShouldAnimate) SnapMotion(); UpdateOverlayRegion(); Invalidate(); }
        protected override void Dispose(bool disposing) { if (disposing) { timer.Dispose(); motionAnimator.Dispose(); Region region = Region; Region = null; if (region != null) region.Dispose(); } base.Dispose(disposing); }
        protected override void OnParentChanged(EventArgs e) { base.OnParentChanged(e); PositionOverlay(); }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); motionAnimator.Update(ShouldAnimate && Visible && HasMovingToasts()); }
        protected override void OnHandleDestroyed(EventArgs e) { motionAnimator.Stop(); base.OnHandleDestroyed(e); }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (timer != null) timer.Enabled = Visible && toasts.Count > 0 && !IsDesignerHosted; if (motionAnimator != null) motionAnimator.Update(ShouldAnimate && Visible && HasMovingToasts()); }
        protected override void OnSizeChanged(EventArgs e) { base.OnSizeChanged(e); UpdateOverlayRegion(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); closeBounds.Clear(); actionBounds.Clear();
            for (int i = 0; i < toasts.Count; i++)
            {
                ZarpaToast toast = toasts[i]; Rectangle card = GetCardBounds(i, GetProgress(toast));
                if (theme.ShadowDepth > 0) ZarpaPaint.FillRounded(e.Graphics, theme.Shadow, new Rectangle(card.X + theme.ShadowDepth, card.Y + theme.ShadowDepth, card.Width, card.Height), theme.CornerRadius);
                ZarpaPaint.FillRounded(e.Graphics, theme.Surface, card, theme.CornerRadius); ZarpaPaint.DrawRounded(e.Graphics, theme.Border, card, theme.CornerRadius, theme.BorderThickness);
                Color accent = ZarpaFeedbackPalette.Get(theme, toast.Kind); Rectangle stripe = new Rectangle(card.Left, card.Top + theme.CornerRadius, 4, card.Height - theme.CornerRadius * 2); using (SolidBrush b = new SolidBrush(accent)) e.Graphics.FillRectangle(b, stripe);
                Rectangle icon = new Rectangle(card.Left + 14, card.Top + 14, theme.IconSize, theme.IconSize); FluentIconCatalog.TryDraw(e.Graphics, ZarpaFeedbackPalette.Icon(toast.Kind), icon, accent, theme.IconSize - 1F);
                Rectangle close = new Rectangle(card.Right - 30, card.Top + 8, 24, 24); closeBounds.Add(close); TextRenderer.DrawText(e.Graphics, "×", Font, close, theme.TextMuted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                int textLeft = icon.Right + 10; using (Font titleFont = new Font(Font, FontStyle.Bold)) TextRenderer.DrawText(e.Graphics, toast.Title, titleFont, new Rectangle(textLeft, card.Top + 10, card.Right - textLeft - 38, 20), theme.Text, TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(e.Graphics, toast.Message, Font, new Rectangle(textLeft, card.Top + 31, card.Right - textLeft - 12, 35), theme.TextMuted, TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
                Rectangle action = Rectangle.Empty; if (!string.IsNullOrEmpty(toast.ActionText)) { int aw = TextRenderer.MeasureText(toast.ActionText, Font).Width + 12; action = new Rectangle(card.Right - aw - 10, card.Bottom - 28, aw, 22); TextRenderer.DrawText(e.Graphics, toast.ActionText, Font, action, accent, TextFormatFlags.Right | TextFormatFlags.VerticalCenter); } actionBounds.Add(action);
            }
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e); for (int i = 0; i < toasts.Count; i++) { if (i < closeBounds.Count && closeBounds[i].Contains(e.Location)) { toasts[i].Dismiss(); return; } if (i < actionBounds.Count && actionBounds[i].Contains(e.Location)) { toasts[i].PerformAction(); return; } }
        }
        private void TimerTick(object sender, EventArgs e) { DateTime now = DateTime.UtcNow; for (int i = toasts.Count - 1; i >= 0; i--) if (toasts[i].ExpiresAt <= now) Remove(toasts[i]); }
        private void Remove(ZarpaToast toast) { if (toasts.Remove(toast)) { toastProgress.Remove(toast); toast.Changed = null; UpdateOverlayState(); Invalidate(); } }
        private void UpdateOverlayState()
        {
            int count = Math.Min(MaxVisible, toasts.Count);
            Height = count == 0 ? 1 : theme.SpacingMedium + count * 76 + Math.Max(0, count - 1) * theme.SpacingMedium + theme.SpacingMedium;
            Visible = count > 0;
            timer.Enabled = Visible && !IsDesignerHosted;
            PositionOverlay();
            UpdateOverlayRegion();
            motionAnimator.Update(ShouldAnimate && Visible && HasMovingToasts());
        }
        private void PositionOverlay()
        {
            if (Parent == null) return;
            Width = Math.Max(220, Math.Min(390, Parent.ClientSize.Width - 24));
            Location = new Point(Math.Max(0, Parent.ClientSize.Width - Width - 12), Math.Min(46, Math.Max(0, Parent.ClientSize.Height - Height)));
            BringToFront();
        }
        private bool IsDesignerHosted { get { return DesignMode || (Site != null && Site.DesignMode) || LicenseManager.UsageMode == LicenseUsageMode.Designtime; } }
        private bool ShouldAnimate { get { return theme.MotionEnabled && !IsDesignerHosted; } }
        private float GetProgress(ZarpaToast toast)
        {
            float progress;
            return toastProgress.TryGetValue(toast, out progress) ? progress : 1F;
        }
        private Rectangle GetCardBounds(int index, float progress)
        {
            float eased = 1F - (float)Math.Pow(1F - Math.Max(0F, Math.Min(1F, progress)), 3F);
            int slide = (int)Math.Round((1F - eased) * 36F);
            return new Rectangle(theme.SpacingMedium + slide,
                theme.SpacingMedium + index * (76 + theme.SpacingMedium),
                Width - theme.SpacingLarge, 76);
        }
        private bool HasMovingToasts()
        {
            for (int i = 0; i < toasts.Count; i++)
                if (GetProgress(toasts[i]) < 1F) return true;
            return false;
        }
        private void AdvanceMotion(float elapsed)
        {
            if (!ShouldAnimate) { SnapMotion(); return; }
            bool moving = false;
            float amount = elapsed * 1000F / Math.Max(180, theme.TabDuration);
            for (int i = 0; i < toasts.Count; i++)
            {
                ZarpaToast toast = toasts[i];
                float current = GetProgress(toast);
                if (current >= 1F) continue;
                Rectangle previous = GetCardBounds(i, current);
                float next = Math.Min(1F, current + amount);
                toastProgress[toast] = next;
                Rectangle dirty = Rectangle.Union(previous, GetCardBounds(i, next));
                dirty.Inflate(theme.ShadowDepth + 1, theme.ShadowDepth + 1);
                dirty.Intersect(ClientRectangle);
                if (!dirty.IsEmpty) Invalidate(dirty);
                if (next < 1F) moving = true;
            }
            UpdateOverlayRegion();
            if (!moving) motionAnimator.Stop();
        }
        private void SnapMotion()
        {
            motionAnimator.Stop();
            for (int i = 0; i < toasts.Count; i++) toastProgress[toasts[i]] = 1F;
            UpdateOverlayRegion();
        }
        private void UpdateOverlayRegion()
        {
            if (IsDisposed || Width <= 0 || Height <= 0) return;
            Region next = new Region();
            next.MakeEmpty();
            for (int i = 0; i < toasts.Count; i++)
            {
                Rectangle card = GetCardBounds(i, GetProgress(toasts[i]));
                card.Width += Math.Max(0, theme.ShadowDepth);
                card.Height += Math.Max(0, theme.ShadowDepth);
                card.Intersect(ClientRectangle);
                if (!card.IsEmpty) next.Union(card);
            }
            Region previous = Region;
            Region = next;
            if (previous != null) previous.Dispose();
        }
    }

    public enum ZarpaDialogButtons { Ok, OkCancel, YesNo }

    public static class ZarpaDialog
    {
        public static DialogResult Show(IWin32Window owner, string title, string message, ZarpaFeedbackKind kind)
        {
            return Show(owner, title, message, kind, ZarpaDialogButtons.Ok, null);
        }
        public static DialogResult Show(IWin32Window owner, string title, string message, ZarpaFeedbackKind kind, ZarpaDialogButtons buttons, ZarpaThemeTokens theme)
        {
            using (ZarpaDialogForm form = new ZarpaDialogForm(title, message, kind, buttons, theme)) return form.ShowDialog(owner);
        }
    }

    internal sealed class ZarpaDialogForm : Form
    {
        internal ZarpaDialogForm(string title, string message, ZarpaFeedbackKind kind, ZarpaDialogButtons buttons, ZarpaThemeTokens value)
        {
            ZarpaThemeTokens theme = value ?? new ZarpaThemeTokens(null); Text = title ?? string.Empty; FormBorderStyle = FormBorderStyle.FixedDialog; StartPosition = FormStartPosition.CenterParent; ShowInTaskbar = false; MaximizeBox = false; MinimizeBox = false; ClientSize = new Size(470, 210); BackColor = theme.Surface; ForeColor = theme.Text; Font = new Font(theme.FontFamily, theme.FontSize);
            Label icon = new Label { Location = new Point(26, 28), Size = new Size(42, 42), Font = new Font(Font.FontFamily, 22F), ForeColor = ZarpaFeedbackPalette.Get(theme, kind), TextAlign = ContentAlignment.MiddleCenter, Text = kind == ZarpaFeedbackKind.Error ? "!" : kind == ZarpaFeedbackKind.Warning ? "!" : "i" };
            Label heading = new Label { Location = new Point(84, 25), Size = new Size(354, 30), Font = new Font(Font, FontStyle.Bold), Text = title ?? string.Empty };
            Label body = new Label { Location = new Point(84, 60), Size = new Size(354, 78), ForeColor = theme.TextMuted, Text = message ?? string.Empty };
            Controls.Add(icon); Controls.Add(heading); Controls.Add(body);
            Button primary = CreateButton(theme, buttons == ZarpaDialogButtons.YesNo ? "Sí" : "Aceptar", DialogResult.OK, true); primary.Location = new Point(338, 158); Controls.Add(primary); AcceptButton = primary;
            if (buttons != ZarpaDialogButtons.Ok) { Button secondary = CreateButton(theme, buttons == ZarpaDialogButtons.YesNo ? "No" : "Cancelar", DialogResult.Cancel, false); secondary.Location = new Point(220, 158); Controls.Add(secondary); CancelButton = secondary; }
        }
        private static Button CreateButton(ZarpaThemeTokens theme, string text, DialogResult result, bool primary) { return new Button { Size = new Size(104, theme.ControlHeight), Text = text, DialogResult = result, FlatStyle = FlatStyle.Flat, BackColor = primary ? theme.Accent : theme.SurfaceRaised, ForeColor = primary ? Color.White : theme.Text, FlatAppearance = { BorderColor = primary ? theme.Accent : theme.Border } }; }
    }
}
