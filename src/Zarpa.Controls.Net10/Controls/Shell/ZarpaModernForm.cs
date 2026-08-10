using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Design;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ZarpaSuite.Controls
{
    [ToolboxItem(true)]
    [ToolboxBitmap(typeof(Form))]
    public class ZarpaModernForm : Form, IZarpaThemeAware
    {
        private const int WmNcHitTest = 0x0084;
        private const int WmNcCalcSize = 0x0083;
        private const int WmNcMouseMove = 0x00A0;
        private const int WmNcMouseLeave = 0x02A2;
        private const int WmSysCommand = 0x0112;
        private const int ScMinimize = 0xF020;
        private const int ScMaximize = 0xF030;
        private const int ScClose = 0xF060;
        private const int ScRestore = 0xF120;
        private const int WsCaption = 0x00C00000;
        private const int WsThickFrame = 0x00040000;
        private const int WsSysMenu = 0x00080000;
        private const int WsMinimizeBox = 0x00020000;
        private const int WsMaximizeBox = 0x00010000;
        private const int TmeLeave = 0x00000002;
        private const int TmeNonClient = 0x00000010;
        private const int SmCxSizeFrame = 32;
        private const int SmCySizeFrame = 33;
        private const int SmCxPaddedBorder = 92;
        private const int HtClient = 1, HtCaption = 2, HtLeft = 10, HtRight = 11;
        private const int HtMinButton = 8, HtMaxButton = 9, HtClose = 20;
        private const int HtTop = 12, HtTopLeft = 13, HtTopRight = 14;
        private const int HtBottom = 15, HtBottomLeft = 16, HtBottomRight = 17;
        private ZarpaThemeTokens theme;
        private int titleBarHeight = 42;
        private bool modernChrome = true;
        private string contextText = string.Empty;
        private string iconKey = "ic_fluent_apps_24_regular";
        private Rectangle minimizeBounds, maximizeBounds, closeBounds;
        private int hotButton = -1;
        private ZarpaDpiScale dpiScale = new ZarpaDpiScale(96, 96);
        internal bool SuppressAccessibilityInterop { get; set; }

        private int S(int logicalPixels) { return dpiScale.X(logicalPixels); }
        private int ChromeHeight { get { return dpiScale.Y(titleBarHeight); } }

        public ZarpaModernForm()
        {
            theme = new ZarpaThemeTokens(ThemeInvalidated);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            Font = new Font("Segoe UI", 9F);
            MinimumSize = dpiScale.Size(new Size(480, 320));
            UpdateChrome();
        }

        [Category("Zarpa Chrome"), DefaultValue(true)]
        public bool ModernChrome { get { return modernChrome; } set { modernChrome = value; UpdateChrome(); } }

        [Category("Zarpa Chrome"), DefaultValue(42)]
        public int TitleBarHeight
        {
            get { return titleBarHeight; }
            set { titleBarHeight = Math.Max(32, Math.Min(64, value)); UpdateChrome(); }
        }

        [Category("Zarpa Chrome"), DefaultValue("")]
        public string ContextText { get { return contextText; } set { contextText = value ?? string.Empty; Invalidate(); } }

        [Category("Zarpa Chrome"), DefaultValue("ic_fluent_apps_24_regular")]
        [Editor("ZarpaSuite.Controls.Design.FluentIconPickerEditor, Zarpa.Controls", typeof(UITypeEditor))]
        public string TitleIconKey { get { return iconKey; } set { iconKey = value ?? string.Empty; Invalidate(); } }

        [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public ZarpaThemeTokens ActiveTheme { get { return theme; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams parameters = base.CreateParams;
                if (!modernChrome) return parameters;

                parameters.Style |= WsCaption | WsThickFrame;
                if (ControlBox) parameters.Style |= WsSysMenu;
                else parameters.Style &= ~(WsSysMenu | WsMinimizeBox | WsMaximizeBox);
                if (ControlBox && MinimizeBox) parameters.Style |= WsMinimizeBox;
                else parameters.Style &= ~WsMinimizeBox;
                if (ControlBox && MaximizeBox) parameters.Style |= WsMaximizeBox;
                else parameters.Style &= ~WsMaximizeBox;
                return parameters;
            }
        }

        public void ApplyTheme(ZarpaThemeTokens value)
        {
            if (value == null) return;
            theme = value;
            titleBarHeight = theme.HeaderHeight;
            if (modernChrome) Padding = new Padding(dpiScale.Stroke(theme.BorderThickness), ChromeHeight,
                dpiScale.Stroke(theme.BorderThickness), dpiScale.Stroke(theme.BorderThickness));
            BackColor = theme.Canvas;
            ForeColor = theme.Text;
            Font = new Font(theme.FontFamily, theme.FontSize);
            Invalidate(true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (!modernChrome) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Rectangle header = new Rectangle(0, 0, ClientSize.Width, ChromeHeight);
            using (SolidBrush brush = new SolidBrush(theme.Surface)) e.Graphics.FillRectangle(brush, header);
            using (Pen borderPen = new Pen(theme.Border))
            {
                borderPen.Width = dpiScale.Stroke(theme.BorderThickness);
                e.Graphics.DrawRectangle(borderPen, 0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1));
                e.Graphics.DrawLine(borderPen, 0, ChromeHeight - 1, Width, ChromeHeight - 1);
            }

            Rectangle iconBounds = new Rectangle(S(14), (ChromeHeight - S(22)) / 2, S(22), S(22));
            FluentIconCatalog.TryDraw(e.Graphics, iconKey, iconBounds, theme.Accent, dpiScale.X(19F));
            int controlsWidth = ControlBox ? S(138) : 0;
            int contextWidth = string.IsNullOrEmpty(contextText) ? 0 :
                Math.Min(S(240), TextRenderer.MeasureText(contextText.ToUpperInvariant(), Font).Width + S(24));
            Rectangle titleBounds = new Rectangle(S(44), 0,
                Math.Max(S(20), Width - S(56) - controlsWidth - contextWidth), ChromeHeight);
            using (Font titleFont = new Font(Font, FontStyle.Bold))
                TextRenderer.DrawText(e.Graphics, Text, titleFont, titleBounds, theme.Text,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            if (contextWidth > 0)
            {
                Rectangle context = new Rectangle(Width - controlsWidth - contextWidth - S(8), S(9),
                    contextWidth, ChromeHeight - S(18));
                ZarpaPaint.FillRounded(e.Graphics, theme.SurfaceRaised, context, S(theme.CornerRadius));
                ZarpaPaint.DrawRounded(e.Graphics, theme.Border, context, S(theme.CornerRadius), dpiScale.Stroke(theme.BorderThickness));
                TextRenderer.DrawText(e.Graphics, contextText.ToUpperInvariant(), Font, context, theme.TextMuted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            }
            DrawWindowButtons(e.Graphics);
        }

        private void DrawWindowButtons(Graphics graphics)
        {
            UpdateWindowButtonBounds();
            if (!ControlBox) return;
            DrawWindowButton(graphics, minimizeBounds, 0, hotButton == 0, MinimizeBox);
            DrawWindowButton(graphics, maximizeBounds, 1, hotButton == 1, MaximizeBox);
            DrawWindowButton(graphics, closeBounds, 2, hotButton == 2, true);
        }

        private void UpdateWindowButtonBounds()
        {
            if (!ControlBox)
            {
                minimizeBounds = maximizeBounds = closeBounds = Rectangle.Empty;
                return;
            }
            int buttonWidth = S(46);
            closeBounds = new Rectangle(Width - buttonWidth, S(1), buttonWidth - S(1), ChromeHeight - S(2));
            maximizeBounds = new Rectangle(closeBounds.Left - buttonWidth, S(1), buttonWidth, ChromeHeight - S(2));
            minimizeBounds = new Rectangle(maximizeBounds.Left - buttonWidth, S(1), buttonWidth, ChromeHeight - S(2));
        }

        private void DrawWindowButton(Graphics graphics, Rectangle bounds, int kind, bool hot, bool enabled)
        {
            if (hot && enabled)
            {
                Color color = kind == 2 ? theme.Danger : theme.SurfaceRaised;
                using (SolidBrush brush = new SolidBrush(color)) graphics.FillRectangle(brush, bounds);
            }
            Color glyph = !enabled ? Color.FromArgb(96, theme.TextMuted) :
                kind == 2 && hot ? Color.White : theme.TextMuted;
            using (Pen pen = new Pen(glyph, dpiScale.X(1.2F)))
            {
                int cx = bounds.Left + bounds.Width / 2, cy = bounds.Top + bounds.Height / 2;
                if (kind == 0) graphics.DrawLine(pen, cx - S(5), cy + S(3), cx + S(5), cy + S(3));
                else if (kind == 1) graphics.DrawRectangle(pen, cx - S(5), cy - S(4), S(10), S(8));
                else
                {
                    graphics.DrawLine(pen, cx - S(4), cy - S(4), cx + S(4), cy + S(4));
                    graphics.DrawLine(pen, cx + S(4), cy - S(4), cx - S(4), cy + S(4));
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            UpdateWindowButtonBounds();
            int next = MinimizeBox && minimizeBounds.Contains(e.Location) ? 0 :
                MaximizeBox && maximizeBounds.Contains(e.Location) ? 1 :
                closeBounds.Contains(e.Location) ? 2 : -1;
            SetHotButton(next);
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); SetHotButton(-1); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            UpdateWindowButtonBounds();
            if (minimizeBounds.Contains(e.Location)) ActivateWindowButton(0);
            else if (maximizeBounds.Contains(e.Location)) ActivateWindowButton(1);
            else if (closeBounds.Contains(e.Location)) ActivateWindowButton(2);
        }

        private void ActivateWindowButton(int index)
        {
            if (!Enabled || IsDisposed || !ControlBox ||
                (index == 0 && !MinimizeBox) || (index == 1 && !MaximizeBox)) return;
            int command = index == 0 ? ScMinimize : index == 1 ?
                (WindowState == FormWindowState.Maximized ? ScRestore : ScMaximize) : ScClose;
            Message message = Message.Create(Handle, WmSysCommand, (IntPtr)command, IntPtr.Zero);
            DefWndProc(ref message);
        }

        private void SetHotButton(int value)
        {
            if (value == hotButton) return;
            hotButton = value;
            Invalidate(new Rectangle(Math.Max(0, Width - S(140)), 0, S(140), ChromeHeight));
        }

        private Rectangle GetWindowButtonBounds(int index)
        {
            UpdateWindowButtonBounds();
            return index == 0 ? minimizeBounds : index == 1 ? maximizeBounds : closeBounds;
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            UpdateWindowButtonBounds();
            if (IsHandleCreated && !SuppressAccessibilityInterop)
            {
                AccessibilityNotifyClients(AccessibleEvents.NameChange, 2);
                AccessibilityNotifyClients(AccessibleEvents.LocationChange, 0);
            }
        }

        protected override AccessibleObject CreateAccessibilityInstance()
        {
            return new ModernFormAccessibleObject(this);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDpiScale(ZarpaDpiScale.FromControl(this));
        }

        internal void ApplyDpiForTest(int dpi)
        {
            ApplyDpiScale(new ZarpaDpiScale(dpi, dpi));
        }

        private void ApplyDpiScale(ZarpaDpiScale value)
        {
            if (value == null || (dpiScale.DpiX == value.DpiX && dpiScale.DpiY == value.DpiY)) return;
            Size previousDefaultMinimum = dpiScale.Size(new Size(480, 320));
            bool ownsDefaultMinimum = MinimumSize == previousDefaultMinimum;
            dpiScale = value;
            if (ownsDefaultMinimum) MinimumSize = dpiScale.Size(new Size(480, 320));
            UpdateChrome();
            PerformLayout();
            Invalidate(true);
        }

        protected override void WndProc(ref Message m)
        {
            if (!modernChrome)
            {
                base.WndProc(ref m);
                return;
            }

            if (m.Msg == WmNcCalcSize && m.WParam != IntPtr.Zero)
            {
                if (WindowState == FormWindowState.Maximized || IsZoomed(Handle))
                    InsetMaximizedClientArea(m.LParam);
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == WmNcHitTest)
            {
                IntPtr result;
                if (DwmDefWindowProc(Handle, m.Msg, m.WParam, m.LParam, out result))
                {
                    m.Result = result;
                    return;
                }
                m.Result = (IntPtr)HitTestWindow(m.LParam);
                return;
            }
            if (m.Msg == WmNcMouseMove)
            {
                int hit = (int)m.WParam;
                SetHotButton(hit == HtMinButton ? 0 : hit == HtMaxButton ? 1 : hit == HtClose ? 2 : -1);
                TrackNonClientMouseLeave();
            }
            else if (m.Msg == WmNcMouseLeave) SetHotButton(-1);

            base.WndProc(ref m);
        }

        private int HitTestWindow(IntPtr coordinates)
        {
            Point point = PointToClient(new Point((short)((long)coordinates & 0xFFFF),
                (short)(((long)coordinates >> 16) & 0xFFFF)));
            int grip = S(6);
            bool resizable = WindowState == FormWindowState.Normal;
            bool left = resizable && point.X < grip;
            bool right = resizable && point.X >= ClientSize.Width - grip;
            bool top = resizable && point.Y < grip;
            bool bottom = resizable && point.Y >= ClientSize.Height - grip;
            if (left && top) return HtTopLeft;
            if (right && top) return HtTopRight;
            if (left && bottom) return HtBottomLeft;
            if (right && bottom) return HtBottomRight;
            if (left) return HtLeft;
            if (right) return HtRight;
            if (top) return HtTop;
            if (bottom) return HtBottom;

            UpdateWindowButtonBounds();
            if (closeBounds.Contains(point)) return HtClose;
            if (maximizeBounds.Contains(point)) return MaximizeBox ? HtMaxButton : HtClient;
            if (minimizeBounds.Contains(point)) return MinimizeBox ? HtMinButton : HtClient;
            return point.Y < ChromeHeight ? HtCaption : HtClient;
        }

        private void TrackNonClientMouseLeave()
        {
            TrackMouseEventData tracking = new TrackMouseEventData();
            tracking.Size = Marshal.SizeOf(typeof(TrackMouseEventData));
            tracking.Flags = TmeLeave | TmeNonClient;
            tracking.TrackWindow = Handle;
            TrackMouseEvent(ref tracking);
        }

        private static void InsetMaximizedClientArea(IntPtr parameters)
        {
            NativeRectangle client = (NativeRectangle)Marshal.PtrToStructure(parameters, typeof(NativeRectangle));
            int horizontalFrame = GetSystemMetrics(SmCxSizeFrame) + GetSystemMetrics(SmCxPaddedBorder);
            int verticalFrame = GetSystemMetrics(SmCySizeFrame) + GetSystemMetrics(SmCxPaddedBorder);
            client.Left += horizontalFrame;
            client.Top += verticalFrame;
            client.Right -= horizontalFrame;
            client.Bottom -= verticalFrame;
            Marshal.StructureToPtr(client, parameters, false);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRectangle
        {
            internal int Left;
            internal int Top;
            internal int Right;
            internal int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TrackMouseEventData
        {
            internal int Size;
            internal int Flags;
            internal IntPtr TrackWindow;
            internal int HoverTime;
        }

        [DllImport("dwmapi.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DwmDefWindowProc(IntPtr window, int message, IntPtr wParam,
            IntPtr lParam, out IntPtr result);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TrackMouseEvent(ref TrackMouseEventData tracking);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool IsZoomed(IntPtr window);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        private sealed class ModernFormAccessibleObject : ControlAccessibleObject
        {
            private readonly ZarpaModernForm form;
            private readonly AccessibleObject[] chromeButtons;

            internal ModernFormAccessibleObject(ZarpaModernForm owner) : base(owner)
            {
                form = owner;
                chromeButtons = new AccessibleObject[] {
                    new WindowButtonAccessibleObject(owner, this, 0),
                    new WindowButtonAccessibleObject(owner, this, 1),
                    new WindowButtonAccessibleObject(owner, this, 2)
                };
            }

            public override string Name
            {
                get { return !string.IsNullOrEmpty(form.AccessibleName) ? form.AccessibleName : form.Text; }
                set { form.AccessibleName = value; }
            }
            public override AccessibleRole Role
            {
                get { return form.AccessibleRole == AccessibleRole.Default ? AccessibleRole.Window : form.AccessibleRole; }
            }
            public override int GetChildCount()
            {
                return (form.ControlBox ? chromeButtons.Length : 0) + form.Controls.Count;
            }
            public override AccessibleObject GetChild(int index)
            {
                int chromeCount = form.ControlBox ? chromeButtons.Length : 0;
                if (index >= 0 && index < chromeCount) return chromeButtons[index];
                int controlIndex = index - chromeCount;
                return controlIndex >= 0 && controlIndex < form.Controls.Count
                    ? form.Controls[controlIndex].AccessibilityObject : null;
            }
            public override AccessibleObject HitTest(int x, int y)
            {
                Point client = form.PointToClient(new Point(x, y));
                if (form.ModernChrome && form.ControlBox && form.Visible)
                    for (int index = 0; index < chromeButtons.Length; index++)
                        if (form.GetWindowButtonBounds(index).Contains(client)) return chromeButtons[index];
                return base.HitTest(x, y);
            }
        }

        private sealed class WindowButtonAccessibleObject : AccessibleObject
        {
            private readonly ZarpaModernForm form;
            private readonly AccessibleObject parent;
            private readonly int index;

            internal WindowButtonAccessibleObject(ZarpaModernForm owner, AccessibleObject parentObject, int buttonIndex)
            { form = owner; parent = parentObject; index = buttonIndex; }

            public override string Name
            {
                get { return index == 0 ? "Minimizar" : index == 1 ?
                    (form.WindowState == FormWindowState.Maximized ? "Restaurar" : "Maximizar") : "Cerrar"; }
                set { }
            }
            public override AccessibleRole Role { get { return AccessibleRole.PushButton; } }
            public override AccessibleObject Parent { get { return parent; } }
            public override string DefaultAction { get { return Name; } }
            public override Rectangle Bounds
            {
                get
                {
                    if (!form.ModernChrome || !form.ControlBox || !form.Visible ||
                        !form.IsHandleCreated || form.IsDisposed)
                        return Rectangle.Empty;
                    Rectangle bounds = Rectangle.Intersect(form.ClientRectangle, form.GetWindowButtonBounds(index));
                    return bounds.IsEmpty ? Rectangle.Empty : form.RectangleToScreen(bounds);
                }
            }
            public override AccessibleStates State
            {
                get
                {
                    AccessibleStates state = AccessibleStates.None;
                    if (!form.Enabled || form.IsDisposed || !form.ControlBox ||
                        (index == 0 && !form.MinimizeBox) || (index == 1 && !form.MaximizeBox))
                        state |= AccessibleStates.Unavailable;
                    if (!form.ModernChrome || !form.ControlBox || !form.Visible)
                        state |= AccessibleStates.Invisible;
                    else if (Bounds.IsEmpty) state |= AccessibleStates.Offscreen;
                    if (form.hotButton == index) state |= AccessibleStates.HotTracked;
                    return state;
                }
            }
            public override void DoDefaultAction()
            {
                if (!form.ModernChrome || !form.Visible || !form.Enabled || form.IsDisposed) return;
                form.ActivateWindowButton(index);
            }
        }

        private void UpdateChrome()
        {
            FormBorderStyle = modernChrome ? FormBorderStyle.None : FormBorderStyle.Sizable;
            Padding = modernChrome ? new Padding(S(1), ChromeHeight, S(1), S(1)) : Padding.Empty;
            Invalidate();
        }

        private void ThemeInvalidated() { ApplyTheme(theme); }
    }
}
