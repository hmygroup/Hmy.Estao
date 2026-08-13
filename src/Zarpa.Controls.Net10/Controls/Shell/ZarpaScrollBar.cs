using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ZarpaSuite.Controls
{
    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(VScrollBar))]
    [DefaultEvent("ValueChanged")]
    [DefaultProperty("Value")]
    public sealed class ZarpaScrollBar : Control, IZarpaThemeAware
    {
        private const int WheelStep = 24;
        private const float SmoothScrollResponse = 28F;
        private const float MinimumSmoothDuration = 0.18F;
        private readonly ZarpaPaintAnimator smoothScrollAnimator;
        private ZarpaThemeTokens theme;
        private Orientation orientation = Orientation.Vertical;
        private int wheelChange = WheelStep;
        private int contentSize;
        private int viewportSize = 1;
        private int value;
        private int targetValue;
        private float animatedValue;
        private bool smoothScrolling;
        private bool hot;
        private bool dragging;
        private int dragOffset;

        public ZarpaScrollBar()
        {
            theme = new ZarpaThemeTokens(Invalidate);
            smoothScrollAnimator = new ZarpaPaintAnimator(this, AdvanceSmoothScroll);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
            BackColor = theme.Surface;
            Cursor = Cursors.Hand;
            TabStop = true;
            AccessibleRole = AccessibleRole.ScrollBar;
            MinimumSize = new Size(7, 7);
            Size = new Size(ScrollBarThickness, 120);
        }

        public event EventHandler ValueChanged;

        [Category("Comportamiento"), DefaultValue(Orientation.Vertical)]
        public Orientation Orientation
        {
            get { return orientation; }
            set
            {
                if (orientation == value) return;
                orientation = value;
                ApplyDensitySize();
                Invalidate();
            }
        }

        [Category("Estado"), DefaultValue(0)]
        public int ContentSize
        {
            get { return contentSize; }
            set { contentSize = Math.Max(0, value); ClampValue(); Invalidate(); }
        }

        [Category("Estado"), DefaultValue(1)]
        public int ViewportSize
        {
            get { return viewportSize; }
            set { viewportSize = Math.Max(1, value); ClampValue(); Invalidate(); }
        }

        [Category("Estado"), DefaultValue(0)]
        public int Value
        {
            get { return value; }
            set
            {
                int next = Clamp(value, 0, MaximumValue);
                if (this.value == next) return;
                StopSmoothScroll();
                SetCurrentValue(next);
            }
        }

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public int MaximumValue { get { return Math.Max(0, contentSize - viewportSize); } }

        [Category("Comportamiento"), DefaultValue(WheelStep)]
        public int WheelChange
        {
            get { return wheelChange; }
            set { wheelChange = Math.Max(1, value); }
        }

        public void SetRange(int newContentSize, int newViewportSize)
        {
            contentSize = Math.Max(0, newContentSize);
            viewportSize = Math.Max(1, newViewportSize);
            ClampValue();
            Invalidate();
        }

        public void ScrollByWheel(int delta)
        {
            if (!Enabled || delta == 0 || MaximumValue == 0) return;
            double wheelDelta = delta / (double)SystemInformation.MouseWheelScrollDelta;
            int start = smoothScrolling ? targetValue : value;
            targetValue = Clamp((int)Math.Round(start - wheelDelta * wheelChange), 0, MaximumValue);
            if (targetValue == value && !smoothScrolling) return;
            if (!theme.MotionEnabled || IsDesignerHosted)
            {
                CompleteSmoothScroll();
                return;
            }
            if (!smoothScrolling) animatedValue = value;
            smoothScrolling = true;
            smoothScrollAnimator.Update(true);
        }

        public void ApplyTheme(ZarpaThemeTokens value)
        {
            if (value == null) return;
            theme = value;
            BackColor = theme.Surface;
            ApplyDensitySize();
            if (!theme.MotionEnabled && smoothScrolling) CompleteSmoothScroll();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle track = TrackBounds();
            Color trackColor = SystemInformation.HighContrast
                ? SystemColors.ScrollBar
                : Color.FromArgb(55, theme.SurfaceRaised);
            ZarpaPaint.FillRounded(e.Graphics, trackColor, track,
                Math.Min(track.Width, track.Height) / 2);

            Rectangle thumb = ThumbBounds(track);
            if (thumb.Width <= 0 || thumb.Height <= 0) return;
            Color thumbColor = SystemInformation.HighContrast
                ? SystemColors.Highlight
                : hot || dragging ? theme.Accent : Color.FromArgb(145, theme.TextMuted);
            ZarpaPaint.FillRounded(e.Graphics, thumbColor, thumb,
                Math.Min(thumb.Width, thumb.Height) / 2);
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            if (!Enabled) StopSmoothScroll();
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            hot = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!dragging)
            {
                hot = false;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (e.Button != MouseButtons.Left || !Enabled) return;
            StopSmoothScroll();
            Rectangle thumb = ThumbBounds(TrackBounds());
            int coordinate = orientation == Orientation.Horizontal ? e.X : e.Y;
            if (thumb.Contains(e.Location))
            {
                dragging = true;
                dragOffset = coordinate - (orientation == Orientation.Horizontal ? thumb.Left : thumb.Top);
                Capture = true;
                Invalidate();
                return;
            }

            int direction = coordinate < (orientation == Orientation.Horizontal ? thumb.Left : thumb.Top) ? -1 : 1;
            Value += direction * Math.Max(1, viewportSize);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging) return;
            Rectangle track = TrackBounds();
            Rectangle thumb = ThumbBounds(track);
            int coordinate = orientation == Orientation.Horizontal ? e.X : e.Y;
            int trackLength = orientation == Orientation.Horizontal ? track.Width : track.Height;
            int thumbLength = orientation == Orientation.Horizontal ? thumb.Width : thumb.Height;
            int available = Math.Max(1, trackLength - thumbLength);
            int position = Clamp(coordinate - dragOffset -
                (orientation == Orientation.Horizontal ? track.Left : track.Top), 0, available);
            Value = (int)Math.Round(position / (double)available * MaximumValue);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            dragging = false;
            Capture = false;
            hot = ClientRectangle.Contains(e.Location);
            Invalidate();
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (Capture || !dragging) return;
            dragging = false;
            hot = ClientRectangle.Contains(PointToClient(MousePosition));
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            ScrollByWheel(e.Delta);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!Enabled) return;
            int line = Math.Max(1, viewportSize / 10);
            int page = Math.Max(1, viewportSize - line);
            switch (e.KeyCode)
            {
                case Keys.Up:
                    if (orientation != Orientation.Vertical) break;
                    Value -= line;
                    e.Handled = true;
                    break;
                case Keys.Left:
                    if (orientation != Orientation.Horizontal) break;
                    Value -= line;
                    e.Handled = true;
                    break;
                case Keys.Down:
                    if (orientation != Orientation.Vertical) break;
                    Value += line;
                    e.Handled = true;
                    break;
                case Keys.Right:
                    if (orientation != Orientation.Horizontal) break;
                    Value += line;
                    e.Handled = true;
                    break;
                case Keys.PageUp:
                    Value -= page;
                    e.Handled = true;
                    break;
                case Keys.PageDown:
                    Value += page;
                    e.Handled = true;
                    break;
                case Keys.Home:
                    Value = 0;
                    e.Handled = true;
                    break;
                case Keys.End:
                    Value = MaximumValue;
                    e.Handled = true;
                    break;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                smoothScrollAnimator.Dispose();
            }
            base.Dispose(disposing);
        }

        private Rectangle TrackBounds()
        {
            return new Rectangle(2, 2, Math.Max(1, Width - 4), Math.Max(1, Height - 4));
        }

        private int ScrollBarThickness
        {
            get { return ZarpaDensityMetrics.Select(theme, 7, 8, 9, 11); }
        }

        private void ApplyDensitySize()
        {
            if (orientation == Orientation.Horizontal) Height = ScrollBarThickness;
            else Width = ScrollBarThickness;
        }

        private Rectangle ThumbBounds(Rectangle track)
        {
            int content = Math.Max(contentSize, viewportSize);
            int trackLength = orientation == Orientation.Horizontal ? track.Width : track.Height;
            int minimum = Math.Min(orientation == Orientation.Horizontal ? 28 : 24, trackLength);
            int proportional = (int)Math.Round(trackLength * viewportSize /
                (double)Math.Max(1, content));
            int thumbLength = Clamp(proportional, minimum, trackLength);
            int available = Math.Max(0, trackLength - thumbLength);
            double displayedValue = smoothScrolling ? animatedValue : value;
            int offset = MaximumValue == 0 ? 0 :
                (int)Math.Round(available * displayedValue / MaximumValue);
            return orientation == Orientation.Horizontal
                ? new Rectangle(track.Left + offset, track.Top, thumbLength, track.Height)
                : new Rectangle(track.Left, track.Top + offset, track.Width, thumbLength);
        }

        private void AdvanceSmoothScroll(float elapsed)
        {
            if (!smoothScrolling || !Enabled || !theme.MotionEnabled || IsDesignerHosted)
            {
                CompleteSmoothScroll();
                return;
            }

            float distance = targetValue - animatedValue;
            if (Math.Abs(distance) <= 0.12F)
            {
                CompleteSmoothScroll();
                return;
            }

            float blend = 1F - (float)Math.Exp(-SmoothScrollResponse * elapsed);
            float step = distance * blend;
            float maximumStep = Math.Max(0.5F, wheelChange * elapsed / MinimumSmoothDuration);
            if (Math.Abs(step) > maximumStep) step = Math.Sign(step) * maximumStep;
            animatedValue += step;
            SetCurrentValue((int)Math.Round(animatedValue));
            Invalidate();
        }

        private void ClampValue()
        {
            targetValue = Clamp(targetValue, 0, MaximumValue);
            animatedValue = Math.Max(0F, Math.Min(MaximumValue, animatedValue));
            if (value > MaximumValue) SetCurrentValue(MaximumValue);
            if (targetValue == value && !smoothScrolling) smoothScrollAnimator.Stop();
        }

        private void SetCurrentValue(int newValue)
        {
            int next = Clamp(newValue, 0, MaximumValue);
            if (value == next) return;
            value = next;
            if (!smoothScrolling) animatedValue = next;
            if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
            Invalidate();
        }

        private void StopSmoothScroll()
        {
            smoothScrollAnimator.Stop();
            smoothScrolling = false;
            targetValue = value;
            animatedValue = value;
        }

        private void CompleteSmoothScroll()
        {
            smoothScrollAnimator.Stop();
            animatedValue = targetValue;
            smoothScrolling = false;
            SetCurrentValue(targetValue);
            Invalidate();
        }

        private bool IsDesignerHosted
        {
            get
            {
                return DesignMode || Site != null && Site.DesignMode ||
                    LicenseManager.UsageMode == LicenseUsageMode.Designtime;
            }
        }

        private static int Clamp(int candidate, int minimum, int maximum)
        {
            return candidate < minimum ? minimum : candidate > maximum ? maximum : candidate;
        }
    }
}
