using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Design;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Windows.Forms;

namespace ZarpaSuite.Controls
{
    public enum ZarpaMenuItemTone
    {
        Neutral,
        Accent,
        Danger
    }

    [ToolboxItem(false)]
    public class ZarpaMenuItem : ToolStripMenuItem
    {
        private static readonly Image IconPlaceholder = new Bitmap(1, 1);
        private string iconKey = string.Empty;
        private ZarpaMenuItemTone tone;

        public ZarpaMenuItem() : this("Opción", string.Empty, null) { }
        public ZarpaMenuItem(string text) : this(text, string.Empty, null) { }
        public ZarpaMenuItem(string text, string iconKey, EventHandler clickHandler)
            : base(text, null, clickHandler)
        {
            this.iconKey = iconKey ?? string.Empty;
            Image = string.IsNullOrEmpty(this.iconKey) ? null : IconPlaceholder;
        }

        [Category("Icono"), DefaultValue("")]
        [Editor("ZarpaSuite.Controls.Design.FluentIconPickerEditor, Zarpa.Controls", typeof(UITypeEditor))]
        public string IconKey
        {
            get { return iconKey; }
            set
            {
                iconKey = value ?? string.Empty;
                Image = string.IsNullOrEmpty(iconKey) ? null : IconPlaceholder;
                Invalidate();
            }
        }

        [Category("Apariencia"), DefaultValue(ZarpaMenuItemTone.Neutral)]
        public ZarpaMenuItemTone Tone
        {
            get { return tone; }
            set { tone = value; Invalidate(); }
        }
    }

    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(ContextMenuStrip))]
    [DefaultEvent("ItemClicked")]
    public class ZarpaContextMenu : ContextMenuStrip, IZarpaThemeAware
    {
        private readonly System.Threading.Timer motionTimer;
        private readonly Dictionary<ToolStripItem, float> hoverProgress = new Dictionary<ToolStripItem, float>();
        private ZarpaThemeTokens theme;
        private long lastMotionTimestamp;
        private long openingStartedTimestamp;
        private float openingProgress = 1F;
        private bool motionRunning;
        private int motionTickPending;

        public ZarpaContextMenu()
        {
            theme = new ZarpaThemeTokens(InvalidateMenu);
            Renderer = new ZarpaContextMenuRenderer(this);
            AutoSize = true;
            ShowImageMargin = true;
            ShowCheckMargin = false;
            DropShadowEnabled = true;
            Padding = new Padding(6);
            MinimumSize = new Size(224, 0);
            Font = new Font("Segoe UI", 9F);
            motionTimer = new System.Threading.Timer(MotionClockPulse, null,
                Timeout.Infinite, Timeout.Infinite);
            ApplyTheme(theme);
        }

        public ZarpaContextMenu(IContainer container) : this()
        {
            if (container != null) container.Add(this);
        }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public ZarpaThemeTokens Theme { get { return theme; } }

        public void ApplyTheme(ZarpaThemeTokens value)
        {
            if (value == null) return;
            theme = value;
            if (!ShouldAnimate) SnapMotion();
            BackColor = theme.Surface;
            ForeColor = theme.Text;
            Font = new Font(theme.FontFamily, theme.FontSize);
            Padding = new Padding(Math.Max(4, theme.SpacingSmall + 2));
            ApplyItemAppearance(Items);
            InvalidateMenu();
        }

        protected override void OnItemAdded(ToolStripItemEventArgs e)
        {
            base.OnItemAdded(e);
            ApplyItemAppearance(e.Item);
        }

        protected override void OnOpening(CancelEventArgs e)
        {
            ApplyItemAppearance(Items);
            openingProgress = ShouldAnimate ? 0F : 1F;
            openingStartedTimestamp = Stopwatch.GetTimestamp();
            base.OnOpening(e);
            if (!e.Cancel && ShouldAnimate) StartMotion();
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            UpdateRoundedRegion(this);
        }

        protected override void OnMouseMove(MouseEventArgs mea)
        {
            base.OnMouseMove(mea);
            if (ShouldAnimate) StartMotion();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (ShouldAnimate) StartMotion();
        }

        protected override void OnClosed(ToolStripDropDownClosedEventArgs e)
        {
            motionRunning = false;
            motionTimer.Change(Timeout.Infinite, Timeout.Infinite);
            hoverProgress.Clear();
            openingProgress = 1F;
            base.OnClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                motionRunning = false;
                motionTimer.Dispose();
            }
            base.Dispose(disposing);
        }

        internal float GetHoverProgress(ToolStripItem item)
        {
            float value;
            if (!hoverProgress.TryGetValue(item, out value)) value = 0F;
            if (ShouldAnimate && value < (item.Selected ? 1F : 0F)) StartMotion();
            return ShouldAnimate ? value : item.Selected ? 1F : 0F;
        }

        internal float GetRevealProgress(ToolStripItem item)
        {
            if (!ShouldAnimate) return 1F;
            int index = item.Owner == null ? 0 : item.Owner.Items.IndexOf(item);
            float value = openingProgress * 1.18F - Math.Max(0, index) * 0.035F;
            return Math.Max(0F, Math.Min(1F, value));
        }

        private bool ShouldAnimate
        {
            get
            {
                return theme.MotionEnabled && LicenseManager.UsageMode != LicenseUsageMode.Designtime;
            }
        }

        private void ApplyItemAppearance(ToolStripItemCollection collection)
        {
            foreach (ToolStripItem item in collection) ApplyItemAppearance(item);
        }

        private void ApplyItemAppearance(ToolStripItem item)
        {
            if (item == null) return;
            item.ForeColor = item.Enabled ? theme.Text : theme.TextMuted;
            ToolStripSeparator separator = item as ToolStripSeparator;
            if (separator != null)
            {
                separator.Margin = new Padding(theme.SpacingMedium, theme.SpacingSmall,
                    theme.SpacingMedium, theme.SpacingSmall);
                return;
            }

            item.AutoSize = true;
            item.Padding = new Padding(theme.SpacingMedium, 7, theme.SpacingMedium, 7);
            item.Margin = new Padding(0, 1, 0, 1);
            ToolStripMenuItem menuItem = item as ToolStripMenuItem;
            if (menuItem == null) return;
            menuItem.DropDown.BackColor = theme.Surface;
            menuItem.DropDown.ForeColor = theme.Text;
            menuItem.DropDown.Font = Font;
            menuItem.DropDown.Padding = Padding;
            menuItem.DropDown.Renderer = Renderer;
            menuItem.DropDown.Opened -= ChildDropDownOpened;
            menuItem.DropDown.Opened += ChildDropDownOpened;
            ApplyItemAppearance(menuItem.DropDownItems);
        }

        private void ChildDropDownOpened(object sender, EventArgs e)
        {
            ToolStripDropDown dropDown = sender as ToolStripDropDown;
            if (dropDown != null) UpdateRoundedRegion(dropDown);
        }

        private void UpdateRoundedRegion(ToolStripDropDown dropDown)
        {
            if (dropDown.Width <= 0 || dropDown.Height <= 0) return;
            int radius = Math.Max(0, theme.GroupCornerRadius);
            Region previous = dropDown.Region;
            if (radius == 0)
                dropDown.Region = null;
            else
            {
                using (GraphicsPath path = ZarpaPaint.RoundedPath(
                    new Rectangle(0, 0, dropDown.Width, dropDown.Height), radius))
                    dropDown.Region = new Region(path);
            }
            if (previous != null) previous.Dispose();
        }

        private void StartMotion()
        {
            if (!ShouldAnimate || IsDisposed) return;
            if (!motionRunning)
            {
                motionRunning = true;
                lastMotionTimestamp = Stopwatch.GetTimestamp();
                motionTimer.Change(0, 15);
            }
        }

        private void MotionClockPulse(object state)
        {
            if (!motionRunning || IsDisposed || !IsHandleCreated) return;
            if (Interlocked.Exchange(ref motionTickPending, 1) != 0) return;
            try { BeginInvoke((MethodInvoker)ProcessMotionFrame); }
            catch (InvalidOperationException) { Interlocked.Exchange(ref motionTickPending, 0); }
        }

        private void ProcessMotionFrame()
        {
            Interlocked.Exchange(ref motionTickPending, 0);
            if (IsDisposed || !Visible || !ShouldAnimate)
            {
                SnapMotion();
                return;
            }

            long now = Stopwatch.GetTimestamp();
            float elapsed = (float)((now - lastMotionTimestamp) * 1000D / Stopwatch.Frequency);
            lastMotionTimestamp = now;
            bool active = false;
            bool revealMoving = false;
            if (openingProgress < 1F)
            {
                float linear = (float)((now - openingStartedTimestamp) * 1000D /
                    Stopwatch.Frequency / Math.Max(150, theme.TabDuration));
                linear = Math.Max(0F, Math.Min(1F, linear));
                openingProgress = 1F - (float)Math.Pow(1F - linear, 3F);
                active = openingProgress < 1F;
                revealMoving = true;
            }

            active |= UpdateHoverAnimations(Items, elapsed);
            if (revealMoving) InvalidateMenu();
            if (!active)
            {
                motionRunning = false;
                motionTimer.Change(Timeout.Infinite, Timeout.Infinite);
            }
        }

        private bool UpdateHoverAnimations(ToolStripItemCollection collection, float elapsed)
        {
            bool active = false;
            foreach (ToolStripItem item in collection)
            {
                float current;
                if (!hoverProgress.TryGetValue(item, out current)) current = 0F;
                float target = item.Selected && item.Enabled ? 1F : 0F;
                float duration = target > current ? theme.HoverDuration : Math.Max(70, theme.PressDuration);
                float next = current + Math.Sign(target - current) * elapsed / duration;
                if ((target > current && next > target) || (target < current && next < target)) next = target;
                next = Math.Max(0F, Math.Min(1F, next));
                if (Math.Abs(next - current) > 0.001F)
                {
                    hoverProgress[item] = next;
                    if (item.Owner != null) item.Owner.Invalidate(item.Bounds);
                }
                if (Math.Abs(target - next) > 0.001F) active = true;
                ToolStripMenuItem menuItem = item as ToolStripMenuItem;
                if (menuItem != null && menuItem.HasDropDownItems)
                    active |= UpdateHoverAnimations(menuItem.DropDownItems, elapsed);
            }
            return active;
        }

        private void InvalidateMenu()
        {
            if (!IsDisposed) Invalidate(true);
        }

        private void SnapMotion()
        {
            motionRunning = false;
            motionTimer.Change(Timeout.Infinite, Timeout.Infinite);
            openingProgress = 1F;
            hoverProgress.Clear();
        }

        private sealed class ZarpaContextMenuRenderer : ToolStripProfessionalRenderer
        {
            private readonly ZarpaContextMenu owner;

            internal ZarpaContextMenuRenderer(ZarpaContextMenu menu)
            {
                owner = menu;
                RoundedEdges = false;
            }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                e.Graphics.Clear(Color.Transparent);
                ZarpaPaint.FillRounded(e.Graphics, owner.theme.Surface,
                    new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1),
                    owner.theme.GroupCornerRadius);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                ZarpaPaint.DrawRounded(e.Graphics, owner.theme.Border,
                    new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1),
                    owner.theme.GroupCornerRadius, owner.theme.BorderThickness);
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                float hover = owner.GetHoverProgress(e.Item);
                if (hover > 0F)
                {
                    Color color = ZarpaPaint.Blend(owner.theme.Surface, owner.theme.SurfaceRaised, hover);
                    Rectangle bounds = new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height);
                    ZarpaPaint.FillRounded(e.Graphics, color, bounds, owner.theme.CornerRadius);
                    if (hover > .72F)
                    {
                        Color border = ZarpaPaint.Blend(color, owner.theme.Border, (hover - .72F) / .28F);
                        ZarpaPaint.DrawRounded(e.Graphics, border, bounds, owner.theme.CornerRadius, 1);
                    }
                }

            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                ZarpaMenuItem item = e.Item as ZarpaMenuItem;
                Color text = !e.Item.Enabled ? owner.theme.TextMuted : item != null && item.Tone == ZarpaMenuItemTone.Danger
                    ? owner.theme.Danger : item != null && item.Tone == ZarpaMenuItemTone.Accent
                    ? owner.theme.Accent : owner.theme.Text;
                float reveal = owner.GetRevealProgress(e.Item);
                e.TextColor = ZarpaPaint.Blend(owner.theme.Surface, text, reveal);
                DrawRevealed(e.Graphics, reveal, delegate { base.OnRenderItemText(e); });
            }

            protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
            {
                float reveal = owner.GetRevealProgress(e.Item);
                ZarpaMenuItem item = e.Item as ZarpaMenuItem;
                if (item != null && !string.IsNullOrEmpty(item.IconKey))
                {
                    Color icon = !item.Enabled ? owner.theme.TextMuted : item.Tone == ZarpaMenuItemTone.Danger
                        ? owner.theme.Danger : item.Tone == ZarpaMenuItemTone.Accent ? owner.theme.Accent : owner.theme.TextMuted;
                    icon = ZarpaPaint.Blend(owner.theme.Surface, icon, reveal);
                    Rectangle bounds = new Rectangle(e.ImageRectangle.X + 1, e.ImageRectangle.Y,
                        owner.theme.IconSize, owner.theme.IconSize);
                    DrawRevealed(e.Graphics, reveal, delegate
                    {
                        FluentIconCatalog.TryDraw(e.Graphics, item.IconKey, bounds, icon, owner.theme.IconSize - 2F);
                    });
                    return;
                }
                DrawRevealed(e.Graphics, reveal, delegate { base.OnRenderItemImage(e); });
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                Rectangle bounds = new Rectangle(e.ImageRectangle.X + 2, e.ImageRectangle.Y + 2,
                    e.ImageRectangle.Width - 4, e.ImageRectangle.Height - 4);
                FluentIconCatalog.TryDraw(e.Graphics, "ic_fluent_checkmark_20_regular", bounds,
                    owner.theme.Accent, 15F);
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                Rectangle bounds = new Rectangle(e.ArrowRectangle.X, e.ArrowRectangle.Y,
                    e.ArrowRectangle.Width, e.ArrowRectangle.Height);
                FluentIconCatalog.TryDraw(e.Graphics, "ic_fluent_chevron_right_20_regular", bounds,
                    e.Item.Enabled ? owner.theme.TextMuted : owner.theme.BorderStrong, 13F);
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                int y = e.Item.Height / 2;
                using (Pen pen = new Pen(owner.theme.Border))
                    e.Graphics.DrawLine(pen, owner.theme.SpacingMedium, y,
                        e.Item.Width - owner.theme.SpacingMedium, y);
            }

            private static void DrawRevealed(Graphics graphics, float reveal, MethodInvoker draw)
            {
                GraphicsState state = graphics.Save();
                graphics.TranslateTransform(0F, (1F - reveal) * -7F);
                try { draw(); }
                finally { graphics.Restore(state); }
            }
        }
    }
}

