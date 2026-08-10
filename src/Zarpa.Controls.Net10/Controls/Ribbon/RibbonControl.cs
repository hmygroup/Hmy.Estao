using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Design;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ZarpaSuite.Controls
{
    [DefaultEvent("SelectedTabChanged")]
    [DefaultProperty("Tabs")]
    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(ToolStrip))]
    [Designer("ZarpaSuite.Controls.Design.RibbonControlDesigner, Zarpa.Controls")]
    public class RibbonControl : Control, IZarpaThemeAware, IZarpaThemeBoundary
    {
        private const int DefaultContentHeight = 100;
        private ZarpaDpiScale dpiScale = new ZarpaDpiScale(96, 96);
        private int SX(int value) { return dpiScale.X(value); }
        private int SY(int value) { return dpiScale.Y(value); }
        private float SX(float value) { return dpiScale.X(value); }
        private int CaptionHeight { get { return SY(appearance == null ? 40 : appearance.HeaderHeight); } }
        private int TabHeight { get { return SY(appearance == null ? 38 : appearance.TabHeight); } }

        private readonly RibbonTabCollection tabs;
        private readonly ToolTip toolTip;
        private readonly System.Threading.Timer motionTimer;
        private readonly RibbonAppearance appearance;
        private readonly Dictionary<RibbonHostedItem, Control> hostedControls;
        private readonly HashSet<RibbonButton> responsiveSmallButtons;
        private readonly HashSet<RibbonGroup> responsiveCompactGroups;
        private readonly HashSet<RibbonGroup> collapsedGroups;
        private readonly HashSet<RibbonGroup> overflowGroups;
        private readonly Dictionary<RibbonItem, float> itemHoverAnimations;
        private readonly Dictionary<RibbonItem, float> itemPressAnimations;
        private readonly Dictionary<RibbonToggleButton, float> toggleAnimations;
        private readonly Dictionary<RibbonItem, float> badgeAnimations;
        private readonly Dictionary<RibbonItem, string> observedBadges;
        private readonly Dictionary<RibbonTab, float> tabHoverAnimations;
        private bool syncingHostedControls;
        private int selectedTabIndex;
        private int hotTabIndex = -1;
        private int keyboardTabIndex = -1;
        private RibbonGroup keyboardGroup;
        private RibbonItem keyboardItem;
        internal bool SuppressAccessibilityInterop { get; set; }
        private RibbonItem hotItem;
        private RibbonItem pressedItem;
        private bool hotApplicationButton;
        private bool pressedApplicationButton;
        private bool pressedOverflow;
        private RibbonGroup pressedCollapsedGroup;
        private Rectangle addTabBounds;
        private Rectangle addGroupBounds;
        private Rectangle designDeleteBounds;
        private Rectangle designResizeBounds;
        private Rectangle designMoreBounds;
        private Rectangle designMoveLeftBounds;
        private Rectangle designMoveRightBounds;
        private Rectangle designToolbarBounds;
        private RibbonGroup designGridGroup;
        private bool designGridVisible;
        private Rectangle overflowBounds;
        private bool responsiveEnabled;
        private float busyAngle;
        private RibbonItem rippleItem;
        private Point rippleOrigin;
        private float rippleProgress = 1F;
        private float applicationHoverProgress;
        private float applicationPressProgress;
        private float tabIndicatorX = -1F;
        private float tabIndicatorWidth;
        private float tabIndicatorTargetX;
        private float tabIndicatorTargetWidth;
        private float tabAnimationStartX;
        private float tabAnimationStartWidth;
        private float tabIndicatorOpacity = 1F;
        private long tabAnimationStartedTimestamp;
        private long lastMotionTimestamp;
        private bool motionRunning;
        private int motionTickPending;
        private ContextMenuStrip activeDropDownMenu;
        private object designSelectedObject;
        private string applicationButtonText = "Archivo";
        private string titleText = string.Empty;
        private string headerContextText = string.Empty;
        private string headerIconKey = "ic_fluent_apps_24_regular";

        public RibbonControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint, true);

            tabs = new RibbonTabCollection(this);
            toolTip = new ToolTip();
            toolTip.IsBalloon = false;
            toolTip.ShowAlways = true;
            toolTip.AutoPopDelay = 7000;
            toolTip.InitialDelay = 450;
            toolTip.ReshowDelay = 100;
            // The clock is independent from the WinForms message timer. Frames are coalesced
            // on the UI thread, so a busy paint never builds a queue of obsolete animation work.
            motionTimer = new System.Threading.Timer(MotionClockPulse, null,
                System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            appearance = new RibbonAppearance(this);
            hostedControls = new Dictionary<RibbonHostedItem, Control>();
            responsiveSmallButtons = new HashSet<RibbonButton>();
            responsiveCompactGroups = new HashSet<RibbonGroup>();
            collapsedGroups = new HashSet<RibbonGroup>();
            overflowGroups = new HashSet<RibbonGroup>();
            itemHoverAnimations = new Dictionary<RibbonItem, float>();
            itemPressAnimations = new Dictionary<RibbonItem, float>();
            toggleAnimations = new Dictionary<RibbonToggleButton, float>();
            badgeAnimations = new Dictionary<RibbonItem, float>();
            observedBadges = new Dictionary<RibbonItem, string>();
            tabHoverAnimations = new Dictionary<RibbonTab, float>();
            Dock = DockStyle.Top;
            Height = CaptionHeight + TabHeight + DefaultContentHeight;
            MinimumSize = new Size(360, CaptionHeight + TabHeight + 76);
            TabStop = true;
            AccessibleRole = AccessibleRole.PageTabList;
            BackColor = appearance.CanvasColor;
            ForeColor = appearance.TextColor;
            Font = new Font("Segoe UI", 9F, FontStyle.Regular);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (activeDropDownMenu != null)
                    activeDropDownMenu.Dispose();
                motionRunning = false;
                using (System.Threading.ManualResetEvent timerStopped = new System.Threading.ManualResetEvent(false))
                {
                    motionTimer.Dispose(timerStopped);
                    timerStopped.WaitOne();
                }
                toolTip.Dispose();
            }
            base.Dispose(disposing);
        }

        [Category("Ribbon")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
        [Editor("ZarpaSuite.Controls.Design.RibbonTabCollectionEditor, Zarpa.Controls", typeof(UITypeEditor))]
        public RibbonTabCollection Tabs
        {
            get { return tabs; }
        }

        [Category("Ribbon")]
        [DefaultValue(0)]
        public int SelectedTabIndex
        {
            get { return selectedTabIndex; }
            set
            {
                int next = tabs.Count == 0 ? 0 : Math.Max(0, Math.Min(value, tabs.Count - 1));
                if (selectedTabIndex == next)
                    return;
                int previous = selectedTabIndex;
                selectedTabIndex = next;
                SetKeyboardTarget(null);
                RibbonTab nextTab = tabs.Count == 0 ? null : tabs[next];
                if (nextTab != null && !nextTab.Bounds.IsEmpty)
                {
                    tabIndicatorTargetX = nextTab.Bounds.Left + SX(12);
                    tabIndicatorTargetWidth = Math.Max(SX(8), nextTab.Bounds.Width - SX(24));
                    BeginTabAnimation();
                }
                else
                    StartMotion();
                LayoutHostedControls();
                Invalidate();
                if (SelectedTabChanged != null)
                    SelectedTabChanged(this, EventArgs.Empty);
                NotifyAccessibleChild(AccessibleEvents.StateChange, previous);
                NotifyAccessibleChild(AccessibleEvents.Selection, selectedTabIndex);
            }
        }

        [Category("Ribbon")]
        [DefaultValue("Archivo")]
        public string ApplicationButtonText
        {
            get { return applicationButtonText; }
            set { applicationButtonText = value ?? string.Empty; Invalidate(); }
        }

        [Category("Ribbon")]
        [DefaultValue("")]
        public string TitleText
        {
            get { return titleText; }
            set { titleText = value ?? string.Empty; Invalidate(); }
        }

        [Category("Ribbon")]
        [DefaultValue("")]
        public string HeaderContextText
        {
            get { return headerContextText; }
            set { headerContextText = value ?? string.Empty; Invalidate(); }
        }

        [Category("Ribbon")]
        [DefaultValue("ic_fluent_apps_24_regular")]
        [Editor("ZarpaSuite.Controls.Design.FluentIconPickerEditor, Zarpa.Controls", typeof(UITypeEditor))]
        public string HeaderIconKey
        {
            get { return headerIconKey; }
            set { headerIconKey = value ?? string.Empty; Invalidate(); }
        }

        [Category("Apariencia")]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
        public RibbonAppearance Appearance
        {
            get { return appearance; }
        }

        [Category("Responsive")]
        [DefaultValue(false)]
        public bool ResponsiveEnabled
        {
            get { return responsiveEnabled; }
            set { responsiveEnabled = value; LayoutHostedControls(); Invalidate(); }
        }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public Color AccentColor
        {
            get { return appearance.AccentColor; }
            set { appearance.AccentColor = value; }
        }

        public event EventHandler SelectedTabChanged;
        public event EventHandler ApplicationButtonClick;

        public void ApplyTheme(ZarpaThemeTokens theme)
        {
            if (theme == null) return;
            appearance.Preset = RibbonThemePreset.Custom;
            appearance.CanvasColor = theme.Canvas;
            appearance.SurfaceColor = theme.Surface;
            appearance.RaisedColor = theme.SurfaceRaised;
            appearance.GroupSurfaceColor = theme.Surface;
            appearance.HeaderSurfaceColor = theme.SurfaceRaised;
            appearance.TabStripColor = theme.Surface;
            appearance.HeaderTextColor = theme.Text;
            appearance.HoverColor = theme.SurfaceRaised;
            appearance.PressedColor = theme.SurfaceOverlay;
            appearance.SelectionColor = theme.Selection;
            appearance.BorderColor = theme.Border;
            appearance.StrongBorderColor = theme.BorderStrong;
            appearance.GroupBorderColor = theme.Border;
            appearance.ShadowColor = theme.Shadow;
            appearance.TextColor = theme.Text;
            appearance.MutedTextColor = theme.TextMuted;
            appearance.AccentColor = theme.Accent;
            appearance.AccentHoverColor = theme.AccentHover;
            appearance.AccentPressedColor = theme.AccentPressed;
            appearance.SuccessColor = theme.Success;
            appearance.WarningColor = theme.Warning;
            appearance.DangerColor = theme.Danger;
            appearance.InformationColor = theme.Information;
            appearance.CornerRadius = theme.CornerRadius;
            appearance.GroupCornerRadius = theme.GroupCornerRadius;
            appearance.HeaderHeight = theme.HeaderHeight;
            appearance.TabHeight = theme.TabHeight;
            appearance.ContentPadding = theme.SpacingMedium;
            appearance.ItemSpacing = theme.SpacingSmall;
            appearance.IconSize = theme.IconSize;
            appearance.BorderThickness = theme.BorderThickness;
            appearance.ShadowDepth = theme.ShadowDepth;
            appearance.FontFamily = theme.FontFamily;
            appearance.FontSize = theme.FontSize;
            appearance.MotionEnabled = theme.MotionEnabled;
            appearance.HoverAnimationDuration = theme.HoverDuration;
            appearance.PressAnimationDuration = theme.PressDuration;
            appearance.TabAnimationDuration = theme.TabDuration;
            Font = new Font(theme.FontFamily, theme.FontSize);
            BackColor = theme.Canvas;
            ForeColor = theme.Text;
            Invalidate();
        }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        internal object DesignSelectedObject
        {
            get { return designSelectedObject; }
            set { designSelectedObject = value; Invalidate(); }
        }

        [Browsable(false)]
        internal bool IsDesignerHosted
        {
            get { return Site != null && Site.DesignMode; }
        }

        internal void AttachItems()
        {
            foreach (RibbonTab tab in tabs)
            {
                tab.Owner = this;
                foreach (RibbonGroup group in tab.Groups)
                {
                    group.Owner = this;
                    foreach (RibbonItem item in group.Items)
                        item.Owner = this;
                }
            }

            if (selectedTabIndex >= tabs.Count)
                selectedTabIndex = Math.Max(0, tabs.Count - 1);
            SyncHostedControls();
            LayoutHostedControls();
            Invalidate();
            if (IsHandleCreated && !SuppressAccessibilityInterop) AccessibilityNotifyClients(AccessibleEvents.Reorder, 0);
        }

        internal void ItemChanged(RibbonItem item)
        {
            if (item.Busy)
                StartMotion();
            if (!IsDesignerHosted && IsHandleCreated)
            {
                RibbonToggleButton toggle = item as RibbonToggleButton;
                if (toggle != null)
                {
                    float current;
                    if (!toggleAnimations.TryGetValue(toggle, out current))
                        current = toggle.Checked ? 0F : 1F;
                    toggleAnimations[toggle] = current;
                    StartMotion();
                }
                string previousBadge;
                if (observedBadges.TryGetValue(item, out previousBadge) &&
                    !string.Equals(previousBadge, item.BadgeText, StringComparison.Ordinal))
                {
                    observedBadges[item] = item.BadgeText;
                    if (appearance.AnimateBadges && !string.IsNullOrEmpty(item.BadgeText))
                    {
                        badgeAnimations[item] = 0F;
                        StartMotion();
                    }
                }
            }
            RibbonHostedItem hostedItem = item as RibbonHostedItem;
            Control hostedControl;
            if (hostedItem != null && hostedControls.TryGetValue(hostedItem, out hostedControl))
            {
                syncingHostedControls = true;
                try
                {
                    UpdateHostedControl(hostedItem, hostedControl);
                }
                finally
                {
                    syncingHostedControls = false;
                }
            }
            LayoutHostedControls();
            Invalidate();
        }

        internal void AppearanceChanged()
        {
            if (appearance == null)
                return;
            BackColor = appearance.CanvasColor;
            ForeColor = appearance.TextColor;
            if (!string.IsNullOrEmpty(appearance.FontFamily) &&
                (Font == null || Font.Name != appearance.FontFamily || Math.Abs(Font.Size - appearance.FontSize) > 0.01F))
                Font = new Font(appearance.FontFamily, appearance.FontSize, FontStyle.Regular);
            MinimumSize = new Size(SX(360), CaptionHeight + TabHeight + SY(76));
            if (!appearance.MotionEnabled) SnapMotionState();
            SyncHostedControls();
            LayoutHostedControls();
            Invalidate();
        }

        private void StartMotion()
        {
            if (appearance == null || !appearance.MotionEnabled || IsDesignerHosted)
            {
                SnapMotionState();
                Invalidate();
                return;
            }
            if (!motionRunning)
            {
                lastMotionTimestamp = Stopwatch.GetTimestamp();
                motionRunning = true;
                motionTimer.Change(0, 8);
            }
        }

        private void StopMotion()
        {
            motionRunning = false;
            motionTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            lastMotionTimestamp = 0L;
        }

        private void SnapMotionState()
        {
            StopMotion();
            itemHoverAnimations.Clear();
            itemPressAnimations.Clear();
            toggleAnimations.Clear();
            badgeAnimations.Clear();
            tabHoverAnimations.Clear();
            rippleItem = null;
            rippleProgress = 1F;
            applicationHoverProgress = hotApplicationButton ? 1F : 0F;
            applicationPressProgress = pressedApplicationButton ? 1F : 0F;
            if (tabIndicatorX >= 0F)
            {
                tabIndicatorX = tabIndicatorTargetX;
                tabIndicatorWidth = tabIndicatorTargetWidth;
            }
            tabIndicatorOpacity = 1F;
            tabAnimationStartedTimestamp = 0L;
        }

        private void MotionClockPulse(object state)
        {
            if (!motionRunning || IsDisposed || Disposing || !IsHandleCreated)
                return;
            if (System.Threading.Interlocked.Exchange(ref motionTickPending, 1) != 0)
                return;
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    System.Threading.Interlocked.Exchange(ref motionTickPending, 0);
                    if (motionRunning && !IsDisposed)
                        MotionTimerTick(this, EventArgs.Empty);
                });
            }
            catch (ObjectDisposedException)
            {
                System.Threading.Interlocked.Exchange(ref motionTickPending, 0);
            }
            catch (InvalidOperationException)
            {
                System.Threading.Interlocked.Exchange(ref motionTickPending, 0);
            }
        }

        private void BeginTabAnimation()
        {
            if (tabIndicatorX < 0F || !appearance.MotionEnabled || IsDesignerHosted ||
                appearance.TabAnimation == RibbonTabAnimation.None)
            {
                tabIndicatorX = tabIndicatorTargetX;
                tabIndicatorWidth = tabIndicatorTargetWidth;
                tabIndicatorOpacity = 1F;
                tabAnimationStartedTimestamp = 0L;
                return;
            }
            tabAnimationStartX = tabIndicatorX;
            tabAnimationStartWidth = tabIndicatorWidth;
            tabIndicatorOpacity = 1F;
            tabAnimationStartedTimestamp = Stopwatch.GetTimestamp();
            StartMotion();
        }

        private void MotionTimerTick(object sender, EventArgs e)
        {
            if (!appearance.MotionEnabled || IsDesignerHosted)
            {
                SnapMotionState();
                Invalidate();
                return;
            }
            long now = Stopwatch.GetTimestamp();
            double elapsed = lastMotionTimestamp == 0L
                ? 0.008
                : (now - lastMotionTimestamp) / (double)Stopwatch.Frequency;
            lastMotionTimestamp = now;
            float deltaSeconds = (float)Math.Max(0.001, Math.Min(0.050, elapsed));

            HashSet<RibbonItem> interactionDirtyItems = CollectInteractionItems();
            bool interactionsMoving = UpdateInteractionAnimations(deltaSeconds);
            bool tabInteractionsMoving = UpdateTabInteractionAnimations(deltaSeconds);
            foreach (RibbonItem dirtyItem in CollectInteractionItems())
                interactionDirtyItems.Add(dirtyItem);
            busyAngle = (busyAngle + 260F * deltaSeconds) % 360F;
            Rectangle previousTabIndicator = GetTabIndicatorPaintBounds(tabIndicatorX, tabIndicatorWidth);
            bool tabWasMoving = tabAnimationStartedTimestamp != 0L;
            if (tabWasMoving)
                UpdateTabAnimation(now);
            bool tabMoving = tabAnimationStartedTimestamp != 0L;
            bool busy = HasBusyItems();
            if (!interactionsMoving && !tabInteractionsMoving && !tabMoving && !busy)
                StopMotion();

            // Repaint only animated pixels. Invalidating the whole Ribbon here makes hosted
            // editors, cards, text and every icon compete with mouse/keyboard messages.
            foreach (RibbonItem dirtyItem in interactionDirtyItems)
                InvalidateAnimatedBounds(dirtyItem.Bounds, 4);
            if (tabInteractionsMoving)
                InvalidateTabInteractionRegions();
            if (tabWasMoving)
            {
                Rectangle currentIndicator = GetTabIndicatorPaintBounds(tabIndicatorX, tabIndicatorWidth);
                Rectangle dirty = Rectangle.Union(previousTabIndicator, currentIndicator);
                if (!dirty.IsEmpty)
                    Invalidate(dirty);
            }
            if (busy)
                InvalidateBusyItems();
        }

        private HashSet<RibbonItem> CollectInteractionItems()
        {
            HashSet<RibbonItem> result = new HashSet<RibbonItem>();
            foreach (RibbonItem item in itemHoverAnimations.Keys) result.Add(item);
            foreach (RibbonItem item in itemPressAnimations.Keys) result.Add(item);
            foreach (RibbonToggleButton item in toggleAnimations.Keys) result.Add(item);
            foreach (RibbonItem item in badgeAnimations.Keys) result.Add(item);
            if (rippleItem != null) result.Add(rippleItem);
            return result;
        }

        private bool UpdateInteractionAnimations(float deltaSeconds)
        {
            bool moving = false;
            moving |= UpdateTargetAnimation(itemHoverAnimations, hotItem, deltaSeconds,
                appearance.HoverAnimationDuration, true);
            moving |= UpdateTargetAnimation(itemPressAnimations, pressedItem, deltaSeconds,
                appearance.PressAnimationDuration, true);

            foreach (RibbonToggleButton toggle in new List<RibbonToggleButton>(toggleAnimations.Keys))
            {
                float value = toggleAnimations[toggle];
                float target = toggle.Checked ? 1F : 0F;
                value = MoveTowards(value, target, deltaSeconds * 1000F / appearance.PressAnimationDuration);
                toggleAnimations[toggle] = value;
                if (Math.Abs(value - target) > 0.001F) moving = true;
            }

            foreach (RibbonItem item in new List<RibbonItem>(badgeAnimations.Keys))
            {
                float value = Math.Min(1F, badgeAnimations[item] +
                    deltaSeconds * 1000F / Math.Max(120, appearance.RippleAnimationDuration));
                badgeAnimations[item] = value;
                if (value >= 1F) badgeAnimations.Remove(item); else moving = true;
            }

            if (rippleItem != null)
            {
                rippleProgress = Math.Min(1F, rippleProgress +
                    deltaSeconds * 1000F / appearance.RippleAnimationDuration);
                if (rippleProgress >= 1F) rippleItem = null; else moving = true;
            }
            return moving;
        }

        private bool UpdateTabInteractionAnimations(float deltaSeconds)
        {
            bool moving = false;
            RibbonTab activeTab = hotTabIndex >= 0 && hotTabIndex < tabs.Count ? tabs[hotTabIndex] : null;
            List<RibbonTab> keys = new List<RibbonTab>(tabHoverAnimations.Keys);
            if (activeTab != null && !tabHoverAnimations.ContainsKey(activeTab))
            {
                tabHoverAnimations[activeTab] = 0F;
                keys.Add(activeTab);
            }
            float amount = deltaSeconds * 1000F / appearance.HoverAnimationDuration;
            foreach (RibbonTab tab in keys)
            {
                float target = tab == activeTab ? 1F : 0F;
                float value = MoveTowards(tabHoverAnimations[tab], target, amount);
                tabHoverAnimations[tab] = value;
                if (Math.Abs(value - target) > 0.001F) moving = true;
                else if (target <= 0F) tabHoverAnimations.Remove(tab);
            }

            float nextApplicationHover = MoveTowards(applicationHoverProgress,
                hotApplicationButton ? 1F : 0F, amount);
            float pressAmount = deltaSeconds * 1000F / appearance.PressAnimationDuration;
            float nextApplicationPress = MoveTowards(applicationPressProgress,
                pressedApplicationButton ? 1F : 0F, pressAmount);
            if (Math.Abs(nextApplicationHover - (hotApplicationButton ? 1F : 0F)) > 0.001F ||
                Math.Abs(nextApplicationPress - (pressedApplicationButton ? 1F : 0F)) > 0.001F)
                moving = true;
            applicationHoverProgress = nextApplicationHover;
            applicationPressProgress = nextApplicationPress;
            return moving;
        }

        private void InvalidateTabInteractionRegions()
        {
            InvalidateAnimatedBounds(ApplicationButtonBounds, 2);
            foreach (RibbonTab tab in tabHoverAnimations.Keys)
                InvalidateAnimatedBounds(tab.Bounds, 2);
        }

        private static bool UpdateTargetAnimation(Dictionary<RibbonItem, float> states,
            RibbonItem activeItem, float deltaSeconds, int duration, bool removeAtZero)
        {
            bool moving = false;
            List<RibbonItem> keys = new List<RibbonItem>(states.Keys);
            if (activeItem != null && !states.ContainsKey(activeItem))
            {
                states[activeItem] = 0F;
                keys.Add(activeItem);
            }
            float amount = deltaSeconds * 1000F / Math.Max(1, duration);
            foreach (RibbonItem item in keys)
            {
                float target = item == activeItem ? 1F : 0F;
                float value = MoveTowards(states[item], target, amount);
                states[item] = value;
                if (Math.Abs(value - target) > 0.001F)
                    moving = true;
                else if (removeAtZero && target <= 0F)
                    states.Remove(item);
            }
            return moving;
        }

        private static float MoveTowards(float value, float target, float maximumDelta)
        {
            if (Math.Abs(target - value) <= maximumDelta) return target;
            return value + Math.Sign(target - value) * maximumDelta;
        }

        private void UpdateTabAnimation(long now)
        {
            float durationSeconds = Math.Max(0.08F, appearance.TabAnimationDuration / 1000F);
            float progress = (float)((now - tabAnimationStartedTimestamp) /
                (double)Stopwatch.Frequency / durationSeconds);
            if (progress >= 1F || appearance.TabAnimation == RibbonTabAnimation.None ||
                !appearance.MotionEnabled)
            {
                tabIndicatorX = tabIndicatorTargetX;
                tabIndicatorWidth = tabIndicatorTargetWidth;
                tabIndicatorOpacity = 1F;
                tabAnimationStartedTimestamp = 0L;
                return;
            }
            progress = Math.Max(0F, progress);
            float eased = progress * progress * (3F - 2F * progress);
            switch (appearance.TabAnimation)
            {
                case RibbonTabAnimation.Fade:
                    if (progress < 0.5F)
                    {
                        tabIndicatorX = tabAnimationStartX;
                        tabIndicatorWidth = tabAnimationStartWidth;
                        tabIndicatorOpacity = 1F - progress * 2F;
                    }
                    else
                    {
                        tabIndicatorX = tabIndicatorTargetX;
                        tabIndicatorWidth = tabIndicatorTargetWidth;
                        tabIndicatorOpacity = (progress - 0.5F) * 2F;
                    }
                    break;
                case RibbonTabAnimation.FluentStretch:
                    float startCenter = tabAnimationStartX + tabAnimationStartWidth / 2F;
                    float targetCenter = tabIndicatorTargetX + tabIndicatorTargetWidth / 2F;
                    float center = Lerp(startCenter, targetCenter, eased);
                    float baseWidth = Lerp(tabAnimationStartWidth, tabIndicatorTargetWidth, eased);
                    float travel = Math.Abs(targetCenter - startCenter);
                    float stretch = (float)Math.Sin(Math.PI * progress) * Math.Min(44F, travel * 0.24F);
                    tabIndicatorWidth = baseWidth + stretch;
                    tabIndicatorX = center - tabIndicatorWidth / 2F;
                    tabIndicatorOpacity = 1F;
                    break;
                default:
                    float slide = 1F - (float)Math.Pow(1F - progress, 3F);
                    tabIndicatorX = Lerp(tabAnimationStartX, tabIndicatorTargetX, slide);
                    tabIndicatorWidth = Lerp(tabAnimationStartWidth, tabIndicatorTargetWidth, slide);
                    tabIndicatorOpacity = 1F;
                    break;
            }
        }

        private static float Lerp(float from, float to, float amount)
        {
            return from + (to - from) * amount;
        }

        private void InvalidateBusyItems()
        {
            RibbonTab selected = GetSelectedTab();
            if (selected == null)
                return;
            foreach (RibbonGroup group in selected.Groups)
                foreach (RibbonItem item in group.Items)
                    if (item.Busy)
                        InvalidateAnimatedBounds(item.Bounds, 3);
        }

        private void InvalidateAnimatedBounds(Rectangle bounds, int padding)
        {
            if (bounds.IsEmpty)
                return;
            bounds.Inflate(padding, padding);
            Invalidate(bounds);
        }

        private Rectangle GetTabIndicatorPaintBounds(float x, float width)
        {
            if (x < 0F || width <= 0F)
                return Rectangle.Empty;
            int left = Math.Max(0, (int)Math.Floor(x) - SX(5));
            int right = Math.Min(Width, (int)Math.Ceiling(x + width) + SX(5));
            return Rectangle.FromLTRB(left, CaptionHeight + TabHeight - SY(10), right,
                CaptionHeight + TabHeight);
        }

        private bool HasBusyItems()
        {
            RibbonTab selected = GetSelectedTab();
            if (selected == null) return false;
            foreach (RibbonGroup group in selected.Groups)
                foreach (RibbonItem item in group.Items)
                    if (item.Busy) return true;
            return false;
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            if (hostedControls != null)
            {
                SyncHostedControls();
                LayoutHostedControls();
            }
        }

        protected override void OnLayout(LayoutEventArgs levent)
        {
            base.OnLayout(levent);
            LayoutHostedControls();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDpiScale(ZarpaDpiScale.FromControl(this));
            SyncHostedControls();
            LayoutHostedControls();
        }

        internal void ApplyDpiForTest(int dpi)
        {
            ApplyDpiScale(new ZarpaDpiScale(dpi, dpi));
        }

        private void ApplyDpiScale(ZarpaDpiScale value)
        {
            if (value == null || (dpiScale.DpiX == value.DpiX && dpiScale.DpiY == value.DpiY)) return;
            Size previousMinimum = new Size(SX(360), CaptionHeight + TabHeight + SY(76));
            int previousDefaultHeight = CaptionHeight + TabHeight + SY(DefaultContentHeight);
            bool ownsMinimum = MinimumSize == previousMinimum;
            bool ownsHeight = Height == previousDefaultHeight;
            dpiScale = value;
            if (ownsMinimum) MinimumSize = new Size(SX(360), CaptionHeight + TabHeight + SY(76));
            if (ownsHeight) Height = CaptionHeight + TabHeight + SY(DefaultContentHeight);
            ResetItemBounds();
            foreach (RibbonTab tab in tabs) tab.Bounds = Rectangle.Empty;
            addTabBounds = addGroupBounds = overflowBounds = Rectangle.Empty;
            tabIndicatorX = -1F;
            tabIndicatorWidth = tabIndicatorTargetWidth = 0F;
            SyncHostedControls();
            LayoutHostedControls();
            PerformLayout();
            Invalidate(true);
        }

        private void LayoutHostedControls()
        {
            if (hostedControls == null || syncingHostedControls)
                return;

            if (IsDesignerHosted)
            {
                foreach (Control control in hostedControls.Values)
                    control.Visible = false;
                return;
            }

            RibbonTab selected = GetSelectedTab();
            if (selected == null)
            {
                foreach (Control control in hostedControls.Values)
                    control.Visible = false;
                return;
            }

            HashSet<RibbonHostedItem> activeItems = new HashSet<RibbonHostedItem>();
            int contentTop = CaptionHeight + TabHeight;
            int contentHeight = Height - contentTop;
            int groupX = SX(8);
            PrepareResponsiveLayout(selected);
            foreach (RibbonGroup group in selected.Groups)
            {
                if (overflowGroups.Contains(group))
                    continue;
                int groupWidth = CalculateGroupWidth(group);
                group.Bounds = new Rectangle(groupX, contentTop + SY(5), groupWidth, contentHeight - SY(10));
                if (collapsedGroups.Contains(group))
                {
                    groupX += groupWidth + SX(5);
                    continue;
                }
                Rectangle itemArea = new Rectangle(group.Bounds.Left + SX(5), group.Bounds.Top + SY(1),
                    group.Bounds.Width - SX(10), group.Bounds.Height - SY(27));
                LayoutGroupItems(group, itemArea);
                foreach (RibbonItem item in group.Items)
                {
                    RibbonHostedItem hostedItem = item as RibbonHostedItem;
                    if (hostedItem != null)
                    {
                        activeItems.Add(hostedItem);
                        Rectangle controlBounds = GetHostedControlBounds(hostedItem, hostedItem.Bounds);
                        Control hosted;
                        if (hostedControls.TryGetValue(hostedItem, out hosted))
                        {
                            hosted.Bounds = controlBounds;
                            if (!hosted.Visible)
                                hosted.Visible = true;
                            hosted.BringToFront();
                        }
                    }
                }
                groupX += groupWidth + SX(5);
                if (groupX >= Width) break;
            }

            foreach (KeyValuePair<RibbonHostedItem, Control> pair in hostedControls)
                if (!activeItems.Contains(pair.Key))
                    pair.Value.Visible = false;
        }

        private void SyncHostedControls()
        {
            if (hostedControls == null || syncingHostedControls)
                return;

            syncingHostedControls = true;
            try
            {
                if (IsDesignerHosted)
                {
                    foreach (Control control in hostedControls.Values)
                    {
                        Controls.Remove(control);
                        control.Dispose();
                    }
                    hostedControls.Clear();
                    return;
                }

                HashSet<RibbonHostedItem> current = new HashSet<RibbonHostedItem>();
                foreach (RibbonTab tab in tabs)
                    foreach (RibbonGroup group in tab.Groups)
                        foreach (RibbonItem item in group.Items)
                        {
                            RibbonHostedItem hosted = item as RibbonHostedItem;
                            if (hosted != null)
                                current.Add(hosted);
                        }

                List<RibbonHostedItem> removed = new List<RibbonHostedItem>();
                foreach (KeyValuePair<RibbonHostedItem, Control> pair in hostedControls)
                    if (!current.Contains(pair.Key))
                        removed.Add(pair.Key);
                foreach (RibbonHostedItem item in removed)
                {
                    Control control = hostedControls[item];
                    Controls.Remove(control);
                    control.Dispose();
                    hostedControls.Remove(item);
                }

                foreach (RibbonHostedItem item in current)
                {
                    Control control;
                    if (!hostedControls.TryGetValue(item, out control))
                    {
                        control = CreateHostedControl(item);
                        control.Visible = false;
                        hostedControls.Add(item, control);
                        Controls.Add(control);
                    }
                    UpdateHostedControl(item, control);
                }
            }
            finally
            {
                syncingHostedControls = false;
            }
        }

        private Control CreateHostedControl(RibbonHostedItem item)
        {
            RibbonTextBox textItem = item as RibbonTextBox;
            if (textItem != null)
            {
                RibbonModernTextBoxHost control = new RibbonModernTextBoxHost();
                control.Editor.TextChanged += delegate { if (!syncingHostedControls) textItem.Value = control.Editor.Text; };
                return control;
            }

            RibbonComboBox comboItem = item as RibbonComboBox;
            if (comboItem != null)
            {
                RibbonModernComboBoxHost control = new RibbonModernComboBoxHost();
                control.Editor.SelectedIndexChanged += delegate { if (!syncingHostedControls) comboItem.SelectedIndex = control.Editor.SelectedIndex; };
                return control;
            }

            RibbonDatePicker dateItem = item as RibbonDatePicker;
            if (dateItem != null)
            {
                RibbonModernDateHost control = new RibbonModernDateHost();
                control.Editor.ValueChanged += delegate { if (!syncingHostedControls) dateItem.Value = control.Editor.Value; };
                return control;
            }

            RibbonCheckBox checkItem = item as RibbonCheckBox;
            if (checkItem != null)
            {
                RibbonModernCheckBoxHost control = new RibbonModernCheckBoxHost();
                control.CheckedChanged += delegate { if (!syncingHostedControls) checkItem.Checked = control.Checked; };
                return control;
            }

            RibbonNumericUpDown numericItem = item as RibbonNumericUpDown;
            if (numericItem != null)
            {
                RibbonModernNumericHost control = new RibbonModernNumericHost();
                control.Editor.ValueChanged += delegate { if (!syncingHostedControls) numericItem.Value = control.Editor.Value; };
                return control;
            }

            throw new NotSupportedException("Tipo de control Ribbon no soportado: " + item.GetType().FullName);
        }

        private void UpdateHostedControl(RibbonHostedItem item, Control control)
        {
            control.Font = Font;
            control.Enabled = item.Enabled;
            IRibbonModernHost modern = control as IRibbonModernHost;
            if (modern != null) { modern.ApplyDpiScale(dpiScale); modern.ApplyAppearance(appearance); }

            RibbonModernTextBoxHost text = control as RibbonModernTextBoxHost;
            RibbonTextBox textItem = item as RibbonTextBox;
            if (text != null && textItem != null)
            {
                text.Editor.ReadOnly = textItem.ReadOnly;
                text.Editor.MaxLength = textItem.MaxLength;
                if (text.Editor.Text != textItem.Value) text.Editor.Text = textItem.Value;
                return;
            }

            RibbonModernComboBoxHost combo = control as RibbonModernComboBoxHost;
            RibbonComboBox comboItem = item as RibbonComboBox;
            if (combo != null && comboItem != null)
            {
                combo.Editor.DropDownStyle = comboItem.DropDownStyle;
                combo.Editor.BeginUpdate();
                combo.Editor.Items.Clear();
                foreach (string value in comboItem.Items) combo.Editor.Items.Add(value);
                combo.Editor.EndUpdate();
                combo.Editor.SelectedIndex = Math.Max(-1, Math.Min(comboItem.SelectedIndex, combo.Editor.Items.Count - 1));
                return;
            }

            RibbonModernDateHost date = control as RibbonModernDateHost;
            RibbonDatePicker dateItem = item as RibbonDatePicker;
            if (date != null && dateItem != null)
            {
                date.Editor.Format = dateItem.Format;
                date.Editor.CustomFormat = dateItem.CustomFormat;
                date.Editor.ShowCheckBox = dateItem.ShowCheckBox;
                date.Editor.Value = dateItem.Value < date.Editor.MinDate ? date.Editor.MinDate : dateItem.Value > date.Editor.MaxDate ? date.Editor.MaxDate : dateItem.Value;
                return;
            }

            RibbonModernCheckBoxHost check = control as RibbonModernCheckBoxHost;
            RibbonCheckBox checkItem = item as RibbonCheckBox;
            if (check != null && checkItem != null)
            {
                check.Text = checkItem.Text;
                check.ThreeState = checkItem.ThreeState;
                check.Checked = checkItem.Checked;
                return;
            }

            RibbonModernNumericHost numeric = control as RibbonModernNumericHost;
            RibbonNumericUpDown numericItem = item as RibbonNumericUpDown;
            if (numeric != null && numericItem != null)
            {
                numeric.Editor.Minimum = numericItem.Minimum;
                numeric.Editor.Maximum = numericItem.Maximum;
                numeric.Editor.Increment = numericItem.Increment;
                numeric.Editor.DecimalPlaces = numericItem.DecimalPlaces;
                numeric.Editor.Value = Math.Max(numeric.Editor.Minimum, Math.Min(numeric.Editor.Maximum, numericItem.Value));
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics graphics = e.Graphics;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush canvas = new SolidBrush(appearance.CanvasColor))
                graphics.FillRectangle(canvas, e.ClipRectangle);

            Rectangle captionBounds = new Rectangle(0, 0, Width, CaptionHeight);
            Rectangle tabBounds = new Rectangle(0, CaptionHeight, Width, TabHeight);
            Rectangle contentBounds = new Rectangle(0, CaptionHeight + TabHeight, Width,
                Math.Max(0, Height - CaptionHeight - TabHeight));
            if (e.ClipRectangle.IntersectsWith(captionBounds))
                DrawCaption(graphics);
            if (e.ClipRectangle.IntersectsWith(tabBounds))
            {
                bool indicatorFrame = e.ClipRectangle.Top >= CaptionHeight + TabHeight - SY(10);
                if (indicatorFrame)
                    DrawTabAnimationFrame(graphics, e.ClipRectangle);
                else
                    DrawTabStrip(graphics);
            }
            if (e.ClipRectangle.IntersectsWith(contentBounds))
            {
                DrawSelectedTab(graphics);
                DrawDesignGrid(graphics);
                DrawDesignSelection(graphics);
            }
        }

        private void DrawCaption(Graphics graphics)
        {
            Rectangle bounds = new Rectangle(0, 0, Width, CaptionHeight);
            using (SolidBrush fill = new SolidBrush(appearance.HeaderSurfaceColor))
                graphics.FillRectangle(fill, bounds);

            using (Pen separator = new Pen(Blend(appearance.BorderColor,
                appearance.HeaderSurfaceColor, 0.30F), dpiScale.Stroke(appearance.BorderThickness)))
                graphics.DrawLine(separator, 0, CaptionHeight - dpiScale.Stroke(1), Width, CaptionHeight - dpiScale.Stroke(1));

            Rectangle brandMark = new Rectangle(SX(12), SY(9), SX(20), SY(20));
            FillRoundedRectangle(graphics, Blend(appearance.AccentColor,
                appearance.HeaderSurfaceColor, 0.84F), brandMark, SX(4));
            if (!FluentIconCatalog.TryDraw(graphics, headerIconKey, brandMark,
                appearance.AccentColor, SX(16F)))
            {
                using (SolidBrush accent = new SolidBrush(appearance.AccentColor))
                    graphics.FillRectangle(accent, brandMark.Left + SX(8), brandMark.Top + SY(4), SX(4), SY(12));
            }

            string contextDisplay = headerContextText.ToUpperInvariant();
            int contextWidth = string.IsNullOrEmpty(headerContextText) ? 0 :
                Math.Min(SX(280), TextRenderer.MeasureText(contextDisplay, Font).Width + SX(30));
            Rectangle titleBounds = new Rectangle(SX(42), 0,
                Math.Max(SX(20), Width - SX(58) - contextWidth), CaptionHeight);
            using (Font titleFont = new Font(Font.FontFamily, Math.Max(9F, Font.Size + 0.25F), FontStyle.Bold))
                TextRenderer.DrawText(graphics, titleText, titleFont, titleBounds,
                    appearance.HeaderTextColor, TextFormatFlags.Left |
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);

            if (contextWidth > 0)
            {
                Rectangle contextBounds = new Rectangle(Width - contextWidth - SX(12), SY(7),
                    contextWidth, CaptionHeight - SY(14));
                FillRoundedRectangle(graphics, appearance.RaisedColor, contextBounds, SX(4));
                DrawRoundedRectangle(graphics, appearance.BorderColor, contextBounds, SX(4));
                using (Font contextFont = new Font(Font.FontFamily, Math.Max(7.5F, Font.Size - 0.5F), FontStyle.Bold))
                    TextRenderer.DrawText(graphics, contextDisplay, contextFont,
                        contextBounds, appearance.MutedTextColor, TextFormatFlags.HorizontalCenter |
                        TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            }
        }

        private void DrawTabStrip(Graphics graphics)
        {
            using (SolidBrush fill = new SolidBrush(appearance.TabStripColor))
                graphics.FillRectangle(fill, 0, CaptionHeight, Width, TabHeight);

            Rectangle appBounds = ApplicationButtonBounds;
            Color appColor = Blend(appearance.AccentColor, appearance.AccentHoverColor,
                applicationHoverProgress);
            appColor = Blend(appColor, appearance.AccentPressedColor, applicationPressProgress);
            FillRoundedRectangle(graphics, appColor, appBounds, SX(4));
            DrawRoundedRectangle(graphics, Blend(appColor, Color.Black, 0.16F), appBounds, SX(4));
            Rectangle appText = Rectangle.FromLTRB(appBounds.Left + SX(12), appBounds.Top,
                appBounds.Right - SX(24), appBounds.Bottom);
            TextRenderer.DrawText(graphics, applicationButtonText, Font, appText, Color.White,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            using (Pen chevron = new Pen(Color.FromArgb(225, Color.White), SX(1.4F)))
            {
                int cx = appBounds.Right - SX(13);
                int cy = appBounds.Top + appBounds.Height / 2;
                graphics.DrawLines(chevron, new[] { new Point(cx - SX(3), cy - SY(2)),
                    new Point(cx, cy + SY(1)), new Point(cx + SX(3), cy - SY(2)) });
            }

            int x = appBounds.Right + SX(8);
            foreach (RibbonTab tab in tabs)
            {
                tab.Bounds = Rectangle.Empty;
                if (!tab.Visible)
                    continue;

                int index = tabs.IndexOf(tab);
                int width = Math.Max(SX(72), TextRenderer.MeasureText(tab.Text, Font).Width + SX(26));
                Rectangle bounds = new Rectangle(x, CaptionHeight + SY(2), width, TabHeight - SY(4));
                tab.Bounds = bounds;
                if (index == selectedTabIndex)
                {
                    FillRoundedRectangle(graphics, Blend(appearance.TabStripColor,
                        appearance.SelectionColor, 0.52F), bounds, 4);
                    float nextTargetX = bounds.Left + SX(12);
                    float nextTargetWidth = Math.Max(SX(8), bounds.Width - SX(24));
                    bool targetChanged = Math.Abs(nextTargetX - tabIndicatorTargetX) > 0.1F ||
                        Math.Abs(nextTargetWidth - tabIndicatorTargetWidth) > 0.1F;
                    tabIndicatorTargetX = nextTargetX;
                    tabIndicatorTargetWidth = nextTargetWidth;
                    if (!appearance.MotionEnabled || IsDesignerHosted || tabIndicatorX < 0F ||
                        appearance.TabAnimation == RibbonTabAnimation.None)
                    {
                        tabIndicatorX = tabIndicatorTargetX;
                        tabIndicatorWidth = tabIndicatorTargetWidth;
                        tabIndicatorOpacity = 1F;
                    }
                    else if (targetChanged && tabAnimationStartedTimestamp == 0L)
                        BeginTabAnimation();
                }
                else
                {
                    float tabHover;
                    if (!tabHoverAnimations.TryGetValue(tab, out tabHover))
                        tabHover = index == hotTabIndex ? 1F : 0F;
                    if (tabHover > 0F)
                        FillRoundedRectangle(graphics, Blend(appearance.SurfaceColor,
                            appearance.HoverColor, tabHover), bounds, SX(appearance.CornerRadius));
                }

                TextRenderer.DrawText(graphics, tab.Text, Font, bounds, appearance.TextColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

                x += width + SX(2);
            }

            DrawTabIndicator(graphics);

            if (IsDesignerHosted)
            {
                addTabBounds = new Rectangle(x + SX(4), CaptionHeight + SY(6), SX(26), TabHeight - SY(12));
                DrawInsertButton(graphics, addTabBounds, "+", false);
            }
            else
            {
                addTabBounds = Rectangle.Empty;
            }
        }

        private void DrawTabAnimationFrame(Graphics graphics, Rectangle clip)
        {
            // Restore only the few pixels occupied by the old indicator. Text and the
            // application button never participate in this frame.
            using (SolidBrush fill = new SolidBrush(appearance.TabStripColor))
                graphics.FillRectangle(fill, clip);

            foreach (RibbonTab tab in tabs)
            {
                if (!tab.Visible || tab.Bounds.IsEmpty || !tab.Bounds.IntersectsWith(clip))
                    continue;
                int index = tabs.IndexOf(tab);
                if (index == selectedTabIndex)
                {
                    FillRoundedRectangle(graphics, Blend(appearance.TabStripColor,
                        appearance.SelectionColor, 0.52F), tab.Bounds, SX(4));
                }
                else if (index == hotTabIndex)
                    FillRoundedRectangle(graphics, appearance.HoverColor, tab.Bounds, SX(appearance.CornerRadius));
            }
            DrawTabIndicator(graphics);
        }

        private void DrawTabIndicator(Graphics graphics)
        {
            if (tabIndicatorX < 0F || tabIndicatorWidth <= 0F)
                return;
            float y = CaptionHeight + TabHeight - SY(2);
            Color glowColor = Color.FromArgb((int)(appearance.AccentGlowColor.A * tabIndicatorOpacity),
                appearance.AccentGlowColor);
            Color accentColor = Color.FromArgb((int)(appearance.AccentColor.A * tabIndicatorOpacity),
                appearance.AccentColor);
            using (Pen glow = new Pen(glowColor, SX(7F)))
                graphics.DrawLine(glow, tabIndicatorX, y, tabIndicatorX + tabIndicatorWidth, y);
            using (Pen accent = new Pen(accentColor, SX(3F)))
                graphics.DrawLine(accent, tabIndicatorX, y, tabIndicatorX + tabIndicatorWidth, y);
        }

        private void DrawSelectedTab(Graphics graphics)
        {
            Rectangle content = new Rectangle(0, CaptionHeight + TabHeight, Width,
                Height - CaptionHeight - TabHeight);
            using (SolidBrush fill = new SolidBrush(appearance.SurfaceColor))
                graphics.FillRectangle(fill, content);
            using (Pen stroke = new Pen(appearance.BorderColor))
                graphics.DrawLine(stroke, 0, content.Top, Width, content.Top);

            ResetItemBounds();
            RibbonTab selected = GetSelectedTab();
            if (selected == null)
                return;

            int x = SX(8);
            PrepareResponsiveLayout(selected);
            foreach (RibbonGroup group in selected.Groups)
            {
                if (overflowGroups.Contains(group))
                    continue;
                int width = CalculateGroupWidth(group);
                group.Bounds = new Rectangle(x, content.Top + SY(5), width, content.Height - SY(10));
                if (collapsedGroups.Contains(group))
                    DrawCollapsedGroup(graphics, group, group.Bounds);
                else
                    DrawGroup(graphics, group, group.Bounds);
                x += width + SX(5);
                if (x >= Width)
                    break;
            }

            if (overflowGroups.Count > 0 && !IsDesignerHosted)
            {
                overflowBounds = new Rectangle(Math.Min(x + SX(2), Width - SX(42)), content.Top + SY(13), SX(34), content.Height - SY(26));
                FillRoundedRectangle(graphics, hotItem == null && overflowBounds.Contains(PointToClient(MousePosition))
                    ? appearance.HoverColor : appearance.RaisedColor, overflowBounds, SX(appearance.CornerRadius));
                DrawRoundedRectangle(graphics, appearance.BorderColor, overflowBounds, SX(appearance.CornerRadius));
                TextRenderer.DrawText(graphics, "•••", Font, overflowBounds, appearance.TextColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            else
                overflowBounds = Rectangle.Empty;

            if (IsDesignerHosted && x + SX(90) < Width)
            {
                addGroupBounds = new Rectangle(x + SX(4), content.Top + SY(14), SX(86), Math.Max(SY(38), content.Height - SY(28)));
                DrawInsertButton(graphics, addGroupBounds, "+ Grupo", true);
            }
            else
            {
                addGroupBounds = Rectangle.Empty;
            }
        }

        private int CalculateGroupWidth(RibbonGroup group)
        {
            if (collapsedGroups.Contains(group))
                return Math.Max(SX(76), TextRenderer.MeasureText(group.Text, Font).Width + SX(32));
            Rectangle virtualArea = new Rectangle(0, 0, SX(10000), Math.Max(SY(30), Height - CaptionHeight - TabHeight - SY(37)));
            LayoutGroupItems(group, virtualArea);
            int width = SX(14);
            foreach (RibbonItem item in group.Items)
                width = Math.Max(width, item.Bounds.Right + SX(10));
            return Math.Max(width, TextRenderer.MeasureText(group.Text, Font).Width + SX(IsDesignerHosted ? 52 : 30));
        }

        private void PrepareResponsiveLayout(RibbonTab selected)
        {
            responsiveSmallButtons.Clear();
            responsiveCompactGroups.Clear();
            collapsedGroups.Clear();
            overflowGroups.Clear();
            if (!responsiveEnabled || IsDesignerHosted || selected == null)
                return;

            int available = Math.Max(SX(100), Width - SX(16));
            if (CalculateVisibleGroupsWidth(selected) <= available)
                return;

            List<RibbonButton> buttons = new List<RibbonButton>();
            foreach (RibbonGroup group in selected.Groups)
                foreach (RibbonItem item in group.Items)
                {
                    RibbonButton button = item as RibbonButton;
                    if (button != null && button.ItemSize == RibbonItemSize.Large && button.AllowResponsiveResize && !button.UseCustomBounds)
                        buttons.Add(button);
                }
            buttons.Sort(delegate(RibbonButton left, RibbonButton right)
            {
                return left.ResponsivePriority.CompareTo(right.ResponsivePriority);
            });
            foreach (RibbonButton button in buttons)
            {
                responsiveSmallButtons.Add(button);
                RibbonGroup ownerGroup = FindOwningGroup(selected, button);
                if (ownerGroup != null)
                    responsiveCompactGroups.Add(ownerGroup);
                if (CalculateVisibleGroupsWidth(selected) <= available)
                    return;
            }

            List<RibbonGroup> groups = new List<RibbonGroup>();
            foreach (RibbonGroup group in selected.Groups)
                if (group.AllowCollapse)
                    groups.Add(group);
            groups.Sort(delegate(RibbonGroup left, RibbonGroup right)
            {
                return left.ResponsivePriority.CompareTo(right.ResponsivePriority);
            });
            foreach (RibbonGroup group in groups)
            {
                collapsedGroups.Add(group);
                if (CalculateVisibleGroupsWidth(selected) <= available)
                    return;
            }

            int overflowSpace = SX(44);
            foreach (RibbonGroup group in groups)
            {
                overflowGroups.Add(group);
                collapsedGroups.Remove(group);
                if (CalculateVisibleGroupsWidth(selected) + overflowSpace <= available)
                    return;
            }
        }

        private int CalculateVisibleGroupsWidth(RibbonTab tab)
        {
            int width = SX(8);
            foreach (RibbonGroup group in tab.Groups)
                if (!overflowGroups.Contains(group))
                    width += CalculateGroupWidth(group) + SX(5);
            return width;
        }

        private static RibbonGroup FindOwningGroup(RibbonTab tab, RibbonItem item)
        {
            foreach (RibbonGroup group in tab.Groups)
                if (group.Items.Contains(item))
                    return group;
            return null;
        }

        private void DrawCollapsedGroup(Graphics graphics, RibbonGroup group, Rectangle bounds)
        {
            DrawGroupCard(graphics, bounds, true);
            Rectangle iconBounds = new Rectangle(bounds.Left + (bounds.Width - SX(26)) / 2, bounds.Top + SY(8), SX(26), SY(26));
            RibbonItem first = group.Items.Count == 0 ? null : group.Items[0];
            if (first != null && !string.IsNullOrEmpty(first.IconKey))
                FluentIconCatalog.TryDraw(graphics, first.IconKey, iconBounds, appearance.TextColor, SX(23F));
            TextRenderer.DrawText(graphics, group.Text + "  ▾", Font,
                new Rectangle(bounds.Left + SX(4), bounds.Top + SY(38), bounds.Width - SX(8), bounds.Height - SY(42)), appearance.TextColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (Focused && keyboardGroup == group && keyboardItem == null)
                DrawRoundedRectangle(graphics, appearance.AccentColor, Rectangle.Inflate(bounds, -SX(3), -SY(3)),
                    SX(appearance.GroupCornerRadius));
        }

        private void LayoutGroupItems(RibbonGroup group, Rectangle itemArea)
        {
            int x = itemArea.Left;
            int usedHeight = 0;
            int columnWidth = 0;
            int gapX = SX(3);
            int gapY = SY(3);

            foreach (RibbonItem item in group.Items)
            {
                bool compactLayout = group.LayoutMode == RibbonGroupLayout.CompactStack || responsiveCompactGroups.Contains(group);
                Size natural = GetNaturalItemSize(item, itemArea.Height, compactLayout);
                if (item.UseCustomBounds)
                {
                    if (usedHeight > 0)
                    {
                        x += columnWidth + gapX;
                        usedHeight = 0;
                        columnWidth = 0;
                    }
                    Point customLocation = dpiScale.Point(item.CustomLocation);
                    Size customSize = dpiScale.Size(item.CustomSize);
                    item.Bounds = new Rectangle(itemArea.Left + customLocation.X,
                        itemArea.Top + customLocation.Y, customSize.Width, customSize.Height);
                    x = Math.Max(x + natural.Width, item.Bounds.Right + gapX);
                    continue;
                }

                if (item is RibbonSeparator)
                {
                    if (usedHeight > 0)
                    {
                        x += columnWidth + gapX;
                        usedHeight = 0;
                        columnWidth = 0;
                    }
                    item.Bounds = new Rectangle(x, itemArea.Top + SY(6), SX(8), Math.Max(1, itemArea.Height - SY(12)));
                    x += SX(9);
                    continue;
                }

                bool stackable = compactLayout && IsCompactStackable(item);
                if (stackable)
                {
                    if (usedHeight > 0 && usedHeight + gapY + natural.Height > itemArea.Height)
                    {
                        x += columnWidth + gapX;
                        usedHeight = 0;
                        columnWidth = 0;
                    }
                    int top = itemArea.Top + usedHeight;
                    item.Bounds = new Rectangle(x, top, natural.Width, natural.Height);
                    usedHeight += (usedHeight == 0 ? 0 : gapY) + natural.Height;
                    if (usedHeight > natural.Height)
                        item.Bounds = new Rectangle(x, top + gapY, natural.Width, natural.Height);
                    columnWidth = Math.Max(columnWidth, natural.Width);
                    continue;
                }

                if (usedHeight > 0)
                {
                    x += columnWidth + gapX;
                    usedHeight = 0;
                    columnWidth = 0;
                }
                item.Bounds = new Rectangle(x, itemArea.Top, natural.Width, itemArea.Height);
                x += natural.Width;
            }
        }

        private Size GetNaturalItemSize(RibbonItem item, int availableHeight, bool compact)
        {
            RibbonHostedItem hosted = item as RibbonHostedItem;
            if (hosted != null)
            {
                int width = SX(hosted.ControlWidth + 10);
                if (!(hosted is RibbonCheckBox) && hosted.LabelPosition == RibbonFieldLabelPosition.Left)
                    width += SX(hosted.LabelWidth + 5);
                int height = compact && IsCompactStackable(item) ? Math.Min(SY(29), availableHeight) : availableHeight;
                return new Size(width, height);
            }
            if (item is RibbonLabel)
                return new Size(Math.Max(SX(60), TextRenderer.MeasureText(item.Text, Font).Width + SX(16)), compact ? Math.Min(SY(29), availableHeight) : availableHeight);
            if (item is RibbonSeparator)
                return new Size(SX(9), availableHeight);
            RibbonButton button = item as RibbonButton;
            bool small = button != null && IsButtonSmall(button);
            int semanticPadding = item.Tone == RibbonItemTone.Neutral
                ? 0
                : Math.Max(SX(12), SX(appearance.ContentPadding * 2));
            int buttonWidth = small
                ? Math.Max(SX(74) + semanticPadding, TextRenderer.MeasureText(item.Text, Font).Width + SX(36) + semanticPadding)
                : Math.Max(SX(64) + semanticPadding, TextRenderer.MeasureText(item.Text, Font).Width + SX(18) + semanticPadding);
            return new Size(buttonWidth, compact && small ? Math.Min(SY(29), availableHeight) : availableHeight);
        }

        private bool IsCompactStackable(RibbonItem item)
        {
            RibbonButton button = item as RibbonButton;
            if (button != null)
                return IsButtonSmall(button);
            RibbonHostedItem hosted = item as RibbonHostedItem;
            if (hosted != null)
                return hosted is RibbonCheckBox || hosted.LabelPosition != RibbonFieldLabelPosition.Top;
            return item is RibbonLabel;
        }

        private void DrawGroup(Graphics graphics, RibbonGroup group, Rectangle bounds)
        {
            DrawGroupCard(graphics, bounds, false);
            int designButtonWidth = IsDesignerHosted ? SX(22) : 0;
            Rectangle label = new Rectangle(bounds.Left + SX(4), bounds.Bottom - SY(22),
                Math.Max(1, bounds.Width - SX(8) - designButtonWidth), SY(18));
            TextRenderer.DrawText(graphics, group.Text, Font, label, appearance.MutedTextColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (!appearance.ShowGroupCards)
                using (Pen separator = new Pen(appearance.BorderColor))
                    graphics.DrawLine(separator, bounds.Right, bounds.Top + SY(4), bounds.Right, bounds.Bottom - SY(4));

            if (IsDesignerHosted)
            {
                group.AddItemBounds = new Rectangle(bounds.Right - SX(22), bounds.Bottom - SY(23), SX(18), SY(18));
                DrawInsertButton(graphics, group.AddItemBounds, "+", false);
            }
            else
            {
                group.AddItemBounds = Rectangle.Empty;
            }

            Rectangle itemArea = new Rectangle(bounds.Left + SX(5), bounds.Top + SY(1), bounds.Width - SX(10), bounds.Height - SY(27));
            LayoutGroupItems(group, itemArea);
            foreach (RibbonItem item in group.Items)
            {
                if (item is RibbonSeparator)
                {
                    using (Pen stroke = new Pen(appearance.BorderColor))
                        graphics.DrawLine(stroke, item.Bounds.Left + item.Bounds.Width / 2,
                            item.Bounds.Top, item.Bounds.Left + item.Bounds.Width / 2, item.Bounds.Bottom);
                    continue;
                }

                RibbonHostedItem hostedItem = item as RibbonHostedItem;
                if (hostedItem != null)
                {
                    DrawHostedItem(graphics, hostedItem, item.Bounds);
                    continue;
                }

                if (item is RibbonLabel)
                {
                    TextRenderer.DrawText(graphics, item.Text, Font, item.Bounds,
                        item.Enabled ? appearance.TextColor : appearance.MutedTextColor,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    continue;
                }

                RibbonButton button = item as RibbonButton;
                bool small = button != null && IsButtonSmall(button);
                DrawItem(graphics, item, item.Bounds, small);
            }
        }

        private void DrawGroupCard(Graphics graphics, Rectangle bounds, bool raised)
        {
            if (!appearance.ShowGroupCards)
                return;
            Rectangle card = bounds;
            card.Width = Math.Max(1, card.Width - SX(2));
            card.Height = Math.Max(1, card.Height - Math.Max(0, SY(appearance.ShadowDepth)));
            if (appearance.ShadowDepth > 0)
            {
                Rectangle shadow = card;
                shadow.Offset(0, SY(appearance.ShadowDepth));
                FillRoundedRectangle(graphics, appearance.ShadowColor, shadow, SX(appearance.GroupCornerRadius));
            }
            FillRoundedRectangle(graphics, raised ? appearance.RaisedColor : appearance.GroupSurfaceColor,
                card, SX(appearance.GroupCornerRadius));
            DrawRoundedRectangle(graphics, appearance.GroupBorderColor, card, SX(appearance.GroupCornerRadius));
        }

        private void DrawHostedItem(Graphics graphics, RibbonHostedItem item, Rectangle bounds)
        {
            Color textColor = item.Enabled ? appearance.TextColor : appearance.MutedTextColor;
            RibbonCheckBox checkItem = item as RibbonCheckBox;
            Rectangle controlBounds = GetHostedControlBounds(item, bounds);

            if (checkItem == null)
            {
                Rectangle labelBounds = GetHostedLabelBounds(item, bounds);
                if (!labelBounds.IsEmpty)
                    TextRenderer.DrawText(graphics, item.Text, Font, labelBounds, appearance.MutedTextColor,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }

            if (!IsDesignerHosted)
                return;

            if (checkItem != null)
            {
                Rectangle box = new Rectangle(controlBounds.Left + SX(2), controlBounds.Top + SY(5), SX(15), SY(15));
                graphics.FillRectangle(Brushes.White, box);
                using (Pen stroke = new Pen(appearance.StrongBorderColor)) graphics.DrawRectangle(stroke, box);
                if (checkItem.Checked)
                    TextRenderer.DrawText(graphics, "✓", Font, box, appearance.AccentColor,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                TextRenderer.DrawText(graphics, checkItem.Text, Font,
                    new Rectangle(box.Right + SX(6), controlBounds.Top, controlBounds.Width - SX(24), controlBounds.Height),
                    textColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                return;
            }

            FillRoundedRectangle(graphics, Color.White, controlBounds, SX(3));
            DrawRoundedRectangle(graphics, appearance.StrongBorderColor, controlBounds, SX(3));
            string preview = string.Empty;
            RibbonTextBox text = item as RibbonTextBox;
            RibbonComboBox combo = item as RibbonComboBox;
            RibbonDatePicker date = item as RibbonDatePicker;
            RibbonNumericUpDown numeric = item as RibbonNumericUpDown;
            if (text != null) preview = text.Value;
            else if (combo != null) preview = combo.SelectedText;
            else if (date != null) preview = date.Format == DateTimePickerFormat.Long ? date.Value.ToLongDateString() : date.Value.ToShortDateString();
            else if (numeric != null) preview = numeric.Value.ToString("F" + numeric.DecimalPlaces);

            TextRenderer.DrawText(graphics, preview, Font,
                new Rectangle(controlBounds.Left + SX(7), controlBounds.Top, controlBounds.Width - SX(22), controlBounds.Height),
                textColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            if (combo != null || date != null || numeric != null)
                TextRenderer.DrawText(graphics, combo != null ? "▾" : numeric != null ? "↕" : "▣", Font,
                    new Rectangle(controlBounds.Right - SX(20), controlBounds.Top, SX(17), controlBounds.Height),
                    appearance.MutedTextColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private Rectangle GetHostedControlBounds(RibbonHostedItem item, Rectangle bounds)
        {
            if (item is RibbonCheckBox)
                return new Rectangle(bounds.Left + SX(5), bounds.Top + Math.Max(0, (bounds.Height - SY(26)) / 2), Math.Max(SX(20), bounds.Width - SX(10)), SY(26));
            if (item.LabelPosition == RibbonFieldLabelPosition.Left)
                return new Rectangle(bounds.Left + SX(item.LabelWidth + 10), bounds.Top + Math.Max(0, (bounds.Height - SY(27)) / 2),
                    Math.Max(SX(20), bounds.Width - SX(item.LabelWidth + 15)), SY(27));
            if (item.LabelPosition == RibbonFieldLabelPosition.Hidden)
                return new Rectangle(bounds.Left + SX(5), bounds.Top + Math.Max(0, (bounds.Height - SY(27)) / 2), Math.Max(SX(20), bounds.Width - SX(10)), SY(27));
            return new Rectangle(bounds.Left + SX(5), bounds.Top + SY(24), Math.Max(SX(20), bounds.Width - SX(10)), SY(27));
        }

        private Rectangle GetHostedLabelBounds(RibbonHostedItem item, Rectangle bounds)
        {
            if (item.LabelPosition == RibbonFieldLabelPosition.Hidden)
                return Rectangle.Empty;
            if (item.LabelPosition == RibbonFieldLabelPosition.Left)
                return new Rectangle(bounds.Left + SX(5), bounds.Top, SX(item.LabelWidth), bounds.Height);
            return new Rectangle(bounds.Left + SX(5), bounds.Top + SY(1), Math.Max(1, bounds.Width - SX(10)), SY(19));
        }

        private void DrawItem(Graphics graphics, RibbonItem item, Rectangle bounds, bool small)
        {
            RibbonToggleButton toggle = item as RibbonToggleButton;
            float hover = GetAnimationValue(itemHoverAnimations, item, item == hotItem ? 1F : 0F);
            float press = GetAnimationValue(itemPressAnimations, item, item == pressedItem ? 1F : 0F);
            float toggleProgress = 0F;
            if (toggle != null)
            {
                if (!toggleAnimations.TryGetValue(toggle, out toggleProgress))
                {
                    toggleProgress = toggle.Checked ? 1F : 0F;
                    toggleAnimations[toggle] = toggleProgress;
                }
            }
            observedBadges[item] = item.BadgeText;
            Color tone = GetToneColor(item.Tone);
            bool semantic = item.Tone != RibbonItemTone.Neutral;
            if (semantic)
                FillRoundedRectangle(graphics, Blend(appearance.GroupSurfaceColor, tone, 0.10F), bounds, SX(appearance.CornerRadius));
            if (toggleProgress > 0F)
                FillRoundedRectangle(graphics, Blend(appearance.GroupSurfaceColor,
                    Blend(appearance.SelectionColor, tone, semantic ? 0.15F : 0F), toggleProgress), bounds, SX(appearance.CornerRadius));
            if (hover > 0F)
                FillRoundedRectangle(graphics, Blend(appearance.GroupSurfaceColor,
                    semantic ? tone : appearance.AccentColor, 0.05F + 0.15F * hover), bounds, SX(appearance.CornerRadius));
            if (press > 0F)
                FillRoundedRectangle(graphics, Blend(appearance.GroupSurfaceColor,
                    Blend(appearance.PressedColor, tone, semantic ? 0.22F : 0F), 0.35F + press * 0.65F),
                    bounds, SX(appearance.CornerRadius));

            if (hover > 0F)
                DrawRoundedRectangle(graphics, Blend(appearance.StrongBorderColor,
                    semantic ? tone : appearance.AccentColor, 0.18F + 0.32F * hover), bounds, SX(appearance.CornerRadius));

            DrawRipple(graphics, item, bounds);

            Color color = item.Enabled ? appearance.TextColor : appearance.MutedTextColor;
            Color iconColor = item.IconColor.IsEmpty ? (semantic && item.Enabled ? tone : color) : item.IconColor;
            int semanticInset = semantic ? Math.Max(SX(6), SX(appearance.ContentPadding - 2)) : 0;
            if (item.Image != null)
            {
                Rectangle imageBounds = small
                    ? new Rectangle(bounds.Left + SX(8) + semanticInset, bounds.Top + (bounds.Height - SY(20)) / 2, SX(20), SY(20))
                    : new Rectangle(bounds.Left + (bounds.Width - SX(32)) / 2, bounds.Top + SY(5), SX(32), SY(32));
                graphics.DrawImage(item.Image, imageBounds);
            }
            else if (!string.IsNullOrEmpty(item.IconKey))
            {
                int iconSize = SX(small ? appearance.IconSize : appearance.IconSize + 12);
                Rectangle iconBounds = small
                    ? new Rectangle(bounds.Left + SX(appearance.ItemSpacing + 3) + semanticInset, bounds.Top + (bounds.Height - iconSize) / 2, iconSize, iconSize)
                    : new Rectangle(bounds.Left + (bounds.Width - iconSize) / 2, bounds.Top + SY(appearance.ItemSpacing), iconSize, iconSize);
                FluentIconCatalog.TryDraw(graphics, item.IconKey, iconBounds, iconColor, iconSize - SX(small ? 2F : 3F));
            }

            RibbonDropDownButton dropDown = item as RibbonDropDownButton;
            int arrowSpace = dropDown == null ? 0 : SY(18);
            Rectangle textBounds = small
                ? new Rectangle(bounds.Left + SX(32) + semanticInset, bounds.Top,
                    Math.Max(1, bounds.Width - SX(36) - arrowSpace - semanticInset * 2), bounds.Height)
                : new Rectangle(bounds.Left + SX(4) + semanticInset, bounds.Top + SY(38),
                    Math.Max(1, bounds.Width - SX(8) - semanticInset * 2),
                    Math.Max(SY(16), bounds.Height - SY(38) - arrowSpace));
            TextFormatFlags align = small ? TextFormatFlags.Left : TextFormatFlags.HorizontalCenter;
            TextRenderer.DrawText(graphics, item.Text, Font, textBounds, color,
                align | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.WordBreak);

            if (dropDown != null)
            {
                Rectangle arrowBounds = GetDropDownArrowBounds(dropDown);
                if (dropDown is RibbonSplitButton)
                {
                    using (Pen divider = new Pen(appearance.BorderColor))
                    {
                        if (small)
                            graphics.DrawLine(divider, arrowBounds.Left, arrowBounds.Top + SY(4), arrowBounds.Left, arrowBounds.Bottom - SY(4));
                        else
                            graphics.DrawLine(divider, arrowBounds.Left + SX(5), arrowBounds.Top, arrowBounds.Right - SX(5), arrowBounds.Top);
                    }
                }
                TextRenderer.DrawText(graphics, "▾", Font, arrowBounds, color,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            if (!string.IsNullOrEmpty(item.BadgeText))
            {
                float badgeProgress;
                if (!badgeAnimations.TryGetValue(item, out badgeProgress)) badgeProgress = 1F;
                DrawBadge(graphics, item, bounds, tone, badgeProgress);
            }
            if (item.Busy)
                DrawBusyIndicator(graphics, bounds, tone);
            if (Focused && keyboardItem == item)
                DrawRoundedRectangle(graphics, appearance.AccentColor, Rectangle.Inflate(bounds, -SX(2), -SY(2)),
                    SX(appearance.CornerRadius));
        }

        private static float GetAnimationValue(Dictionary<RibbonItem, float> animations,
            RibbonItem item, float defaultValue)
        {
            float value;
            return animations.TryGetValue(item, out value) ? value : defaultValue;
        }

        private void DrawRipple(Graphics graphics, RibbonItem item, Rectangle bounds)
        {
            if (item != rippleItem || rippleProgress >= 1F || !appearance.EnableRipples)
                return;
            float eased = 1F - (float)Math.Pow(1F - rippleProgress, 3F);
            float maxRadius = (float)Math.Sqrt(bounds.Width * bounds.Width + bounds.Height * bounds.Height);
            float radius = Math.Max(SX(4F), maxRadius * eased);
            int alpha = (int)(52F * (1F - rippleProgress));
            GraphicsState state = graphics.Save();
            using (GraphicsPath clip = RoundedRectangle(bounds, SX(appearance.CornerRadius)))
            using (SolidBrush ripple = new SolidBrush(Color.FromArgb(alpha, appearance.AccentColor)))
            {
                graphics.SetClip(clip, CombineMode.Intersect);
                graphics.FillEllipse(ripple, rippleOrigin.X - radius, rippleOrigin.Y - radius,
                    radius * 2F, radius * 2F);
            }
            graphics.Restore(state);
        }

        private void DrawBadge(Graphics graphics, RibbonItem item, Rectangle bounds, Color tone, float progress)
        {
            if (progress <= 0.04F)
                return;
            Color badge = item.BadgeColor.IsEmpty
                ? (item.Tone == RibbonItemTone.Neutral ? appearance.DangerColor : tone)
                : item.BadgeColor;
            using (Font badgeFont = new Font(Font.FontFamily, 7F, FontStyle.Bold))
            {
                Size textSize = TextRenderer.MeasureText(item.BadgeText, badgeFont);
                int width = Math.Max(SX(16), textSize.Width + SX(6));
                Rectangle badgeBounds = new Rectangle(bounds.Right - width - SX(2), bounds.Top + SY(2), width, SY(16));
                if (progress < 1F)
                {
                    float shifted = progress - 1F;
                    float scale = 1F + 2.70158F * shifted * shifted * shifted +
                        1.70158F * shifted * shifted;
                    int scaledWidth = Math.Max(1, (int)Math.Round(width * scale));
                    int scaledHeight = Math.Max(1, (int)Math.Round(SY(16) * scale));
                    Point center = new Point(badgeBounds.Left + badgeBounds.Width / 2,
                        badgeBounds.Top + badgeBounds.Height / 2);
                    badgeBounds = new Rectangle(center.X - scaledWidth / 2, center.Y - scaledHeight / 2,
                        scaledWidth, scaledHeight);
                }
                FillRoundedRectangle(graphics, badge, badgeBounds, SX(8));
                TextRenderer.DrawText(graphics, item.BadgeText, badgeFont, badgeBounds, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
        }

        private void DrawBusyIndicator(Graphics graphics, Rectangle bounds, Color tone)
        {
            Rectangle spinner = new Rectangle(bounds.Right - SX(18), bounds.Top + SY(3), SX(13), SY(13));
            Color color = tone == appearance.TextColor ? appearance.AccentColor : tone;
            using (Pen pen = new Pen(color, SX(2F)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                graphics.DrawArc(pen, spinner, busyAngle, 250F);
            }
        }

        private Color GetToneColor(RibbonItemTone tone)
        {
            switch (tone)
            {
                case RibbonItemTone.Primary: return appearance.AccentColor;
                case RibbonItemTone.Success: return appearance.SuccessColor;
                case RibbonItemTone.Warning: return appearance.WarningColor;
                case RibbonItemTone.Danger: return appearance.DangerColor;
                case RibbonItemTone.Information: return appearance.InformationColor;
                default: return appearance.TextColor;
            }
        }

        private Rectangle GetDropDownArrowBounds(RibbonDropDownButton button)
        {
            bool small = IsButtonSmall(button);
            return small
                ? new Rectangle(button.Bounds.Right - SX(18), button.Bounds.Top, SX(18), button.Bounds.Height)
                : new Rectangle(button.Bounds.Left, button.Bounds.Bottom - SY(18), button.Bounds.Width, SY(18));
        }

        private bool IsButtonSmall(RibbonButton button)
        {
            return button.ItemSize == RibbonItemSize.Small || responsiveSmallButtons.Contains(button);
        }

        private void ResetItemBounds()
        {
            foreach (RibbonTab tab in tabs)
                foreach (RibbonGroup group in tab.Groups)
                {
                    group.Bounds = Rectangle.Empty;
                    group.AddItemBounds = Rectangle.Empty;
                    foreach (RibbonItem item in group.Items)
                        item.Bounds = Rectangle.Empty;
                }
        }

        private RibbonTab GetSelectedTab()
        {
            if (tabs.Count == 0)
                return null;
            if (selectedTabIndex < 0 || selectedTabIndex >= tabs.Count)
                selectedTabIndex = 0;
            return tabs[selectedTabIndex];
        }

        private Rectangle ApplicationButtonBounds
        {
            get
            {
                int width = Math.Max(SX(110), TextRenderer.MeasureText(applicationButtonText, Font).Width + SX(44));
                return new Rectangle(SX(8), CaptionHeight + SY(3), width, TabHeight - SY(6));
            }
        }

        private int HitTestTab(Point point)
        {
            for (int index = 0; index < tabs.Count; index++)
                if (GetTabBounds(index).Contains(point)) return index;
            return -1;
        }

        private Rectangle GetTabBounds(int modelIndex)
        {
            if (modelIndex < 0 || modelIndex >= tabs.Count || !tabs[modelIndex].Visible)
                return Rectangle.Empty;
            int x = ApplicationButtonBounds.Right + SX(8);
            for (int index = 0; index < tabs.Count; index++)
            {
                RibbonTab tab = tabs[index];
                if (!tab.Visible) continue;
                int width = Math.Max(SX(72), TextRenderer.MeasureText(tab.Text, Font).Width + SX(26));
                Rectangle bounds = new Rectangle(x, CaptionHeight + SY(2), width, TabHeight - SY(4));
                if (index == modelIndex) return bounds;
                x += width + SX(2);
            }
            return Rectangle.Empty;
        }

        private int FindVisibleTab(int start, int direction)
        {
            int index = start + direction;
            while (index >= 0 && index < tabs.Count)
            {
                if (tabs[index].Visible) return index;
                index += direction;
            }
            return start >= 0 && start < tabs.Count && tabs[start].Visible ? start : -1;
        }

        private void SetKeyboardTabIndex(int value)
        {
            if (keyboardTabIndex == value) return;
            keyboardTabIndex = value;
            NotifyAccessibleChild(AccessibleEvents.Focus, keyboardTabIndex);
            Invalidate(new Rectangle(0, CaptionHeight, Width, TabHeight));
        }

        private void NotifyAccessibleChild(AccessibleEvents accessibleEvent, int modelIndex)
        {
            if (!SuppressAccessibilityInterop && IsHandleCreated && modelIndex >= 0 && modelIndex < tabs.Count)
                AccessibilityNotifyClients(accessibleEvent, modelIndex + 1);
        }

        private bool IsAccessibleGroup(RibbonTab tab, RibbonGroup group)
        {
            return tab != null && tab.Visible && tab == GetSelectedTab() && !overflowGroups.Contains(group) &&
                !group.Bounds.IsEmpty && ClientRectangle.IntersectsWith(group.Bounds);
        }

        private List<RibbonGroup> GetAccessibleGroups(RibbonTab tab)
        {
            List<RibbonGroup> result = new List<RibbonGroup>();
            if (tab == null) return result;
            foreach (RibbonGroup group in tab.Groups)
                if (IsAccessibleGroup(tab, group)) result.Add(group);
            return result;
        }

        private bool IsAccessibleItem(RibbonGroup group, RibbonItem item)
        {
            if (group == null || collapsedGroups.Contains(group) || overflowGroups.Contains(group) ||
                item is RibbonHostedItem || item.Bounds.IsEmpty) return false;
            if (!(item is RibbonButton) && !(item is RibbonLabel) && !(item is RibbonSeparator)) return false;
            Rectangle bounds = Rectangle.Intersect(group.Bounds, item.Bounds);
            return !bounds.IsEmpty && ClientRectangle.IntersectsWith(bounds);
        }

        private List<RibbonItem> GetAccessibleItems(RibbonGroup group)
        {
            List<RibbonItem> result = new List<RibbonItem>();
            if (group == null) return result;
            foreach (RibbonItem item in group.Items)
                if (IsAccessibleItem(group, item)) result.Add(item);
            return result;
        }

        private Rectangle GetAccessibleScreenBounds(Rectangle bounds)
        {
            if (!IsHandleCreated || IsDisposed || !Visible) return Rectangle.Empty;
            bounds = Rectangle.Intersect(ClientRectangle, bounds);
            return bounds.IsEmpty ? Rectangle.Empty : RectangleToScreen(bounds);
        }

        private List<object> GetKeyboardTargets()
        {
            List<object> result = new List<object>();
            RibbonTab tab = GetSelectedTab();
            foreach (RibbonGroup group in GetAccessibleGroups(tab))
            {
                if (collapsedGroups.Contains(group))
                {
                    result.Add(group);
                    continue;
                }
                foreach (RibbonItem item in GetAccessibleItems(group))
                    if (item is RibbonButton && item.Enabled) result.Add(item);
            }
            return result;
        }

        private object KeyboardTarget
        {
            get { return keyboardItem != null ? (object)keyboardItem : keyboardGroup; }
        }

        private void SetKeyboardTarget(object target)
        {
            Rectangle dirty = Rectangle.Empty;
            if (keyboardItem != null) dirty = keyboardItem.Bounds;
            else if (keyboardGroup != null) dirty = keyboardGroup.Bounds;
            keyboardItem = target as RibbonItem;
            keyboardGroup = keyboardItem == null ? target as RibbonGroup : FindOwningGroup(GetSelectedTab(), keyboardItem);
            Rectangle next = keyboardItem != null ? keyboardItem.Bounds : keyboardGroup == null ? Rectangle.Empty : keyboardGroup.Bounds;
            if (dirty.IsEmpty) { if (!next.IsEmpty) Invalidate(next); }
            else if (next.IsEmpty) Invalidate(dirty);
            else Invalidate(Rectangle.Union(dirty, next));
        }

        private void MoveKeyboardTarget(int direction)
        {
            List<object> targets = GetKeyboardTargets();
            if (targets.Count == 0) { SetKeyboardTarget(null); return; }
            int current = targets.IndexOf(KeyboardTarget);
            int next = current < 0 ? (direction > 0 ? 0 : targets.Count - 1) : current + direction;
            next = Math.Max(0, Math.Min(targets.Count - 1, next));
            SetKeyboardTarget(targets[next]);
        }

        private void PerformAccessibleTarget(object target, bool openPopup)
        {
            RibbonGroup group = target as RibbonGroup;
            if (group != null)
            {
                if (collapsedGroups.Contains(group)) ShowCollapsedGroupMenu(group);
                return;
            }
            RibbonItem item = target as RibbonItem;
            if (item == null || !item.Enabled) return;
            RibbonSplitButton split = item as RibbonSplitButton;
            RibbonDropDownButton dropDown = item as RibbonDropDownButton;
            RibbonButton button = item as RibbonButton;
            if (split != null && !openPopup) split.PerformClick();
            else if (dropDown != null) ShowDropDownMenu(dropDown);
            else if (button != null) button.PerformClick();
        }

        private RibbonItem HitTestItem(Point point)
        {
            RibbonTab selected = GetSelectedTab();
            if (selected == null)
                return null;
            foreach (RibbonGroup group in selected.Groups)
            {
                if (collapsedGroups.Contains(group) || overflowGroups.Contains(group))
                    continue;
                foreach (RibbonItem item in group.Items)
                    if (!(item is RibbonSeparator) && item.Bounds.Contains(point))
                        return item;
            }
            return null;
        }

        private RibbonGroup HitTestCollapsedGroup(Point point)
        {
            RibbonTab selected = GetSelectedTab();
            if (selected == null)
                return null;
            foreach (RibbonGroup group in selected.Groups)
                if (collapsedGroups.Contains(group) && group.Bounds.Contains(point))
                    return group;
            return null;
        }

        internal bool HitTestAddTab(Point point)
        {
            return !addTabBounds.IsEmpty && addTabBounds.Contains(point);
        }

        internal bool HitTestAddGroup(Point point)
        {
            return !addGroupBounds.IsEmpty && addGroupBounds.Contains(point);
        }

        internal RibbonGroup HitTestAddItem(Point point)
        {
            RibbonTab selected = GetSelectedTab();
            if (selected == null)
                return null;
            foreach (RibbonGroup group in selected.Groups)
                if (!group.AddItemBounds.IsEmpty && group.AddItemBounds.Contains(point))
                    return group;
            return null;
        }

        internal object HitTestDesignElement(Point point)
        {
            RibbonItem item = HitTestDesignItem(point);
            if (item != null)
                return item;

            RibbonTab selected = GetSelectedTab();
            if (selected != null)
                foreach (RibbonGroup group in selected.Groups)
                    if (group.Bounds.Contains(point))
                        return group;

            foreach (RibbonTab tab in tabs)
                if (tab.Bounds.Contains(point))
                    return tab;
            return null;
        }

        private RibbonItem HitTestDesignItem(Point point)
        {
            RibbonTab selected = GetSelectedTab();
            if (selected == null)
                return null;

            // Keep the selected element easy to grab while moving or resizing overlapping
            // custom layouts. This is especially important for the narrow split/arrow area.
            RibbonItem selectedItem = designSelectedObject as RibbonItem;
            if (selectedItem != null && GetDesignHitBounds(selectedItem).Contains(point))
                return selectedItem;

            for (int groupIndex = selected.Groups.Count - 1; groupIndex >= 0; groupIndex--)
            {
                RibbonGroup group = selected.Groups[groupIndex];
                if (collapsedGroups.Contains(group) || overflowGroups.Contains(group))
                    continue;
                for (int itemIndex = group.Items.Count - 1; itemIndex >= 0; itemIndex--)
                {
                    RibbonItem candidate = group.Items[itemIndex];
                    if (GetDesignHitBounds(candidate).Contains(point))
                        return candidate;
                }
            }
            return null;
        }

        internal Rectangle GetDesignHitBounds(RibbonItem item)
        {
            if (item == null || item.Bounds.IsEmpty)
                return Rectangle.Empty;
            Rectangle hit = item.Bounds;
            int minimumWidth = item is RibbonSeparator ? 18 : item is RibbonSplitButton ? 48 : 28;
            int minimumHeight = item is RibbonSplitButton ? 34 : 26;
            if (hit.Width < minimumWidth)
            {
                int extra = minimumWidth - hit.Width;
                hit.X -= extra / 2;
                hit.Width = minimumWidth;
            }
            if (hit.Height < minimumHeight)
            {
                int extra = minimumHeight - hit.Height;
                hit.Y -= extra / 2;
                hit.Height = minimumHeight;
            }
            if (item is RibbonSplitButton)
                hit.Inflate(5, 4);
            return Rectangle.Intersect(ClientRectangle, hit);
        }

        internal object HitTestDesignDelete(Point point)
        {
            return !designDeleteBounds.IsEmpty && designDeleteBounds.Contains(point)
                ? designSelectedObject
                : null;
        }

        internal object HitTestDesignMore(Point point)
        {
            return !designMoreBounds.IsEmpty && designMoreBounds.Contains(point)
                ? designSelectedObject
                : null;
        }

        internal object HitTestDesignMoveLeft(Point point)
        {
            return !designMoveLeftBounds.IsEmpty && designMoveLeftBounds.Contains(point)
                ? designSelectedObject
                : null;
        }

        internal object HitTestDesignMoveRight(Point point)
        {
            return !designMoveRightBounds.IsEmpty && designMoveRightBounds.Contains(point)
                ? designSelectedObject
                : null;
        }

        internal RibbonItem HitTestDesignResize(Point point)
        {
            return !designResizeBounds.IsEmpty && designResizeBounds.Contains(point)
                ? designSelectedObject as RibbonItem
                : null;
        }

        internal void SetDesignGrid(RibbonGroup group, bool visible)
        {
            if (designGridGroup == group && designGridVisible == visible)
                return;

            designGridGroup = group;
            designGridVisible = visible;
            Invalidate();
        }

        private void DrawDesignGrid(Graphics graphics)
        {
            if (!IsDesignerHosted || !designGridVisible || designGridGroup == null || designGridGroup.Bounds.IsEmpty)
                return;

            Rectangle bounds = Rectangle.Intersect(ClientRectangle, designGridGroup.Bounds);
            bounds.Inflate(-5, -2);
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return;

            int gridX = Math.Max(2, SX(4));
            int gridY = Math.Max(2, SY(4));
            using (Pen gridPen = new Pen(Color.FromArgb(92, appearance.AccentColor), 1F))
            {
                gridPen.DashStyle = DashStyle.Dot;
                for (int x = bounds.Left; x <= bounds.Right; x += gridX)
                    graphics.DrawLine(gridPen, x, bounds.Top, x, bounds.Bottom);
                for (int y = bounds.Top; y <= bounds.Bottom; y += gridY)
                    graphics.DrawLine(gridPen, bounds.Left, y, bounds.Right, y);
            }
        }

        private void DrawDesignSelection(Graphics graphics)
        {
            if (!IsDesignerHosted || designSelectedObject == null)
            {
                designDeleteBounds = Rectangle.Empty;
                designResizeBounds = Rectangle.Empty;
                designMoreBounds = Rectangle.Empty;
                designMoveLeftBounds = Rectangle.Empty;
                designMoveRightBounds = Rectangle.Empty;
                designToolbarBounds = Rectangle.Empty;
                return;
            }

            Rectangle bounds = Rectangle.Empty;
            RibbonTab tab = designSelectedObject as RibbonTab;
            RibbonGroup group = designSelectedObject as RibbonGroup;
            RibbonItem item = designSelectedObject as RibbonItem;
            if (tab != null)
                bounds = tab.Bounds;
            else if (group != null)
                bounds = group.Bounds;
            else if (item != null)
                bounds = item.Bounds;

            if (bounds.IsEmpty)
            {
                designDeleteBounds = Rectangle.Empty;
                designResizeBounds = Rectangle.Empty;
                designMoreBounds = Rectangle.Empty;
                designMoveLeftBounds = Rectangle.Empty;
                designMoveRightBounds = Rectangle.Empty;
                designToolbarBounds = Rectangle.Empty;
                return;
            }
            bounds.Inflate(-1, -1);
            using (Pen pen = new Pen(appearance.AccentColor, 1F))
            {
                pen.DashStyle = DashStyle.Dash;
                graphics.DrawRectangle(pen, bounds);
            }

            const int actionSize = 20;
            int toolbarWidth = actionSize * 4 + 6;
            int toolbarX = Math.Max(2, Math.Min(bounds.Left, Width - toolbarWidth - 2));
            int toolbarY = Math.Max(CaptionHeight + TabHeight, bounds.Top - actionSize - 3);
            designToolbarBounds = new Rectangle(toolbarX, toolbarY, toolbarWidth, actionSize + 4);
            Rectangle shadow = designToolbarBounds;
            shadow.Offset(0, 2);
            FillRoundedRectangle(graphics, appearance.ShadowColor, shadow, 6);
            FillRoundedRectangle(graphics, appearance.SurfaceColor, designToolbarBounds, 6);
            DrawRoundedRectangle(graphics, appearance.StrongBorderColor, designToolbarBounds, 6);

            int actionY = designToolbarBounds.Top + 2;
            designMoreBounds = new Rectangle(designToolbarBounds.Left + 3, actionY, actionSize, actionSize);
            designMoveLeftBounds = new Rectangle(designMoreBounds.Right, actionY, actionSize, actionSize);
            designMoveRightBounds = new Rectangle(designMoveLeftBounds.Right, actionY, actionSize, actionSize);
            designDeleteBounds = new Rectangle(designMoveRightBounds.Right, actionY, actionSize, actionSize);
            FillRoundedRectangle(graphics, appearance.RaisedColor, designMoreBounds, 4);
            TextRenderer.DrawText(graphics, "•••", Font, designMoreBounds, appearance.TextColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(graphics, "◀", Font, designMoveLeftBounds, appearance.MutedTextColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(graphics, "▶", Font, designMoveRightBounds, appearance.MutedTextColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            FillRoundedRectangle(graphics, Color.FromArgb(245, 225, 228), designDeleteBounds, 4);
            TextRenderer.DrawText(graphics, "×", Font, designDeleteBounds, appearance.DangerColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

            if (designSelectedObject is RibbonItem)
            {
                designResizeBounds = new Rectangle(bounds.Right - 6, bounds.Bottom - 6, 10, 10);
                using (SolidBrush handle = new SolidBrush(appearance.AccentColor))
                    graphics.FillRectangle(handle, designResizeBounds);
            }
            else
            {
                designResizeBounds = Rectangle.Empty;
            }
        }

        private void DrawInsertButton(Graphics graphics, Rectangle bounds, string text, bool outlined)
        {
            if (!outlined)
                FillRoundedRectangle(graphics, appearance.RaisedColor, bounds, Math.Min(appearance.CornerRadius, 5));
            DrawRoundedRectangle(graphics, appearance.StrongBorderColor, bounds, Math.Min(appearance.CornerRadius, 5));
            TextRenderer.DrawText(graphics, text, Font, bounds, appearance.MutedTextColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int previousTab = hotTabIndex;
            RibbonItem previousItem = hotItem;
            bool previousApplication = hotApplicationButton;
            hotTabIndex = HitTestTab(e.Location);
            hotItem = HitTestItem(e.Location);
            hotApplicationButton = ApplicationButtonBounds.Contains(e.Location);
            bool designInsert = IsDesignerHosted &&
                (HitTestAddTab(e.Location) || HitTestAddGroup(e.Location) || HitTestAddItem(e.Location) != null);
            bool designAction = IsDesignerHosted &&
                (HitTestDesignMore(e.Location) != null || HitTestDesignMoveLeft(e.Location) != null ||
                 HitTestDesignMoveRight(e.Location) != null || HitTestDesignDelete(e.Location) != null ||
                 HitTestDesignResize(e.Location) != null);
            bool interactive = hotTabIndex >= 0 || hotItem != null || hotApplicationButton || designInsert ||
                designAction || overflowBounds.Contains(e.Location) || HitTestCollapsedGroup(e.Location) != null;
            Cursor = interactive ? Cursors.Hand : Cursors.Default;

            if (previousTab != hotTabIndex || previousItem != hotItem || previousApplication != hotApplicationButton)
            {
                if (previousTab != hotTabIndex)
                {
                    if (previousTab >= 0 && previousTab < tabs.Count &&
                        !tabHoverAnimations.ContainsKey(tabs[previousTab]))
                        tabHoverAnimations[tabs[previousTab]] = 1F;
                    if (hotTabIndex >= 0 && hotTabIndex < tabs.Count &&
                        !tabHoverAnimations.ContainsKey(tabs[hotTabIndex]))
                        tabHoverAnimations[tabs[hotTabIndex]] = 0F;
                    StartMotion();
                }
                if (previousApplication != hotApplicationButton)
                    StartMotion();
                if (previousItem != hotItem)
                {
                    if (previousItem != null && !itemHoverAnimations.ContainsKey(previousItem))
                        itemHoverAnimations[previousItem] = 1F;
                    if (hotItem != null && !itemHoverAnimations.ContainsKey(hotItem))
                        itemHoverAnimations[hotItem] = appearance.MotionEnabled ? 0F : 1F;
                    StartMotion();
                }
                string tooltipText = string.Empty;
                if (hotItem != null)
                {
                    toolTip.ToolTipTitle = string.IsNullOrEmpty(hotItem.ToolTipTitle) ? hotItem.Text : hotItem.ToolTipTitle;
                    tooltipText = hotItem.ToolTipText;
                    if (!string.IsNullOrEmpty(hotItem.ShortcutText))
                        tooltipText += (string.IsNullOrEmpty(tooltipText) ? string.Empty : "\r\n") + "Atajo: " + hotItem.ShortcutText;
                }
                else
                    toolTip.ToolTipTitle = string.Empty;
                toolTip.SetToolTip(this, tooltipText);
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            RibbonItem previousItem = hotItem;
            int previousTab = hotTabIndex;
            hotTabIndex = -1;
            hotItem = null;
            hotApplicationButton = false;
            if (previousItem != null)
            {
                if (!itemHoverAnimations.ContainsKey(previousItem))
                    itemHoverAnimations[previousItem] = 1F;
                StartMotion();
            }
            if (previousTab >= 0 && previousTab < tabs.Count)
            {
                if (!tabHoverAnimations.ContainsKey(tabs[previousTab]))
                    tabHoverAnimations[tabs[previousTab]] = 1F;
                StartMotion();
            }
            if (applicationHoverProgress > 0F)
                StartMotion();
            Cursor = Cursors.Default;
            Invalidate();
        }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new RibbonControlAccessibleObject(this);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys key = keyData & Keys.KeyCode;
            return key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down ||
                base.IsInputKey(keyData);
        }

        protected override void OnGotFocus(EventArgs e)
        {
            base.OnGotFocus(e);
            int next = selectedTabIndex >= 0 && selectedTabIndex < tabs.Count && tabs[selectedTabIndex].Visible
                ? selectedTabIndex : FindVisibleTab(-1, 1);
            SetKeyboardTabIndex(next);
        }

        protected override void OnLostFocus(EventArgs e)
        {
            base.OnLostFocus(e);
            SetKeyboardTarget(null);
            SetKeyboardTabIndex(-1);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (KeyboardTarget != null)
            {
                if (e.Alt && e.KeyCode == Keys.Down) { PerformAccessibleTarget(KeyboardTarget, true); e.Handled = true; }
                else if (e.KeyCode == Keys.Left) { MoveKeyboardTarget(-1); e.Handled = true; }
                else if (e.KeyCode == Keys.Right) { MoveKeyboardTarget(1); e.Handled = true; }
                else if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Escape) { SetKeyboardTarget(null); e.Handled = true; }
                else if (e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space)
                {
                    PerformAccessibleTarget(KeyboardTarget, false);
                    e.Handled = true;
                }
            }
            else if (e.KeyCode == Keys.Left) { SetKeyboardTabIndex(FindVisibleTab(keyboardTabIndex, -1)); e.Handled = true; }
            else if (e.KeyCode == Keys.Right) { SetKeyboardTabIndex(FindVisibleTab(keyboardTabIndex, 1)); e.Handled = true; }
            else if (e.KeyCode == Keys.Home) { SetKeyboardTabIndex(FindVisibleTab(-1, 1)); e.Handled = true; }
            else if (e.KeyCode == Keys.End) { SetKeyboardTabIndex(FindVisibleTab(tabs.Count, -1)); e.Handled = true; }
            else if (e.KeyCode == Keys.Down) { MoveKeyboardTarget(1); e.Handled = KeyboardTarget != null; }
            else if ((e.KeyCode == Keys.Enter || e.KeyCode == Keys.Space) && keyboardTabIndex >= 0)
            {
                SelectedTabIndex = keyboardTabIndex;
                e.Handled = true;
            }
            if (e.Handled) e.SuppressKeyPress = true;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (IsDesignerHosted)
                return;
            if (e.Button != MouseButtons.Left)
                return;
            int tab = HitTestTab(e.Location);
            if (tab >= 0)
            {
                Focus();
                SetKeyboardTabIndex(tab);
            }
            pressedItem = HitTestItem(e.Location);
            if (pressedItem is RibbonButton)
            {
                Focus();
                SetKeyboardTarget(pressedItem);
            }
            if (pressedItem != null)
            {
                itemPressAnimations[pressedItem] = 0F;
                if (appearance.EnableRipples && appearance.MotionEnabled)
                {
                    rippleItem = pressedItem;
                    rippleOrigin = e.Location;
                    rippleProgress = 0F;
                }
                StartMotion();
            }
            pressedApplicationButton = ApplicationButtonBounds.Contains(e.Location);
            if (pressedApplicationButton)
                StartMotion();
            pressedOverflow = overflowBounds.Contains(e.Location);
            pressedCollapsedGroup = HitTestCollapsedGroup(e.Location);
            if (pressedCollapsedGroup != null)
            {
                Focus();
                SetKeyboardTarget(pressedCollapsedGroup);
            }
            if (pressedItem != null || pressedOverflow || pressedCollapsedGroup != null)
                Capture = true;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (IsDesignerHosted)
                return;
            if (e.Button != MouseButtons.Left)
                return;

            bool invokeOverflow = pressedOverflow && overflowBounds.Contains(e.Location);
            RibbonGroup collapsed = pressedCollapsedGroup != null && pressedCollapsedGroup.Bounds.Contains(e.Location)
                ? pressedCollapsedGroup : null;

            RibbonButton button = pressedItem as RibbonButton;
            if (button != null && button == HitTestItem(e.Location) && button.Enabled)
            {
                RibbonDropDownButton dropDown = button as RibbonDropDownButton;
                if (dropDown == null)
                    button.PerformClick();
                else if (dropDown is RibbonSplitButton && !GetDropDownArrowBounds(dropDown).Contains(e.Location))
                    button.PerformClick();
                else
                    ShowDropDownMenu(dropDown);
            }
            pressedItem = null;
            StartMotion();
            pressedOverflow = false;
            pressedCollapsedGroup = null;
            bool invokeApplication = pressedApplicationButton && ApplicationButtonBounds.Contains(e.Location);
            pressedApplicationButton = false;
            StartMotion();
            Capture = false;

            int tab = HitTestTab(e.Location);
            if (tab >= 0)
                SelectedTabIndex = tab;
            else if (invokeOverflow)
                ShowOverflowMenu();
            else if (collapsed != null)
                ShowCollapsedGroupMenu(collapsed);
            else if (invokeApplication && ApplicationButtonClick != null)
                ApplicationButtonClick(this, EventArgs.Empty);
            Invalidate();
        }

        private void ShowDropDownMenu(RibbonDropDownButton button)
        {
            button.OnDropDownOpening();
            if (activeDropDownMenu != null)
                activeDropDownMenu.Dispose();
            activeDropDownMenu = CreateRibbonMenu();
            foreach (RibbonMenuItem model in button.Items)
            {
                RibbonMenuItem current = model;
                if (current.IsSeparator)
                {
                    activeDropDownMenu.Items.Add(new ToolStripSeparator());
                    continue;
                }
                ToolStripMenuItem menuItem = new ToolStripMenuItem(current.Text)
                {
                    Enabled = current.Enabled,
                    Checked = current.Checked,
                    ShortcutKeyDisplayString = current.ShortcutText,
                    ForeColor = appearance.TextColor
                };
                if (!string.IsNullOrEmpty(current.IconKey))
                    menuItem.Image = CreateMenuIcon(current.IconKey, appearance.TextColor);
                menuItem.Click += delegate { current.PerformClick(); };
                activeDropDownMenu.Items.Add(menuItem);
            }
            if (activeDropDownMenu.Items.Count == 0)
                activeDropDownMenu.Items.Add(new ToolStripMenuItem("Sin opciones") { Enabled = false });
            activeDropDownMenu.Show(this, new Point(button.Bounds.Left, button.Bounds.Bottom + 2));
        }

        private void ShowCollapsedGroupMenu(RibbonGroup group)
        {
            if (activeDropDownMenu != null)
                activeDropDownMenu.Dispose();
            activeDropDownMenu = CreateRibbonMenu();
            AddGroupMenuItems(activeDropDownMenu.Items, group);
            activeDropDownMenu.Show(this, new Point(group.Bounds.Left, group.Bounds.Bottom + 2));
        }

        private void ShowOverflowMenu()
        {
            if (activeDropDownMenu != null)
                activeDropDownMenu.Dispose();
            activeDropDownMenu = CreateRibbonMenu();
            RibbonTab selected = GetSelectedTab();
            if (selected != null)
                foreach (RibbonGroup group in selected.Groups)
                    if (overflowGroups.Contains(group))
                    {
                        ToolStripMenuItem groupMenu = new ToolStripMenuItem(group.Text) { ForeColor = appearance.TextColor };
                        AddGroupMenuItems(groupMenu.DropDownItems, group);
                        activeDropDownMenu.Items.Add(groupMenu);
                    }
            activeDropDownMenu.Show(this, new Point(overflowBounds.Left, overflowBounds.Bottom + 2));
        }

        private void AddGroupMenuItems(ToolStripItemCollection target, RibbonGroup group)
        {
            foreach (RibbonItem item in group.Items)
            {
                if (item is RibbonSeparator)
                {
                    target.Add(new ToolStripSeparator());
                    continue;
                }

                RibbonDropDownButton dropDown = item as RibbonDropDownButton;
                if (dropDown != null)
                {
                    ToolStripMenuItem parent = CreateToolStripButton(item);
                    foreach (RibbonMenuItem model in dropDown.Items)
                    {
                        if (model.IsSeparator)
                            parent.DropDownItems.Add(new ToolStripSeparator());
                        else
                        {
                            RibbonMenuItem current = model;
                            ToolStripMenuItem child = new ToolStripMenuItem(current.Text)
                            {
                                Enabled = current.Enabled,
                                Checked = current.Checked,
                                ShortcutKeyDisplayString = current.ShortcutText,
                                ForeColor = appearance.TextColor
                            };
                            child.Click += delegate { current.PerformClick(); };
                            parent.DropDownItems.Add(child);
                        }
                    }
                    target.Add(parent);
                    continue;
                }

                RibbonButton button = item as RibbonButton;
                if (button != null)
                {
                    RibbonButton current = button;
                    ToolStripMenuItem menuItem = CreateToolStripButton(item);
                    RibbonToggleButton toggle = item as RibbonToggleButton;
                    if (toggle != null) menuItem.Checked = toggle.Checked;
                    menuItem.Click += delegate { current.PerformClick(); };
                    target.Add(menuItem);
                    continue;
                }

                ToolStripMenuItem valueItem = new ToolStripMenuItem(item.Text + GetOverflowValue(item))
                {
                    Enabled = false,
                    ForeColor = appearance.MutedTextColor
                };
                target.Add(valueItem);
            }
        }

        private ToolStripMenuItem CreateToolStripButton(RibbonItem item)
        {
            ToolStripMenuItem result = new ToolStripMenuItem(item.Text)
            {
                Enabled = item.Enabled,
                ForeColor = appearance.TextColor
            };
            if (!string.IsNullOrEmpty(item.IconKey))
                result.Image = CreateMenuIcon(item.IconKey, item.IconColor.IsEmpty ? appearance.TextColor : item.IconColor);
            return result;
        }

        private static string GetOverflowValue(RibbonItem item)
        {
            RibbonTextBox text = item as RibbonTextBox;
            if (text != null) return ": " + text.Value;
            RibbonComboBox combo = item as RibbonComboBox;
            if (combo != null) return ": " + combo.SelectedText;
            RibbonDatePicker date = item as RibbonDatePicker;
            if (date != null) return ": " + date.Value.ToShortDateString();
            RibbonNumericUpDown numeric = item as RibbonNumericUpDown;
            if (numeric != null) return ": " + numeric.Value;
            RibbonCheckBox check = item as RibbonCheckBox;
            if (check != null) return check.Checked ? ": Sí" : ": No";
            return string.Empty;
        }

        private ContextMenuStrip CreateRibbonMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip
            {
                BackColor = appearance.SurfaceColor,
                ForeColor = appearance.TextColor,
                Font = Font,
                ShowImageMargin = true,
                Renderer = new ToolStripProfessionalRenderer(new RibbonMenuColorTable(appearance))
            };
            return menu;
        }

        private static Image CreateMenuIcon(string iconKey, Color color)
        {
            Bitmap bitmap = new Bitmap(20, 20);
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                FluentIconCatalog.TryDraw(graphics, iconKey, new Rectangle(0, 0, 20, 20), color, 18F);
            }
            return bitmap;
        }

        private sealed class RibbonMenuColorTable : ProfessionalColorTable
        {
            private readonly RibbonAppearance colors;
            internal RibbonMenuColorTable(RibbonAppearance colors) { this.colors = colors; UseSystemColors = false; }
            public override Color ToolStripDropDownBackground { get { return colors.SurfaceColor; } }
            public override Color ImageMarginGradientBegin { get { return colors.RaisedColor; } }
            public override Color ImageMarginGradientMiddle { get { return colors.RaisedColor; } }
            public override Color ImageMarginGradientEnd { get { return colors.RaisedColor; } }
            public override Color MenuItemSelected { get { return colors.HoverColor; } }
            public override Color MenuItemBorder { get { return colors.StrongBorderColor; } }
            public override Color SeparatorDark { get { return colors.BorderColor; } }
            public override Color SeparatorLight { get { return colors.SurfaceColor; } }
        }

        private sealed class RibbonControlAccessibleObject : ControlAccessibleObject
        {
            private readonly RibbonControl ribbon;
            private readonly Dictionary<RibbonTab, AccessibleObject> children = new Dictionary<RibbonTab, AccessibleObject>();

            internal RibbonControlAccessibleObject(RibbonControl owner) : base(owner) { ribbon = owner; }

            public override string Name
            {
                get { return !string.IsNullOrEmpty(ribbon.AccessibleName) ? ribbon.AccessibleName :
                    !string.IsNullOrEmpty(ribbon.TitleText) ? ribbon.TitleText : "Cinta"; }
                set { ribbon.AccessibleName = value; }
            }

            public override AccessibleRole Role { get { return ribbon.AccessibleRole; } }
            public override int GetChildCount() { return ribbon.tabs.Count; }
            public override AccessibleObject GetChild(int index)
            {
                if (index < 0 || index >= ribbon.tabs.Count) return null;
                RibbonTab tab = ribbon.tabs[index];
                AccessibleObject child;
                if (!children.TryGetValue(tab, out child))
                {
                    child = new RibbonTabAccessibleObject(ribbon, tab, this);
                    children[tab] = child;
                }
                return child;
            }
        }

        private sealed class RibbonTabAccessibleObject : AccessibleObject
        {
            private readonly RibbonControl ribbon;
            private readonly RibbonTab tab;
            private readonly AccessibleObject parent;
            private readonly Dictionary<RibbonGroup, AccessibleObject> children = new Dictionary<RibbonGroup, AccessibleObject>();

            internal RibbonTabAccessibleObject(RibbonControl owner, RibbonTab model, AccessibleObject parentObject)
            { ribbon = owner; tab = model; parent = parentObject; }

            private int Index { get { return ribbon.tabs.IndexOf(tab); } }
            public override string Name { get { return tab.Text; } set { tab.Text = value ?? string.Empty; } }
            public override AccessibleRole Role { get { return AccessibleRole.PageTab; } }
            public override AccessibleObject Parent { get { return parent; } }
            public override string DefaultAction { get { return "Seleccionar"; } }
            public override Rectangle Bounds
            {
                get
                {
                    int index = Index;
                    if (index < 0 || !ribbon.IsHandleCreated || ribbon.IsDisposed || !ribbon.Visible || !tab.Visible)
                        return Rectangle.Empty;
                    Rectangle bounds = Rectangle.Intersect(ribbon.ClientRectangle, ribbon.GetTabBounds(index));
                    return bounds.IsEmpty ? Rectangle.Empty : ribbon.RectangleToScreen(bounds);
                }
            }
            public override AccessibleStates State
            {
                get
                {
                    int index = Index;
                    AccessibleStates state = AccessibleStates.Selectable | AccessibleStates.Focusable;
                    if (index < 0 || ribbon.IsDisposed) return state | AccessibleStates.Invisible | AccessibleStates.Unavailable;
                    if (!ribbon.Enabled) state |= AccessibleStates.Unavailable;
                    if (!ribbon.Visible || !tab.Visible) state |= AccessibleStates.Invisible;
                    else if (Bounds.IsEmpty) state |= AccessibleStates.Offscreen;
                    if (index == ribbon.selectedTabIndex) state |= AccessibleStates.Selected;
                    if (ribbon.Focused && ribbon.KeyboardTarget == null && index == ribbon.keyboardTabIndex)
                        state |= AccessibleStates.Focused;
                    return state;
                }
            }
            public override void DoDefaultAction()
            {
                int index = Index;
                if (index < 0 || ribbon.IsDisposed || !ribbon.Enabled || !tab.Visible) return;
                if (!ribbon.SuppressAccessibilityInterop) ribbon.Focus();
                ribbon.SetKeyboardTabIndex(index);
                ribbon.SelectedTabIndex = index;
            }
            public override AccessibleObject Navigate(AccessibleNavigation navdir)
            {
                int index = Index;
                int next = navdir == AccessibleNavigation.Next || navdir == AccessibleNavigation.Right
                    ? ribbon.FindVisibleTab(index, 1)
                    : navdir == AccessibleNavigation.Previous || navdir == AccessibleNavigation.Left
                        ? ribbon.FindVisibleTab(index, -1) : -1;
                return next >= 0 && next != index ? parent.GetChild(next) : null;
            }
            public override int GetChildCount() { return ribbon.GetAccessibleGroups(tab).Count; }
            public override AccessibleObject GetChild(int index)
            {
                List<RibbonGroup> groups = ribbon.GetAccessibleGroups(tab);
                if (index < 0 || index >= groups.Count) return null;
                RibbonGroup group = groups[index];
                AccessibleObject child;
                if (!children.TryGetValue(group, out child))
                {
                    child = new RibbonGroupAccessibleObject(ribbon, tab, group, this);
                    children[group] = child;
                }
                return child;
            }
        }

        private sealed class RibbonGroupAccessibleObject : AccessibleObject
        {
            private readonly RibbonControl ribbon;
            private readonly RibbonTab tab;
            private readonly RibbonGroup group;
            private readonly AccessibleObject parent;
            private readonly Dictionary<RibbonItem, AccessibleObject> children = new Dictionary<RibbonItem, AccessibleObject>();

            internal RibbonGroupAccessibleObject(RibbonControl owner, RibbonTab ownerTab, RibbonGroup model,
                AccessibleObject parentObject)
            { ribbon = owner; tab = ownerTab; group = model; parent = parentObject; }

            private bool IsCollapsed { get { return ribbon.collapsedGroups.Contains(group); } }
            public override string Name { get { return group.Text; } set { group.Text = value ?? string.Empty; } }
            public override AccessibleRole Role { get { return IsCollapsed ? AccessibleRole.ButtonDropDown : AccessibleRole.Grouping; } }
            public override AccessibleObject Parent { get { return parent; } }
            public override string DefaultAction { get { return IsCollapsed ? "Abrir" : string.Empty; } }
            public override Rectangle Bounds
            {
                get { return ribbon.IsAccessibleGroup(tab, group) ? ribbon.GetAccessibleScreenBounds(group.Bounds) : Rectangle.Empty; }
            }
            public override AccessibleStates State
            {
                get
                {
                    AccessibleStates state = IsCollapsed
                        ? AccessibleStates.Focusable | AccessibleStates.Collapsed | AccessibleStates.HasPopup
                        : AccessibleStates.Expanded;
                    if (ribbon.IsDisposed || !ribbon.Enabled) state |= AccessibleStates.Unavailable;
                    if (!ribbon.IsAccessibleGroup(tab, group)) state |= AccessibleStates.Invisible;
                    else if (Bounds.IsEmpty) state |= AccessibleStates.Offscreen;
                    if (ribbon.Focused && ribbon.keyboardGroup == group && ribbon.keyboardItem == null)
                        state |= AccessibleStates.Focused;
                    return state;
                }
            }
            public override int GetChildCount() { return IsCollapsed ? 0 : ribbon.GetAccessibleItems(group).Count; }
            public override AccessibleObject GetChild(int index)
            {
                if (IsCollapsed) return null;
                List<RibbonItem> items = ribbon.GetAccessibleItems(group);
                if (index < 0 || index >= items.Count) return null;
                RibbonItem item = items[index];
                AccessibleObject child;
                if (!children.TryGetValue(item, out child))
                {
                    child = new RibbonItemAccessibleObject(ribbon, group, item, this);
                    children[item] = child;
                }
                return child;
            }
            public override void DoDefaultAction()
            {
                if (!IsCollapsed || !ribbon.IsAccessibleGroup(tab, group) || !ribbon.Enabled) return;
                if (!ribbon.SuppressAccessibilityInterop) ribbon.Focus();
                ribbon.SetKeyboardTarget(group);
                ribbon.PerformAccessibleTarget(group, true);
            }
        }

        private sealed class RibbonItemAccessibleObject : AccessibleObject
        {
            private readonly RibbonControl ribbon;
            private readonly RibbonGroup group;
            private readonly RibbonItem item;
            private readonly AccessibleObject parent;

            internal RibbonItemAccessibleObject(RibbonControl owner, RibbonGroup ownerGroup, RibbonItem model,
                AccessibleObject parentObject)
            { ribbon = owner; group = ownerGroup; item = model; parent = parentObject; }

            public override string Name { get { return item.Text; } set { item.Text = value ?? string.Empty; } }
            public override AccessibleObject Parent { get { return parent; } }
            public override AccessibleRole Role
            {
                get
                {
                    if (item is RibbonSplitButton) return AccessibleRole.SplitButton;
                    if (item is RibbonDropDownButton) return AccessibleRole.ButtonDropDown;
                    if (item is RibbonToggleButton) return AccessibleRole.CheckButton;
                    if (item is RibbonButton) return AccessibleRole.PushButton;
                    if (item is RibbonLabel) return AccessibleRole.StaticText;
                    return AccessibleRole.Separator;
                }
            }
            public override string DefaultAction
            {
                get
                {
                    RibbonToggleButton toggle = item as RibbonToggleButton;
                    if (toggle != null) return toggle.Checked ? "Desactivar" : "Activar";
                    if (item is RibbonDropDownButton && !(item is RibbonSplitButton)) return "Abrir";
                    return item is RibbonButton ? "Ejecutar" : string.Empty;
                }
            }
            public override Rectangle Bounds
            {
                get
                {
                    if (!ribbon.IsAccessibleItem(group, item)) return Rectangle.Empty;
                    return ribbon.GetAccessibleScreenBounds(Rectangle.Intersect(group.Bounds, item.Bounds));
                }
            }
            public override AccessibleStates State
            {
                get
                {
                    bool actionable = item is RibbonButton;
                    AccessibleStates state = actionable ? AccessibleStates.Focusable : AccessibleStates.ReadOnly;
                    if (!item.Enabled || !ribbon.Enabled || ribbon.IsDisposed) state |= AccessibleStates.Unavailable;
                    if (!ribbon.IsAccessibleItem(group, item)) state |= AccessibleStates.Invisible;
                    else if (Bounds.IsEmpty) state |= AccessibleStates.Offscreen;
                    RibbonToggleButton toggle = item as RibbonToggleButton;
                    if (toggle != null && toggle.Checked) state |= AccessibleStates.Checked;
                    if (item is RibbonDropDownButton) state |= AccessibleStates.HasPopup;
                    if (item.Busy) state |= AccessibleStates.Busy;
                    if (ribbon.Focused && ribbon.keyboardItem == item) state |= AccessibleStates.Focused;
                    return state;
                }
            }
            public override void DoDefaultAction()
            {
                if (!(item is RibbonButton) || !item.Enabled || !ribbon.Enabled || !ribbon.IsAccessibleItem(group, item))
                    return;
                if (!ribbon.SuppressAccessibilityInterop) ribbon.Focus();
                ribbon.SetKeyboardTarget(item);
                ribbon.PerformAccessibleTarget(item, false);
            }
            public override AccessibleObject Navigate(AccessibleNavigation navdir)
            {
                List<RibbonItem> items = ribbon.GetAccessibleItems(group);
                int index = items.IndexOf(item);
                int direction = navdir == AccessibleNavigation.Next || navdir == AccessibleNavigation.Right ? 1 :
                    navdir == AccessibleNavigation.Previous || navdir == AccessibleNavigation.Left ? -1 : 0;
                int next = index + direction;
                return direction != 0 && next >= 0 && next < items.Count ? parent.GetChild(next) : null;
            }
        }

        private static void FillRoundedRectangle(Graphics graphics, Color color, Rectangle bounds, int radius)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0)
                return;
            using (GraphicsPath path = RoundedRectangle(bounds, radius))
            using (SolidBrush fill = new SolidBrush(color))
                graphics.FillPath(fill, path);
        }

        private static Color Blend(Color from, Color to, float amount)
        {
            amount = Math.Max(0F, Math.Min(1F, amount));
            return Color.FromArgb(
                (int)(from.A + (to.A - from.A) * amount),
                (int)(from.R + (to.R - from.R) * amount),
                (int)(from.G + (to.G - from.G) * amount),
                (int)(from.B + (to.B - from.B) * amount));
        }

        private static void DrawRoundedRectangle(Graphics graphics, Color color, Rectangle bounds, int radius)
        {
            Rectangle outline = bounds;
            outline.Width--;
            outline.Height--;
            using (GraphicsPath path = RoundedRectangle(outline, radius))
            using (Pen stroke = new Pen(color))
                graphics.DrawPath(stroke, path);
        }

        private static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
        {
            int diameter = radius * 2;
            GraphicsPath path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
