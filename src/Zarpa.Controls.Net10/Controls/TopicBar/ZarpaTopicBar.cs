using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Design;
using System.Windows.Forms;

namespace ZarpaSuite.Controls
{
    public enum ZarpaTopicBarDensity
    {
        Theme,
        UltraCompact,
        Compact,
        Comfortable,
        Spacious,
        Custom
    }

    [DefaultEvent("LinkClicked")]
    [DefaultProperty("Pages")]
    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(TreeView))]
    [Designer("ZarpaSuite.Controls.Design.ZarpaTopicBarDesigner, Zarpa.Controls")]
    public class ZarpaTopicBar : ScrollableControl, IZarpaThemeAware
    {
        private readonly ZarpaTopicPageCollection pages;
        private readonly ToolTip toolTip;
        private readonly ZarpaPaintAnimator animator;
        private readonly ZarpaScrollBar scrollBar;
        private readonly Dictionary<ZarpaTopicPage, float> expansionProgress =
            new Dictionary<ZarpaTopicPage, float>();
        private readonly Dictionary<object, float> hoverProgress = new Dictionary<object, float>();
        private readonly Dictionary<ZarpaTopicPage, Rectangle> addLinkBounds =
            new Dictionary<ZarpaTopicPage, Rectangle>();
        private ZarpaThemeTokens theme;
        private ZarpaDpiScale dpiScale = new ZarpaDpiScale(96, 96);
        private ImageList pageImageList;
        private ImageList linkImageList;
        private ZarpaTopicLink selectedLink;
        private ZarpaTopicPage selectedPage;
        private object hotObject;
        private object pressedObject;
        private object keyboardObject;
        private object toolTipObject;
        private object designSelectedObject;
        private Rectangle addPageBounds;
        private bool allowMultipleExpanded = true;
        private bool showToolTips = true;
        private bool autoSelectLinks = true;
        private ZarpaTopicBarDensity density = ZarpaTopicBarDensity.Theme;
        private ZarpaTopicBarDensity effectiveDensity = ZarpaTopicBarDensity.Compact;
        private int headerHeight = 40;
        private int linkHeight = 34;
        private int pageSpacing = 6;
        private Padding pagePadding = new Padding(6, 4, 6, 6);
        private bool layoutValid;
        private bool updatingLayout;
        private bool enforcingExpansion;
        private int contentHeight;
        private Font themeFont;

        public ZarpaTopicBar()
        {
            pages = new ZarpaTopicPageCollection(this);
            theme = new ZarpaThemeTokens(Invalidate);
            toolTip = new ToolTip();
            animator = new ZarpaPaintAnimator(this, AdvanceAnimation);
            scrollBar = new ZarpaScrollBar { Dock = DockStyle.Right, Width = 9, TabStop = false };
            scrollBar.ValueChanged += ScrollBarValueChanged;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
            base.AutoScroll = false;
            BackColor = theme.Canvas;
            ForeColor = theme.Text;
            themeFont = new Font("Segoe UI", 9F);
            Font = themeFont;
            Size = new Size(280, 420);
            MinimumSize = new Size(180, 120);
            TabStop = true;
            AccessibleRole = AccessibleRole.Grouping;
            Controls.Add(scrollBar);
        }

        [Browsable(false)]
        [EditorBrowsable(EditorBrowsableState.Never)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public new bool AutoScroll
        {
            get { return false; }
            set { base.AutoScroll = false; }
        }

        [Browsable(false)]
        [EditorBrowsable(EditorBrowsableState.Never)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public new Size AutoScrollMinSize
        {
            get { return Size.Empty; }
            set { base.AutoScrollMinSize = Size.Empty; }
        }

        [Category("Datos")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
        [Editor("ZarpaSuite.Controls.Design.ZarpaTopicPageCollectionEditor, Zarpa.Controls", typeof(UITypeEditor))]
        public ZarpaTopicPageCollection Pages { get { return pages; } }

        [Category("Imágenes"), DefaultValue(null)]
        public ImageList PageImageList
        {
            get { return pageImageList; }
            set { pageImageList = value; Invalidate(); }
        }

        [Category("Imágenes"), DefaultValue(null)]
        public ImageList LinkImageList
        {
            get { return linkImageList; }
            set { linkImageList = value; Invalidate(); }
        }

        [Category("Apariencia"), DefaultValue(ZarpaThemePreset.ZarpaLight)]
        public ZarpaThemePreset ThemePreset
        {
            get { return theme.Preset; }
            set { theme.Preset = value; ApplyCurrentTheme(); }
        }

        [Category("Comportamiento"), DefaultValue(true)]
        public bool AllowMultipleExpanded
        {
            get { return allowMultipleExpanded; }
            set
            {
                if (allowMultipleExpanded == value) return;
                allowMultipleExpanded = value;
                if (!value) KeepOnlyFirstExpanded();
                RefreshPages();
            }
        }

        [Category("Comportamiento"), DefaultValue(true)]
        public bool ShowToolTips
        {
            get { return showToolTips; }
            set
            {
                showToolTips = value;
                if (!value) toolTip.SetToolTip(this, string.Empty);
            }
        }

        [Category("Comportamiento"), DefaultValue(true)]
        public bool AutoSelectLinks
        {
            get { return autoSelectLinks; }
            set { autoSelectLinks = value; }
        }

        [Category("Diseño"), DefaultValue(ZarpaTopicBarDensity.Theme)]
        public ZarpaTopicBarDensity Density
        {
            get { return density; }
            set
            {
                if (density == value) return;
                density = value;
                if (value != ZarpaTopicBarDensity.Custom) ApplyDensity(value);
                else effectiveDensity = ZarpaTopicBarDensity.Custom;
                RefreshPages();
            }
        }

        [Category("Diseño"), DefaultValue(40)]
        public int HeaderHeight
        {
            get { return headerHeight; }
            set
            {
                int next = Math.Max(32, Math.Min(72, value));
                if (headerHeight == next) return;
                headerHeight = next;
                MarkCustomDensity();
                RefreshPages();
            }
        }

        [Category("Diseño"), DefaultValue(34)]
        public int LinkHeight
        {
            get { return linkHeight; }
            set
            {
                int next = Math.Max(26, Math.Min(72, value));
                if (linkHeight == next) return;
                linkHeight = next;
                MarkCustomDensity();
                RefreshPages();
            }
        }

        [Category("Diseño"), DefaultValue(6)]
        public int PageSpacing
        {
            get { return pageSpacing; }
            set
            {
                int next = Math.Max(0, Math.Min(32, value));
                if (pageSpacing == next) return;
                pageSpacing = next;
                MarkCustomDensity();
                RefreshPages();
            }
        }

        [Category("Diseño"), DefaultValue(typeof(Padding), "6, 4, 6, 6")]
        public Padding PagePadding
        {
            get { return pagePadding; }
            set
            {
                Padding next = new Padding(Math.Max(0, value.Left), Math.Max(0, value.Top),
                    Math.Max(0, value.Right), Math.Max(0, value.Bottom));
                if (pagePadding == next) return;
                pagePadding = next;
                MarkCustomDensity();
                RefreshPages();
            }
        }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public ZarpaTopicLink SelectedLink { get { return selectedLink; } }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public ZarpaTopicPage SelectedPage { get { return selectedPage; } }

        public event EventHandler SelectedLinkChanged;
        public event EventHandler<ZarpaTopicLinkEventArgs> LinkClicked;
        public event EventHandler<ZarpaTopicPageEventArgs> PageCollapsedChanged;

        public void ApplyTheme(ZarpaThemeTokens value)
        {
            if (value == null) return;
            theme = value;
            ApplyCurrentTheme();
        }

        public ZarpaTopicPage FindPageByTag(object tag)
        {
            foreach (ZarpaTopicPage page in pages)
                if (object.Equals(page.Tag, tag)) return page;
            return null;
        }

        public ZarpaTopicLink FindLinkByKey(string key)
        {
            foreach (ZarpaTopicPage page in pages)
                foreach (ZarpaTopicLink link in page.Links)
                    if (string.Equals(link.Key, key, StringComparison.Ordinal)) return link;
            return null;
        }

        public void SelectLink(ZarpaTopicLink link)
        {
            if (link != null && (link.Owner != this || link.Kind != ZarpaTopicLinkKind.Link))
                throw new ArgumentException("El enlace no pertenece a este ZarpaTopicBar.", "link");
            SetSelectedLink(link);
        }

        public void ExpandAll()
        {
            bool first = true;
            foreach (ZarpaTopicPage page in pages)
            {
                if (!page.Visible) continue;
                page.Collapsed = !allowMultipleExpanded && !first;
                first = false;
            }
        }

        public void CollapseAll()
        {
            foreach (ZarpaTopicPage page in pages) page.Collapsed = true;
        }

        public void TogglePage(ZarpaTopicPage page)
        {
            if (page == null) throw new ArgumentNullException("page");
            if (page.Owner != this) throw new ArgumentException("La página no pertenece a este ZarpaTopicBar.", "page");
            if (page.Enabled) page.Collapsed = !page.Collapsed;
        }

        public void PerformLinkClick(ZarpaTopicLink link)
        {
            if (link == null) throw new ArgumentNullException("link");
            if (link.Owner != this) throw new ArgumentException("El enlace no pertenece a este ZarpaTopicBar.", "link");
            ActivateLink(link);
        }

        internal bool IsDesignerHosted
        {
            get { return Site != null && Site.DesignMode; }
        }

        internal object DesignSelectedObject
        {
            get { return designSelectedObject; }
            set
            {
                if (ReferenceEquals(designSelectedObject, value)) return;
                designSelectedObject = value;
                layoutValid = false;
                Invalidate();
            }
        }

        internal void RefreshPages()
        {
            SynchronizeState();
            layoutValid = false;
            PerformLayout();
            Invalidate();
            if (IsHandleCreated && !SuppressAccessibilityInterop)
                AccessibilityNotifyClients(AccessibleEvents.Reorder, 0);
        }

        internal void PageCollapsedStateChanged(ZarpaTopicPage page)
        {
            if (!enforcingExpansion && !page.Collapsed && !allowMultipleExpanded)
            {
                enforcingExpansion = true;
                try
                {
                    foreach (ZarpaTopicPage other in pages)
                        if (other != page && !other.Collapsed) other.Collapsed = true;
                }
                finally { enforcingExpansion = false; }
            }
            StartAnimation();
            if (PageCollapsedChanged != null)
                PageCollapsedChanged(this, new ZarpaTopicPageEventArgs(page));
            if (IsHandleCreated && !SuppressAccessibilityInterop)
                AccessibilityNotifyClients(AccessibleEvents.StateChange, VisiblePageIndex(page) + 1);
        }

        internal object HitTestDesignElement(Point clientPoint)
        {
            EnsureLayout();
            return HitTest(ToContentPoint(clientPoint), true);
        }

        internal bool HitTestDesignAddPage(Point clientPoint)
        {
            EnsureLayout();
            return addPageBounds.Contains(ToContentPoint(clientPoint));
        }

        internal ZarpaTopicPage HitTestDesignAddLink(Point clientPoint)
        {
            EnsureLayout();
            Point point = ToContentPoint(clientPoint);
            foreach (KeyValuePair<ZarpaTopicPage, Rectangle> pair in addLinkBounds)
                if (pair.Value.Contains(point)) return pair.Key;
            return null;
        }

        internal bool SuppressAccessibilityInterop { get; set; }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(CanvasColor);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            EnsureLayout();
            using (Font headingFont = new Font(Font, FontStyle.Bold))
            using (Font detailFont = new Font(Font.FontFamily, Math.Max(7F, Font.Size - 1F), FontStyle.Regular))
            {
                foreach (ZarpaTopicPage page in pages)
                    if (page.Visible) PaintPage(e.Graphics, page, headingFont, detailFont);
                if (IsDesignerHosted) PaintDesignerAffordances(e.Graphics);
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            layoutValid = false;
            Invalidate();
        }

        protected override void OnScroll(ScrollEventArgs se)
        {
            base.OnScroll(se);
            ResetPointerAfterScroll();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            scrollBar.ScrollByWheel(e.Delta);
        }

        private void ScrollBarValueChanged(object sender, EventArgs e)
        {
            ResetPointerAfterScroll();
        }

        private void ResetPointerAfterScroll()
        {
            hotObject = null;
            pressedObject = null;
            Cursor = Cursors.Default;
            UpdateToolTip(null);
            Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            if (updatingLayout) return;
            layoutValid = false;
            EnsureLayout();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDpiScale(ZarpaDpiScale.FromControl(this));
            SnapAnimations();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            EnsureLayout();
            object next = HitTest(ToContentPoint(e.Location), false);
            if (!ReferenceEquals(hotObject, next))
            {
                hotObject = next;
                UpdateToolTip(next);
                StartAnimation();
            }
            Cursor = IsActionable(next) ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hotObject = null;
            pressedObject = null;
            Cursor = Cursors.Default;
            UpdateToolTip(null);
            StartAnimation();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            object hit = HitTest(ToContentPoint(e.Location), false);
            if (IsActionable(hit))
            {
                pressedObject = hit;
                keyboardObject = hit;
                InvalidateObject(hit);
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            object pressed = pressedObject;
            pressedObject = null;
            if (pressed != null) InvalidateObject(pressed);
            if (e.Button != MouseButtons.Left || IsDesignerHosted || pressed == null) return;
            object hit = HitTest(ToContentPoint(e.Location), false);
            if (!ReferenceEquals(pressed, hit)) return;
            ZarpaTopicPage page = hit as ZarpaTopicPage;
            ZarpaTopicLink link = hit as ZarpaTopicLink;
            if (page != null) TogglePage(page);
            else if (link != null) ActivateLink(link);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Up || key == Keys.Down || key == Keys.Left || key == Keys.Right ||
                key == Keys.Home || key == Keys.End || base.IsInputKey(keyData);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            if (!IsKeyboardTarget(keyboardObject))
            {
                List<object> targets = GetKeyboardTargets();
                keyboardObject = selectedLink != null && targets.Contains(selectedLink) ? (object)selectedLink :
                    targets.Count == 0 ? null : targets[0];
            }
            EnsureKeyboardVisible();
            Invalidate();
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            pressedObject = null;
            Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            List<object> targets = GetKeyboardTargets();
            if (targets.Count == 0) return;
            int index = targets.IndexOf(keyboardObject);
            if (e.KeyCode == Keys.Down) index = Math.Min(targets.Count - 1, Math.Max(-1, index) + 1);
            else if (e.KeyCode == Keys.Up) index = Math.Max(0, index < 0 ? targets.Count - 1 : index - 1);
            else if (e.KeyCode == Keys.Home) index = 0;
            else if (e.KeyCode == Keys.End) index = targets.Count - 1;
            else if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right)
            {
                ZarpaTopicPage page = keyboardObject as ZarpaTopicPage;
                ZarpaTopicLink link = keyboardObject as ZarpaTopicLink;
                if (link != null) page = link.OwnerPage;
                if (page != null)
                {
                    bool collapse = e.KeyCode == (RightToLeft == RightToLeft.Yes ? Keys.Right : Keys.Left);
                    page.Collapsed = collapse;
                }
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
            {
                ZarpaTopicPage page = keyboardObject as ZarpaTopicPage;
                ZarpaTopicLink link = keyboardObject as ZarpaTopicLink;
                if (page != null) TogglePage(page);
                else if (link != null) ActivateLink(link);
                e.Handled = true;
            }
            else return;

            if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Down || e.KeyCode == Keys.Home || e.KeyCode == Keys.End)
            {
                keyboardObject = targets[index];
                e.Handled = true;
            }
            if (e.Handled)
            {
                e.SuppressKeyPress = true;
                EnsureKeyboardVisible();
                Invalidate();
            }
        }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new TopicBarAccessibleObject(this);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                animator.Dispose();
                toolTip.Dispose();
                detailBadgeFont.Dispose();
                if (themeFont != null) themeFont.Dispose();
            }
            base.Dispose(disposing);
        }

        internal void ApplyDpiForTest(int dpi)
        {
            ApplyDpiScale(new ZarpaDpiScale(dpi, dpi));
        }

        private void ApplyDpiScale(ZarpaDpiScale value)
        {
            if (value == null || value.DpiX == dpiScale.DpiX && value.DpiY == dpiScale.DpiY) return;
            dpiScale = value;
            scrollBar.Width = SX(ZarpaDensityMetrics.Select(theme, 7, 8, 9, 11));
            layoutValid = false;
            PerformLayout();
            Invalidate();
        }

        private void ApplyCurrentTheme()
        {
            if (!ShouldAnimate) SnapAnimations();
            BackColor = theme.Canvas;
            ForeColor = theme.Text;
            scrollBar.ApplyTheme(theme);
            if (density == ZarpaTopicBarDensity.Theme) ApplyDensity(density);
            Font nextFont = new Font(theme.FontFamily, theme.FontSize);
            Font previousFont = themeFont;
            themeFont = nextFont;
            Font = nextFont;
            if (previousFont != null) previousFont.Dispose();
            layoutValid = false;
            PerformLayout();
            Invalidate();
        }

        private void ApplyDensity(ZarpaTopicBarDensity value)
        {
            if (value == ZarpaTopicBarDensity.Theme) value = MapThemeDensity();
            effectiveDensity = value;
            switch (value)
            {
                case ZarpaTopicBarDensity.UltraCompact:
                    headerHeight = 34;
                    linkHeight = 28;
                    pageSpacing = 4;
                    pagePadding = new Padding(4, 2, 4, 4);
                    break;
                case ZarpaTopicBarDensity.Comfortable:
                    headerHeight = 44;
                    linkHeight = 38;
                    pageSpacing = 8;
                    pagePadding = new Padding(8, 6, 8, 8);
                    break;
                case ZarpaTopicBarDensity.Spacious:
                    headerHeight = 48;
                    linkHeight = 44;
                    pageSpacing = 10;
                    pagePadding = new Padding(10, 8, 10, 10);
                    break;
                default:
                    headerHeight = 40;
                    linkHeight = 34;
                    pageSpacing = 6;
                    pagePadding = new Padding(6, 4, 6, 6);
                    break;
            }
        }

        private ZarpaTopicBarDensity MapThemeDensity()
        {
            switch (theme.Density)
            {
                case ZarpaDensity.UltraCompact: return ZarpaTopicBarDensity.UltraCompact;
                case ZarpaDensity.Comfortable: return ZarpaTopicBarDensity.Comfortable;
                case ZarpaDensity.Spacious: return ZarpaTopicBarDensity.Spacious;
                case ZarpaDensity.Custom:
                    if (theme.ControlHeight <= 28) return ZarpaTopicBarDensity.UltraCompact;
                    if (theme.ControlHeight >= 38) return ZarpaTopicBarDensity.Spacious;
                    return theme.ControlHeight >= 34 ? ZarpaTopicBarDensity.Comfortable : ZarpaTopicBarDensity.Compact;
                default: return ZarpaTopicBarDensity.Compact;
            }
        }

        private void MarkCustomDensity()
        {
            if (density != ZarpaTopicBarDensity.Custom)
                density = ZarpaTopicBarDensity.Custom;
            effectiveDensity = ZarpaTopicBarDensity.Custom;
        }

        private bool ShouldSerializeHeaderHeight()
        {
            return density == ZarpaTopicBarDensity.Custom && headerHeight != 40;
        }

        private bool ShouldSerializeLinkHeight()
        {
            return density == ZarpaTopicBarDensity.Custom && linkHeight != 34;
        }

        private bool ShouldSerializePageSpacing()
        {
            return density == ZarpaTopicBarDensity.Custom && pageSpacing != 6;
        }

        private bool ShouldSerializePagePadding()
        {
            return density == ZarpaTopicBarDensity.Custom && pagePadding != new Padding(6, 4, 6, 6);
        }

        private bool ShouldAnimate
        {
            get { return theme.MotionEnabled && !IsDesignerHosted; }
        }

        private void KeepOnlyFirstExpanded()
        {
            bool found = false;
            enforcingExpansion = true;
            try
            {
                foreach (ZarpaTopicPage page in pages)
                {
                    if (page.Collapsed) continue;
                    if (!found) found = true;
                    else page.Collapsed = true;
                }
            }
            finally { enforcingExpansion = false; }
        }

        private void SynchronizeState()
        {
            List<ZarpaTopicPage> stalePages = new List<ZarpaTopicPage>();
            foreach (ZarpaTopicPage page in expansionProgress.Keys)
                if (!pages.Contains(page)) stalePages.Add(page);
            foreach (ZarpaTopicPage page in stalePages) expansionProgress.Remove(page);
            foreach (ZarpaTopicPage page in pages)
                if (!expansionProgress.ContainsKey(page)) expansionProgress.Add(page, page.Collapsed ? 0F : 1F);

            if (selectedLink != null && selectedLink.Owner != this) SetSelectedLink(null);
            if (hotObject != null && !ContainsObject(hotObject)) hotObject = null;
            if (pressedObject != null && !ContainsObject(pressedObject)) pressedObject = null;
            if (keyboardObject != null && !ContainsObject(keyboardObject)) keyboardObject = null;
            if (designSelectedObject != null && !ContainsObject(designSelectedObject)) designSelectedObject = null;
        }

        private bool ContainsObject(object value)
        {
            ZarpaTopicPage page = value as ZarpaTopicPage;
            if (page != null) return pages.Contains(page);
            ZarpaTopicLink link = value as ZarpaTopicLink;
            return link != null && link.Owner == this;
        }

        private void StartAnimation()
        {
            if (!ShouldAnimate)
            {
                SnapAnimations();
                return;
            }
            animator.Update(true);
        }

        private void SnapAnimations()
        {
            animator.Stop();
            foreach (ZarpaTopicPage page in pages) expansionProgress[page] = page.Collapsed ? 0F : 1F;
            hoverProgress.Clear();
            if (hotObject != null) hoverProgress[hotObject] = 1F;
            layoutValid = false;
            Invalidate();
        }

        private void AdvanceAnimation(float elapsed)
        {
            bool running = false;
            float expansionStep = elapsed * 1000F / Math.Max(120, theme.TabDuration);
            float hoverStep = elapsed * 1000F / Math.Max(60, theme.HoverDuration);
            foreach (ZarpaTopicPage page in pages)
            {
                float current;
                if (!expansionProgress.TryGetValue(page, out current)) current = page.Collapsed ? 0F : 1F;
                float target = page.Collapsed ? 0F : 1F;
                float next = MoveTowards(current, target, expansionStep);
                expansionProgress[page] = next;
                if (Math.Abs(next - target) > 0.001F) running = true;
            }

            List<object> objects = GetVisualObjects();
            List<object> stale = new List<object>();
            foreach (object value in hoverProgress.Keys)
                if (!objects.Contains(value)) stale.Add(value);
            foreach (object value in stale) hoverProgress.Remove(value);
            foreach (object value in objects)
            {
                float current;
                if (!hoverProgress.TryGetValue(value, out current)) current = 0F;
                float target = ReferenceEquals(value, hotObject) ? 1F : 0F;
                float next = MoveTowards(current, target, hoverStep);
                if (next <= 0F && target <= 0F) hoverProgress.Remove(value);
                else hoverProgress[value] = next;
                if (Math.Abs(next - target) > 0.001F) running = true;
            }
            layoutValid = false;
            Invalidate();
            if (!running) animator.Stop();
        }

        private static float MoveTowards(float current, float target, float step)
        {
            if (current < target) return Math.Min(target, current + step);
            if (current > target) return Math.Max(target, current - step);
            return current;
        }

        private void EnsureLayout()
        {
            if (layoutValid || updatingLayout) return;
            updatingLayout = true;
            try
            {
                addLinkBounds.Clear();
                int outer = SX(GetOuterSpacing());
                int availableWidth = Math.Max(SX(120), ClientSize.Width - scrollBar.Width - outer * 2);
                int y = SY(theme.SpacingMedium);
                ZarpaTopicPage addLinkPage = DesignAddLinkPage;
                foreach (ZarpaTopicPage page in pages)
                {
                    page.Bounds = page.HeaderBounds = page.ContentBounds = Rectangle.Empty;
                    foreach (ZarpaTopicLink link in page.Links) link.Bounds = Rectangle.Empty;
                    if (!page.Visible) continue;

                    Rectangle header = new Rectangle(outer, y, availableWidth, SY(headerHeight));
                    float progress = GetExpansionProgress(page);
                    int fullContentHeight = MeasurePageContent(page, IsDesignerHosted && page == addLinkPage);
                    int visibleContentHeight = (int)Math.Round(fullContentHeight * progress);
                    page.HeaderBounds = header;
                    page.ContentBounds = new Rectangle(outer, header.Bottom, availableWidth, visibleContentHeight);
                    page.Bounds = new Rectangle(outer, y, availableWidth, header.Height + visibleContentHeight);

                    int linkY = header.Bottom + SY(pagePadding.Top);
                    int linkLeft = outer + SX(pagePadding.Left);
                    int linkWidth = Math.Max(1, availableWidth - SX(pagePadding.Left + pagePadding.Right));
                    foreach (ZarpaTopicLink link in page.Links)
                    {
                        if (!link.Visible) continue;
                        int height = GetLinkHeight(page, link);
                        link.Bounds = new Rectangle(linkLeft, linkY, linkWidth, height);
                        linkY += height + SY(GetLinkSpacing());
                    }
                    if (IsDesignerHosted && page == addLinkPage)
                    {
                        Rectangle add = new Rectangle(linkLeft, linkY + SY(GetLinkSpacing()), linkWidth,
                            SY(GetDesignerAddHeight()));
                        addLinkBounds[page] = add;
                    }
                    y = page.Bounds.Bottom + SY(pageSpacing);
                }
                if (IsDesignerHosted)
                {
                    addPageBounds = new Rectangle(outer, y, availableWidth, SY(32));
                    y = addPageBounds.Bottom + outer;
                }
                else addPageBounds = Rectangle.Empty;
                contentHeight = Math.Max(0, y);
                scrollBar.SetRange(contentHeight, Math.Max(1, ClientSize.Height));
                scrollBar.Enabled = contentHeight > ClientSize.Height;
                if (!scrollBar.Enabled) scrollBar.Value = 0;
                scrollBar.BringToFront();
                layoutValid = true;
            }
            finally { updatingLayout = false; }
        }

        private int MeasurePageContent(ZarpaTopicPage page, bool includeDesignerAdd)
        {
            int height = SY(pagePadding.Top + pagePadding.Bottom);
            foreach (ZarpaTopicLink link in page.Links)
                if (link.Visible) height += GetLinkHeight(page, link) + SY(GetLinkSpacing());
            if (includeDesignerAdd) height += SY(GetDesignerAddHeight() + GetLinkSpacing() * 2);
            return Math.Max(0, height);
        }

        private int GetLinkHeight(ZarpaTopicPage page, ZarpaTopicLink link)
        {
            if (link.Kind == ZarpaTopicLinkKind.Separator)
                return SY(effectiveDensity == ZarpaTopicBarDensity.UltraCompact ? 10 :
                    effectiveDensity == ZarpaTopicBarDensity.Spacious ? 18 : 14);
            int logical = linkHeight;
            if (!string.IsNullOrEmpty(link.Description) || page.WrapLinkText)
                logical = Math.Max(logical, linkHeight + 14);
            return SY(logical);
        }

        private int GetOuterSpacing()
        {
            return effectiveDensity == ZarpaTopicBarDensity.UltraCompact ? 4 :
                effectiveDensity == ZarpaTopicBarDensity.Spacious ? Math.Max(10, theme.SpacingMedium) :
                effectiveDensity == ZarpaTopicBarDensity.Comfortable ? 8 : 6;
        }

        private int GetLinkSpacing()
        {
            return effectiveDensity == ZarpaTopicBarDensity.UltraCompact ? 1 :
                effectiveDensity == ZarpaTopicBarDensity.Spacious ? 3 : 2;
        }

        private int GetDesignerAddHeight()
        {
            return effectiveDensity == ZarpaTopicBarDensity.UltraCompact ? 22 :
                effectiveDensity == ZarpaTopicBarDensity.Spacious ? 30 :
                effectiveDensity == ZarpaTopicBarDensity.Comfortable ? 28 : 26;
        }

        private float GetExpansionProgress(ZarpaTopicPage page)
        {
            float value;
            if (!expansionProgress.TryGetValue(page, out value))
            {
                value = page.Collapsed ? 0F : 1F;
                expansionProgress[page] = value;
            }
            return value;
        }

        private float GetHoverProgress(object value)
        {
            float progress;
            return value != null && hoverProgress.TryGetValue(value, out progress) ? progress : 0F;
        }

        private void PaintPage(Graphics graphics, ZarpaTopicPage page, Font headingFont, Font detailFont)
        {
            Rectangle pageBounds = ToClientRectangle(page.Bounds);
            Rectangle contentBounds = ToClientRectangle(page.ContentBounds);
            if (pageBounds.Width <= 0 || pageBounds.Height <= 0 ||
                !pageBounds.IntersectsWith(ClientRectangle)) return;
            int radius = SX(theme.GroupCornerRadius);
            ZarpaPaint.FillRounded(graphics, SurfaceColor, pageBounds, radius);
            ZarpaPaint.DrawRounded(graphics, BorderColor, pageBounds, radius,
                dpiScale.Stroke(theme.BorderThickness));
            PaintPageHeader(graphics, page, ToClientRectangle(page.HeaderBounds), headingFont);

            if (contentBounds.Height > 0)
            {
                GraphicsState state = graphics.Save();
                graphics.SetClip(contentBounds, CombineMode.Intersect);
                using (Pen separator = new Pen(BorderColor, dpiScale.Stroke(1)))
                    graphics.DrawLine(separator, contentBounds.Left + SX(pagePadding.Left),
                        contentBounds.Top, contentBounds.Right - SX(pagePadding.Right), contentBounds.Top);
                foreach (ZarpaTopicLink link in page.Links)
                    if (link.Visible && page.ContentBounds.Contains(link.Bounds))
                        PaintLink(graphics, page, link, ToClientRectangle(link.Bounds), detailFont);
                graphics.Restore(state);
            }
            DrawDesignSelection(graphics, page, pageBounds);
        }

        private void PaintPageHeader(Graphics graphics, ZarpaTopicPage page, Rectangle bounds, Font headingFont)
        {
            float hover = GetHoverProgress(page);
            Color baseColor = page.Emphasized ? SelectionColor : SurfaceColor;
            Color hoverColor = page.Emphasized ? ZarpaPaint.Blend(SelectionColor, AccentColor, 0.10F) : RaisedColor;
            Color fill = ZarpaPaint.Blend(baseColor, hoverColor, hover);
            if (ReferenceEquals(pressedObject, page)) fill = OverlayColor;
            ZarpaPaint.FillRounded(graphics, fill, bounds, SX(theme.GroupCornerRadius));

            bool rtl = RightToLeft == RightToLeft.Yes;
            int inset = SX(effectiveDensity == ZarpaTopicBarDensity.UltraCompact ? 8 :
                effectiveDensity == ZarpaTopicBarDensity.Spacious ? 14 : 10);
            int iconSize = SX(effectiveDensity == ZarpaTopicBarDensity.UltraCompact ? 18 :
                effectiveDensity == ZarpaTopicBarDensity.Spacious ? Math.Max(22, theme.IconSize) : 19);
            int chevronSize = SX(effectiveDensity == ZarpaTopicBarDensity.UltraCompact ? 18 :
                effectiveDensity == ZarpaTopicBarDensity.Spacious ? 24 : 20);
            Rectangle chevron = new Rectangle(rtl ? bounds.Left + inset : bounds.Right - inset - chevronSize,
                bounds.Top + (bounds.Height - chevronSize) / 2, chevronSize, chevronSize);
            int leading = rtl ? bounds.Right - inset : bounds.Left + inset;
            Image image = ResolvePageImage(page);
            bool hasIcon = image != null || !string.IsNullOrEmpty(page.IconKey);
            Rectangle iconBounds = rtl
                ? new Rectangle(leading - iconSize, bounds.Top + (bounds.Height - iconSize) / 2, iconSize, iconSize)
                : new Rectangle(leading, bounds.Top + (bounds.Height - iconSize) / 2, iconSize, iconSize);
            if (hasIcon)
            {
                int tileInset = effectiveDensity == ZarpaTopicBarDensity.UltraCompact ? 3 :
                    effectiveDensity == ZarpaTopicBarDensity.Spacious ? 5 : 4;
                Rectangle tile = Rectangle.Inflate(iconBounds, SX(tileInset), SY(tileInset));
                ZarpaPaint.FillRounded(graphics, page.Emphasized ? SurfaceColor : RaisedColor, tile,
                    SX(theme.CornerRadius));
                DrawImageOrIcon(graphics, image, page.IconKey, iconBounds,
                    page.Enabled ? AccentColor : MutedColor);
            }

            int textStart = hasIcon ? (rtl ? iconBounds.Left - SX(10) : iconBounds.Right + SX(10)) : leading;
            int textEnd = rtl ? chevron.Right + SX(8) : chevron.Left - SX(8);
            Rectangle textBounds = rtl
                ? Rectangle.FromLTRB(textEnd, bounds.Top, textStart, bounds.Bottom)
                : Rectangle.FromLTRB(textStart, bounds.Top, textEnd, bounds.Bottom);

            if (!string.IsNullOrEmpty(page.BadgeText))
            {
                Size measured = TextRenderer.MeasureText(page.BadgeText, detailBadgeFont);
                int badgeWidth = Math.Min(Math.Max(SX(22), measured.Width + SX(10)), Math.Max(SX(22), textBounds.Width / 2));
                Rectangle badge = rtl
                    ? new Rectangle(textBounds.Left, bounds.Top + (bounds.Height - SY(22)) / 2, badgeWidth, SY(22))
                    : new Rectangle(textBounds.Right - badgeWidth, bounds.Top + (bounds.Height - SY(22)) / 2, badgeWidth, SY(22));
                ZarpaPaint.FillRounded(graphics, AccentColor, badge, SX(Math.Min(11, theme.CornerRadius)));
                TextRenderer.DrawText(graphics, page.BadgeText, detailBadgeFont, badge, AccentTextColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                if (rtl) textBounds.X = badge.Right + SX(8);
                else textBounds.Width = Math.Max(1, badge.Left - SX(8) - textBounds.Left);
            }

            Color headerTextColor = SystemInformation.HighContrast && page.Emphasized
                ? SystemColors.HighlightText : page.Enabled ? TextColor : MutedColor;
            TextRenderer.DrawText(graphics, page.Text, headingFont, textBounds, headerTextColor,
                (rtl ? TextFormatFlags.Right : TextFormatFlags.Left) |
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            DrawChevron(graphics, chevron, GetExpansionProgress(page), page.Enabled ? TextColor : MutedColor, rtl);
            if (Focused && ReferenceEquals(keyboardObject, page))
                ZarpaPaint.DrawRounded(graphics, AccentColor, Rectangle.Inflate(bounds, -SX(3), -SY(3)),
                    SX(Math.Max(2, theme.CornerRadius - 2)), dpiScale.Stroke(1));
        }

        private readonly Font detailBadgeFont = new Font("Segoe UI", 8F, FontStyle.Bold);

        private void PaintLink(Graphics graphics, ZarpaTopicPage page, ZarpaTopicLink link,
            Rectangle bounds, Font detailFont)
        {
            if (link.Kind == ZarpaTopicLinkKind.Separator)
            {
                using (Pen pen = new Pen(BorderColor, dpiScale.Stroke(1)))
                    graphics.DrawLine(pen, bounds.Left + SX(8), bounds.Top + bounds.Height / 2,
                        bounds.Right - SX(8), bounds.Top + bounds.Height / 2);
                DrawDesignSelection(graphics, link, bounds);
                return;
            }

            float hover = GetHoverProgress(link);
            Color fill = ZarpaPaint.Blend(SurfaceColor, RaisedColor, hover);
            if (ReferenceEquals(selectedLink, link)) fill = SelectionColor;
            if (ReferenceEquals(pressedObject, link)) fill = OverlayColor;
            if (hover > 0F || ReferenceEquals(selectedLink, link) || ReferenceEquals(pressedObject, link))
                ZarpaPaint.FillRounded(graphics, fill, bounds, SX(theme.CornerRadius));
            if (ReferenceEquals(selectedLink, link))
            {
                Rectangle marker = RightToLeft == RightToLeft.Yes
                    ? new Rectangle(bounds.Right - SX(3), bounds.Top + SY(8), SX(3), bounds.Height - SY(16))
                    : new Rectangle(bounds.Left, bounds.Top + SY(8), SX(3), bounds.Height - SY(16));
                ZarpaPaint.FillRounded(graphics, AccentColor, marker, SX(2));
            }

            bool rtl = RightToLeft == RightToLeft.Yes;
            int inset = SX(effectiveDensity == ZarpaTopicBarDensity.UltraCompact ? 8 :
                effectiveDensity == ZarpaTopicBarDensity.Spacious ? 12 : 10);
            int iconSize = SX(effectiveDensity == ZarpaTopicBarDensity.UltraCompact ? 18 :
                effectiveDensity == ZarpaTopicBarDensity.Spacious ? Math.Min(22, theme.IconSize) : 18);
            int leading = rtl ? bounds.Right - inset : bounds.Left + inset;
            Image image = ResolveLinkImage(link);
            bool hasIcon = image != null || !string.IsNullOrEmpty(link.IconKey);
            Rectangle icon = rtl
                ? new Rectangle(leading - iconSize, bounds.Top + (bounds.Height - iconSize) / 2, iconSize, iconSize)
                : new Rectangle(leading, bounds.Top + (bounds.Height - iconSize) / 2, iconSize, iconSize);
            bool selected = ReferenceEquals(selectedLink, link);
            Color iconColor = !link.Enabled || !page.Enabled ? BorderStrongColor :
                SystemInformation.HighContrast && selected ? SystemColors.HighlightText :
                selected ? AccentColor : MutedColor;
            if (hasIcon) DrawImageOrIcon(graphics, image, link.IconKey, icon, iconColor);

            int textLeading = hasIcon ? (rtl ? icon.Left - SX(10) : icon.Right + SX(10)) : leading;
            int trailing = rtl ? bounds.Left + inset : bounds.Right - inset;
            Rectangle text = rtl
                ? Rectangle.FromLTRB(trailing, bounds.Top + SY(4), textLeading, bounds.Bottom - SY(4))
                : Rectangle.FromLTRB(textLeading, bounds.Top + SY(4), trailing, bounds.Bottom - SY(4));
            if (!string.IsNullOrEmpty(link.BadgeText))
            {
                Size measured = TextRenderer.MeasureText(link.BadgeText, detailBadgeFont);
                int width = Math.Min(measured.Width + SX(9), Math.Max(SX(24), text.Width / 2));
                Rectangle badge = rtl
                    ? new Rectangle(text.Left, bounds.Top + (bounds.Height - SY(20)) / 2, width, SY(20))
                    : new Rectangle(text.Right - width, bounds.Top + (bounds.Height - SY(20)) / 2, width, SY(20));
                ZarpaPaint.FillRounded(graphics, AccentColor, badge, SX(Math.Min(10, theme.CornerRadius)));
                TextRenderer.DrawText(graphics, link.BadgeText, detailBadgeFont, badge, AccentTextColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                if (rtl) text.X = badge.Right + SX(8);
                else text.Width = Math.Max(1, badge.Left - SX(8) - text.Left);
            }

            Color linkText = !link.Enabled || !page.Enabled ? MutedColor :
                SystemInformation.HighContrast && selected ? SystemColors.HighlightText : TextColor;
            TextFormatFlags horizontal = page.LinkAlignment == HorizontalAlignment.Center ? TextFormatFlags.HorizontalCenter :
                page.LinkAlignment == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left;
            if (rtl && page.LinkAlignment == HorizontalAlignment.Left) horizontal = TextFormatFlags.Right;
            else if (rtl && page.LinkAlignment == HorizontalAlignment.Right) horizontal = TextFormatFlags.Left;
            if (string.IsNullOrEmpty(link.Description))
                TextRenderer.DrawText(graphics, link.Text, Font, text, linkText,
                    horizontal | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                    (page.WrapLinkText ? TextFormatFlags.WordBreak : TextFormatFlags.SingleLine));
            else
            {
                Rectangle title = new Rectangle(text.X, text.Top, text.Width, Math.Max(SY(19), text.Height / 2));
                Rectangle description = new Rectangle(text.X, title.Bottom - SY(1), text.Width,
                    Math.Max(SY(18), text.Bottom - title.Bottom));
                TextRenderer.DrawText(graphics, link.Text, Font, title, linkText,
                    horizontal | TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(graphics, link.Description, detailFont, description, MutedColor,
                    horizontal | TextFormatFlags.Top | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }
            if (Focused && ReferenceEquals(keyboardObject, link))
                ZarpaPaint.DrawRounded(graphics, AccentColor, Rectangle.Inflate(bounds, -SX(2), -SY(2)),
                    SX(Math.Max(2, theme.CornerRadius - 1)), dpiScale.Stroke(1));
            DrawDesignSelection(graphics, link, bounds);
        }

        private void PaintDesignerAffordances(Graphics graphics)
        {
            foreach (KeyValuePair<ZarpaTopicPage, Rectangle> pair in addLinkBounds)
                DrawAddAffordance(graphics, ToClientRectangle(pair.Value), "+  Añadir enlace");
            if (!addPageBounds.IsEmpty)
                DrawAddAffordance(graphics, ToClientRectangle(addPageBounds), "+  Añadir página");
        }

        private void DrawAddAffordance(Graphics graphics, Rectangle bounds, string text)
        {
            using (Pen pen = new Pen(AccentColor, dpiScale.Stroke(1)))
            {
                pen.DashStyle = DashStyle.Dash;
                graphics.DrawRectangle(pen, bounds.Left, bounds.Top, Math.Max(1, bounds.Width - 1),
                    Math.Max(1, bounds.Height - 1));
            }
            TextRenderer.DrawText(graphics, text, Font, bounds, AccentColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        private void DrawDesignSelection(Graphics graphics, object value, Rectangle bounds)
        {
            if (!IsDesignerHosted || !ReferenceEquals(designSelectedObject, value) || bounds.IsEmpty) return;
            using (Pen pen = new Pen(AccentColor, dpiScale.Stroke(2)))
            {
                pen.DashStyle = DashStyle.Dash;
                Rectangle selected = Rectangle.Inflate(bounds, -SX(2), -SY(2));
                graphics.DrawRectangle(pen, selected.Left, selected.Top,
                    Math.Max(1, selected.Width - 1), Math.Max(1, selected.Height - 1));
            }
        }

        private void DrawChevron(Graphics graphics, Rectangle bounds, float progress, Color color, bool rtl)
        {
            GraphicsState state = graphics.Save();
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            float direction = rtl ? -1F : 1F;
            graphics.TranslateTransform(bounds.Left + bounds.Width / 2F, bounds.Top + bounds.Height / 2F);
            graphics.RotateTransform((rtl ? 180F : 0F) + direction * 90F * progress);
            using (Pen pen = new Pen(color, Math.Max(1.5F, dpiScale.X(1.6F))))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                graphics.DrawLines(pen, new[] { new PointF(-3F, -5F), new PointF(2F, 0F), new PointF(-3F, 5F) });
            }
            graphics.Restore(state);
        }

        private void DrawImageOrIcon(Graphics graphics, Image image, string iconKey, Rectangle bounds, Color color)
        {
            if (image != null)
            {
                GraphicsState state = graphics.Save();
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(image, FitImage(image.Size, bounds));
                graphics.Restore(state);
            }
            else if (!string.IsNullOrEmpty(iconKey))
                FluentIconCatalog.TryDraw(graphics, iconKey, bounds, color, dpiScale.X(Math.Max(16F, bounds.Width - 2F)));
        }

        private static Rectangle FitImage(Size imageSize, Rectangle bounds)
        {
            if (imageSize.Width <= 0 || imageSize.Height <= 0) return bounds;
            float scale = Math.Min(bounds.Width / (float)imageSize.Width, bounds.Height / (float)imageSize.Height);
            Size size = new Size(Math.Max(1, (int)Math.Round(imageSize.Width * scale)),
                Math.Max(1, (int)Math.Round(imageSize.Height * scale)));
            return new Rectangle(bounds.Left + (bounds.Width - size.Width) / 2,
                bounds.Top + (bounds.Height - size.Height) / 2, size.Width, size.Height);
        }

        private Image ResolvePageImage(ZarpaTopicPage page)
        {
            if (page.Image != null) return page.Image;
            return pageImageList != null && page.ImageIndex >= 0 && page.ImageIndex < pageImageList.Images.Count
                ? pageImageList.Images[page.ImageIndex] : null;
        }

        private Image ResolveLinkImage(ZarpaTopicLink link)
        {
            if (link.Image != null) return link.Image;
            return linkImageList != null && link.ImageIndex >= 0 && link.ImageIndex < linkImageList.Images.Count
                ? linkImageList.Images[link.ImageIndex] : null;
        }

        private object HitTest(Point contentPoint, bool includeSeparators)
        {
            foreach (ZarpaTopicPage page in pages)
            {
                if (!page.Visible) continue;
                if (page.HeaderBounds.Contains(contentPoint)) return page;
                if (page.ContentBounds.Contains(contentPoint))
                    foreach (ZarpaTopicLink link in page.Links)
                        if (link.Visible && link.Bounds.Contains(contentPoint) &&
                            (includeSeparators || link.Kind == ZarpaTopicLinkKind.Link)) return link;
            }
            return null;
        }

        private Point ToContentPoint(Point clientPoint)
        {
            return new Point(clientPoint.X, clientPoint.Y + scrollBar.Value);
        }

        private Rectangle ToClientRectangle(Rectangle contentBounds)
        {
            contentBounds.Offset(0, -scrollBar.Value);
            return contentBounds;
        }

        private bool IsActionable(object value)
        {
            ZarpaTopicPage page = value as ZarpaTopicPage;
            if (page != null) return page.Enabled;
            ZarpaTopicLink link = value as ZarpaTopicLink;
            return link != null && link.Kind == ZarpaTopicLinkKind.Link && link.Enabled &&
                link.OwnerPage != null && link.OwnerPage.Enabled;
        }

        private void ActivateLink(ZarpaTopicLink link)
        {
            if (!IsActionable(link)) return;
            if (autoSelectLinks) SetSelectedLink(link);
            link.PerformClick();
            if (LinkClicked != null) LinkClicked(this, new ZarpaTopicLinkEventArgs(link.OwnerPage, link));
        }

        private void SetSelectedLink(ZarpaTopicLink value)
        {
            if (ReferenceEquals(selectedLink, value)) return;
            ZarpaTopicLink previous = selectedLink;
            selectedLink = value;
            selectedPage = value == null ? null : value.OwnerPage;
            if (SelectedLinkChanged != null) SelectedLinkChanged(this, EventArgs.Empty);
            InvalidateObject(previous);
            InvalidateObject(selectedLink);
            if (IsHandleCreated && !SuppressAccessibilityInterop)
                AccessibilityNotifyClients(AccessibleEvents.Selection, selectedPage == null ? 0 : VisiblePageIndex(selectedPage) + 1);
        }

        private void UpdateToolTip(object value)
        {
            if (ReferenceEquals(toolTipObject, value)) return;
            toolTipObject = value;
            string text = string.Empty;
            if (showToolTips)
            {
                ZarpaTopicPage page = value as ZarpaTopicPage;
                ZarpaTopicLink link = value as ZarpaTopicLink;
                if (page != null) text = page.ToolTipText;
                else if (link != null) text = link.ToolTipText;
            }
            toolTip.SetToolTip(this, text ?? string.Empty);
        }

        private void InvalidateObject(object value)
        {
            if (value == null) return;
            Rectangle bounds = value is ZarpaTopicPage ? ((ZarpaTopicPage)value).Bounds : ((ZarpaTopicLink)value).Bounds;
            bounds.Offset(0, -scrollBar.Value);
            Invalidate(Rectangle.Inflate(bounds, SX(2), SY(2)));
        }

        private List<object> GetKeyboardTargets()
        {
            List<object> result = new List<object>();
            foreach (ZarpaTopicPage page in pages)
            {
                if (!page.Visible || !page.Enabled) continue;
                result.Add(page);
                if (page.Collapsed) continue;
                foreach (ZarpaTopicLink link in page.Links)
                    if (link.Visible && link.Enabled && link.Kind == ZarpaTopicLinkKind.Link) result.Add(link);
            }
            return result;
        }

        private List<object> GetVisualObjects()
        {
            List<object> result = new List<object>();
            foreach (ZarpaTopicPage page in pages)
            {
                if (!page.Visible) continue;
                result.Add(page);
                foreach (ZarpaTopicLink link in page.Links) if (link.Visible) result.Add(link);
            }
            return result;
        }

        private bool IsKeyboardTarget(object value)
        {
            return value != null && GetKeyboardTargets().Contains(value);
        }

        private void EnsureKeyboardVisible()
        {
            EnsureLayout();
            Rectangle bounds = keyboardObject is ZarpaTopicPage ? ((ZarpaTopicPage)keyboardObject).HeaderBounds :
                keyboardObject is ZarpaTopicLink ? ((ZarpaTopicLink)keyboardObject).Bounds : Rectangle.Empty;
            if (bounds.IsEmpty) return;
            int viewportTop = scrollBar.Value;
            int viewportBottom = viewportTop + ClientSize.Height;
            if (bounds.Top < viewportTop) scrollBar.Value = bounds.Top;
            else if (bounds.Bottom > viewportBottom) scrollBar.Value = bounds.Bottom - ClientSize.Height;
        }

        private ZarpaTopicPage DesignAddLinkPage
        {
            get
            {
                ZarpaTopicPage page = designSelectedObject as ZarpaTopicPage;
                ZarpaTopicLink link = designSelectedObject as ZarpaTopicLink;
                if (page == null && link != null) page = link.OwnerPage;
                return page != null && page.Owner == this && !page.Collapsed ? page : null;
            }
        }

        private int VisiblePageIndex(ZarpaTopicPage target)
        {
            int index = 0;
            foreach (ZarpaTopicPage page in pages)
            {
                if (!page.Visible) continue;
                if (page == target) return index;
                index++;
            }
            return -1;
        }

        private ZarpaTopicPage VisiblePageAt(int index)
        {
            int current = 0;
            foreach (ZarpaTopicPage page in pages)
            {
                if (!page.Visible) continue;
                if (current == index) return page;
                current++;
            }
            return null;
        }

        private int VisiblePageCount
        {
            get { int count = 0; foreach (ZarpaTopicPage page in pages) if (page.Visible) count++; return count; }
        }

        private int SX(int value) { return dpiScale.X(value); }
        private int SY(int value) { return dpiScale.Y(value); }

        private Color CanvasColor { get { return SystemInformation.HighContrast ? SystemColors.Window : theme.Canvas; } }
        private Color SurfaceColor { get { return SystemInformation.HighContrast ? SystemColors.Control : theme.Surface; } }
        private Color RaisedColor { get { return SystemInformation.HighContrast ? SystemColors.ControlLight : theme.SurfaceRaised; } }
        private Color OverlayColor { get { return SystemInformation.HighContrast ? SystemColors.Highlight : theme.SurfaceOverlay; } }
        private Color BorderColor { get { return SystemInformation.HighContrast ? SystemColors.ControlDark : theme.Border; } }
        private Color BorderStrongColor { get { return SystemInformation.HighContrast ? SystemColors.GrayText : theme.BorderStrong; } }
        private Color TextColor { get { return SystemInformation.HighContrast ? SystemColors.ControlText : theme.Text; } }
        private Color MutedColor { get { return SystemInformation.HighContrast ? SystemColors.GrayText : theme.TextMuted; } }
        private Color AccentColor { get { return SystemInformation.HighContrast ? SystemColors.Highlight : theme.Accent; } }
        private Color AccentTextColor { get { return SystemInformation.HighContrast ? SystemColors.HighlightText : Color.White; } }
        private Color SelectionColor { get { return SystemInformation.HighContrast ? SystemColors.Highlight : theme.Selection; } }

        private sealed class TopicBarAccessibleObject : ControlAccessibleObject
        {
            private readonly ZarpaTopicBar topicBar;
            private readonly Dictionary<ZarpaTopicPage, AccessibleObject> children =
                new Dictionary<ZarpaTopicPage, AccessibleObject>();

            internal TopicBarAccessibleObject(ZarpaTopicBar owner) : base(owner) { topicBar = owner; }

            public override string Name
            {
                get { return !string.IsNullOrEmpty(topicBar.AccessibleName) ? topicBar.AccessibleName : "Temas"; }
                set { topicBar.AccessibleName = value; }
            }

            public override AccessibleRole Role { get { return topicBar.AccessibleRole; } }
            public override int GetChildCount() { return topicBar.VisiblePageCount; }
            public override AccessibleObject GetChild(int index)
            {
                ZarpaTopicPage page = topicBar.VisiblePageAt(index);
                if (page == null) return null;
                AccessibleObject child;
                if (!children.TryGetValue(page, out child))
                {
                    child = new TopicPageAccessibleObject(topicBar, page);
                    children.Add(page, child);
                }
                return child;
            }
        }

        private sealed class TopicPageAccessibleObject : AccessibleObject
        {
            private readonly ZarpaTopicBar topicBar;
            private readonly ZarpaTopicPage page;
            private readonly Dictionary<ZarpaTopicLink, AccessibleObject> children =
                new Dictionary<ZarpaTopicLink, AccessibleObject>();

            internal TopicPageAccessibleObject(ZarpaTopicBar owner, ZarpaTopicPage topicPage)
            {
                topicBar = owner;
                page = topicPage;
            }

            public override string Name { get { return page.Text; } set { page.Text = value ?? string.Empty; } }
            public override AccessibleRole Role { get { return AccessibleRole.Grouping; } }
            public override AccessibleObject Parent { get { return topicBar.AccessibilityObject; } }
            public override string DefaultAction { get { return page.Collapsed ? "Expandir" : "Contraer"; } }
            public override Rectangle Bounds
            {
                get
                {
                    if (!topicBar.IsHandleCreated || !page.Visible) return Rectangle.Empty;
                    Rectangle bounds = page.Bounds;
                    bounds.Offset(0, -topicBar.scrollBar.Value);
                    bounds = Rectangle.Intersect(bounds, topicBar.ClientRectangle);
                    return bounds.IsEmpty ? Rectangle.Empty : topicBar.RectangleToScreen(bounds);
                }
            }
            public override AccessibleStates State
            {
                get
                {
                    AccessibleStates state = page.Collapsed ? AccessibleStates.Collapsed : AccessibleStates.Expanded;
                    if (!topicBar.Enabled || !page.Enabled) state |= AccessibleStates.Unavailable;
                    if (!page.Visible) state |= AccessibleStates.Invisible;
                    if (Bounds.IsEmpty && page.Visible) state |= AccessibleStates.Offscreen;
                    if (topicBar.Focused && ReferenceEquals(topicBar.keyboardObject, page)) state |= AccessibleStates.Focused;
                    return state | AccessibleStates.Focusable;
                }
            }
            public override int GetChildCount()
            {
                if (page.Collapsed) return 0;
                int count = 0;
                foreach (ZarpaTopicLink link in page.Links)
                    if (link.Visible && link.Kind == ZarpaTopicLinkKind.Link) count++;
                return count;
            }
            public override AccessibleObject GetChild(int index)
            {
                int current = 0;
                foreach (ZarpaTopicLink link in page.Links)
                {
                    if (!link.Visible || link.Kind != ZarpaTopicLinkKind.Link) continue;
                    if (current++ != index) continue;
                    AccessibleObject child;
                    if (!children.TryGetValue(link, out child))
                    {
                        child = new TopicLinkAccessibleObject(topicBar, page, link, this);
                        children.Add(link, child);
                    }
                    return child;
                }
                return null;
            }
            public override void DoDefaultAction()
            {
                if (topicBar.Enabled && page.Enabled && page.Visible) topicBar.TogglePage(page);
            }
        }

        private sealed class TopicLinkAccessibleObject : AccessibleObject
        {
            private readonly ZarpaTopicBar topicBar;
            private readonly ZarpaTopicPage page;
            private readonly ZarpaTopicLink link;
            private readonly AccessibleObject parent;

            internal TopicLinkAccessibleObject(ZarpaTopicBar owner, ZarpaTopicPage topicPage,
                ZarpaTopicLink topicLink, AccessibleObject parentObject)
            {
                topicBar = owner;
                page = topicPage;
                link = topicLink;
                parent = parentObject;
            }

            public override string Name { get { return link.Text; } set { link.Text = value ?? string.Empty; } }
            public override string Description { get { return link.Description; } }
            public override AccessibleRole Role { get { return AccessibleRole.Link; } }
            public override AccessibleObject Parent { get { return parent; } }
            public override string DefaultAction { get { return "Abrir"; } }
            public override Rectangle Bounds
            {
                get
                {
                    if (!topicBar.IsHandleCreated || !page.Visible || page.Collapsed || !link.Visible) return Rectangle.Empty;
                    Rectangle bounds = link.Bounds;
                    bounds.Offset(0, -topicBar.scrollBar.Value);
                    bounds = Rectangle.Intersect(bounds, topicBar.ClientRectangle);
                    return bounds.IsEmpty ? Rectangle.Empty : topicBar.RectangleToScreen(bounds);
                }
            }
            public override AccessibleStates State
            {
                get
                {
                    AccessibleStates state = AccessibleStates.Focusable | AccessibleStates.Selectable;
                    if (!topicBar.Enabled || !page.Enabled || !link.Enabled) state |= AccessibleStates.Unavailable;
                    if (!page.Visible || page.Collapsed || !link.Visible) state |= AccessibleStates.Invisible;
                    if (Bounds.IsEmpty && page.Visible && !page.Collapsed && link.Visible) state |= AccessibleStates.Offscreen;
                    if (ReferenceEquals(topicBar.selectedLink, link)) state |= AccessibleStates.Selected;
                    if (topicBar.Focused && ReferenceEquals(topicBar.keyboardObject, link)) state |= AccessibleStates.Focused;
                    return state;
                }
            }
            public override void DoDefaultAction()
            {
                if (topicBar.Enabled && page.Enabled && link.Enabled && page.Visible && !page.Collapsed && link.Visible)
                    topicBar.ActivateLink(link);
            }
        }
    }
}
