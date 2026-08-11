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
            Padding = new Padding(4);
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
            ImageScalingSize = new Size(theme.IconSize, theme.IconSize);
            Padding = new Padding(Math.Max(4, theme.SpacingSmall));
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
            int contentHeight = Math.Max(Font.Height, ImageScalingSize.Height);
            int itemHeight = Math.Max(theme.ControlHeight, contentHeight + theme.SpacingMedium);
            int verticalPadding = Math.Max(3, (itemHeight - contentHeight) / 2);
            item.Padding = new Padding(theme.SpacingMedium, verticalPadding,
                theme.SpacingMedium, verticalPadding);
            item.Margin = Padding.Empty;
            ToolStripMenuItem menuItem = item as ToolStripMenuItem;
            if (menuItem == null) return;
            menuItem.DropDown.BackColor = theme.Surface;
            menuItem.DropDown.ForeColor = theme.Text;
            menuItem.DropDown.Font = Font;
            menuItem.DropDown.Padding = Padding;
            menuItem.DropDown.ImageScalingSize = ImageScalingSize;
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
                e.Graphics.Clear(owner.theme.Surface);
                ZarpaPaint.FillRounded(e.Graphics, owner.theme.Surface,
                    new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1),
                    owner.theme.GroupCornerRadius);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                Color border = owner.theme.Preset == ZarpaThemePreset.HighContrast
                    ? owner.theme.Text : owner.theme.Border;
                ZarpaPaint.DrawRounded(e.Graphics, border,
                    new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1),
                    owner.theme.GroupCornerRadius, owner.theme.BorderThickness);
            }

            protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
            {
                // El fondo completo ya lo pinta el menú. Evita la franja con colores
                // del renderer profesional de Windows detrás de los iconos.
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                float hover = owner.GetHoverProgress(e.Item);
                if (hover > 0F)
                {
                    Color target = HoverColor(e.Item);
                    Color color = ZarpaPaint.Blend(owner.theme.Surface, target, hover);
                    Rectangle bounds = new Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
                    ZarpaPaint.FillRounded(e.Graphics, color, bounds, owner.theme.CornerRadius);
                    if (owner.theme.Preset == ZarpaThemePreset.HighContrast)
                    {
                        Color border = ZarpaPaint.Blend(owner.theme.Surface, owner.theme.Text, hover);
                        ZarpaPaint.DrawRounded(e.Graphics, border, bounds,
                            owner.theme.CornerRadius, owner.theme.BorderThickness);
                    }
                }
            }

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                ZarpaMenuItem item = e.Item as ZarpaMenuItem;
                Color text = !e.Item.Enabled ? owner.theme.TextMuted : item != null && item.Tone == ZarpaMenuItemTone.Danger
                    ? ReadableDanger() : item != null && item.Tone == ZarpaMenuItemTone.Accent
                    ? owner.theme.Accent : owner.theme.Text;
                float reveal = owner.GetRevealProgress(e.Item);
                e.TextColor = ZarpaPaint.Blend(owner.theme.Surface, text, reveal);
                Rectangle textBounds = e.TextRectangle;
                textBounds.Y = 0;
                textBounds.Height = e.Item.Height;
                e.TextRectangle = textBounds;
                e.TextFormat |= TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
                DrawRevealed(e.Graphics, reveal, delegate { base.OnRenderItemText(e); });
            }

            protected override void OnRenderItemImage(ToolStripItemImageRenderEventArgs e)
            {
                float reveal = owner.GetRevealProgress(e.Item);
                ZarpaMenuItem item = e.Item as ZarpaMenuItem;
                if (item != null && !string.IsNullOrEmpty(item.IconKey))
                {
                    Color icon = !item.Enabled ? owner.theme.TextMuted : item.Tone == ZarpaMenuItemTone.Danger
                        ? ReadableDanger() : item.Tone == ZarpaMenuItemTone.Accent ? owner.theme.Accent : owner.theme.TextMuted;
                    if (item.Enabled && item.Tone == ZarpaMenuItemTone.Neutral)
                        icon = ZarpaPaint.Blend(owner.theme.Text, owner.theme.TextMuted, .38F);
                    icon = ZarpaPaint.Blend(owner.theme.Surface, icon, reveal);
                    Rectangle bounds = CenteredSquare(e.ImageRectangle, e.Item.Height);
                    DrawRevealed(e.Graphics, reveal, delegate
                    {
                        FluentIconCatalog.TryDraw(e.Graphics, item.IconKey, bounds, icon,
                            Math.Max(12F, Math.Min(bounds.Width, bounds.Height) - 2F));
                    });
                    return;
                }
                DrawRevealed(e.Graphics, reveal, delegate { base.OnRenderItemImage(e); });
            }

            protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
            {
                Rectangle bounds = CenteredSquare(e.ImageRectangle, e.Item.Height);
                FluentIconCatalog.TryDraw(e.Graphics, "ic_fluent_checkmark_20_regular", bounds,
                    owner.theme.Accent, Math.Max(12F, bounds.Height - 5F));
            }

            protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
            {
                int size = Math.Max(12, Math.Min(16, e.Item.Height - 10));
                int centerX = e.ArrowRectangle.Left + e.ArrowRectangle.Width / 2;
                Rectangle bounds = new Rectangle(centerX - size / 2,
                    (e.Item.Height - size) / 2, size, size);
                Color arrow = e.Item.Enabled
                    ? ZarpaPaint.Blend(owner.theme.Text, owner.theme.TextMuted, .5F)
                    : owner.theme.BorderStrong;
                FluentIconCatalog.TryDraw(e.Graphics, "ic_fluent_chevron_right_20_regular", bounds,
                    arrow, Math.Max(11F, bounds.Height - 3F));
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                int y = e.Item.Height / 2;
                Color separator = owner.theme.Preset == ZarpaThemePreset.HighContrast
                    ? owner.theme.Text : owner.theme.Border;
                using (Pen pen = new Pen(separator))
                    e.Graphics.DrawLine(pen, owner.theme.SpacingMedium, y,
                        e.Item.Width - owner.theme.SpacingMedium, y);
            }

            private Color HoverColor(ToolStripItem item)
            {
                if (owner.theme.Preset == ZarpaThemePreset.HighContrast)
                    return owner.theme.Selection;

                Color color = ZarpaPaint.Blend(owner.theme.SurfaceRaised,
                    owner.theme.Selection, .32F);
                ZarpaMenuItem menuItem = item as ZarpaMenuItem;
                if (menuItem == null || !item.Enabled) return color;
                if (menuItem.Tone == ZarpaMenuItemTone.Danger)
                    return ZarpaPaint.Blend(color, ReadableDanger(), .10F);
                if (menuItem.Tone == ZarpaMenuItemTone.Accent)
                    return ZarpaPaint.Blend(color, owner.theme.Accent, .08F);
                return color;
            }

            private Color ReadableDanger()
            {
                return owner.theme.Surface.GetBrightness() < .35F
                    ? ZarpaPaint.Blend(owner.theme.Danger, owner.theme.Text, .22F)
                    : owner.theme.Danger;
            }

            private static Rectangle CenteredSquare(Rectangle slot, int itemHeight)
            {
                int availableHeight = Math.Max(1, itemHeight - 8);
                int size = Math.Max(1, Math.Min(Math.Min(slot.Width, slot.Height), availableHeight));
                return new Rectangle(slot.Left + (slot.Width - size) / 2,
                    (itemHeight - size) / 2, size, size);
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
