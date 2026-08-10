using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ZarpaSuite.Controls
{
    internal interface IRibbonModernHost
    {
        void ApplyAppearance(RibbonAppearance appearance);
        void ApplyDpiScale(ZarpaDpiScale scale);
    }

    internal abstract class RibbonModernFieldHost : UserControl, IRibbonModernHost
    {
        private Color surface = Color.White;
        private Color border = Color.FromArgb(198, 198, 206);
        private Color accent = Color.FromArgb(79, 70, 229);
        private int radius = 5;
        protected ZarpaDpiScale DpiScale = new ZarpaDpiScale(96, 96);
        private bool hovered;

        protected RibbonModernFieldHost()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            BackColor = Color.Transparent;
            TabStop = true;
        }

        public void ApplyAppearance(RibbonAppearance a)
        {
            surface = a.SurfaceColor; border = a.StrongBorderColor;
            accent = a.AccentColor; radius = Math.Max(2, Math.Min(8, a.CornerRadius));
            ApplyChildColors(a); Invalidate();
        }
        public virtual void ApplyDpiScale(ZarpaDpiScale scale)
        {
            if (scale == null) return;
            DpiScale = scale;
            PerformLayout();
            OnResize(EventArgs.Empty);
            Invalidate();
        }

        protected virtual void ApplyChildColors(RibbonAppearance a) { }
        protected ZarpaThemeTokens CreateEditorTheme(RibbonAppearance a)
        {
            ZarpaThemeTokens value = new ZarpaThemeTokens(null);
            value.Canvas = a.CanvasColor;
            value.Surface = a.SurfaceColor;
            value.SurfaceRaised = a.HoverColor;
            value.SurfaceOverlay = a.SurfaceColor;
            value.Border = a.BorderColor;
            value.BorderStrong = a.StrongBorderColor;
            value.Text = a.TextColor;
            value.TextMuted = a.MutedTextColor;
            value.Accent = a.AccentColor;
            value.AccentHover = a.AccentHoverColor;
            value.AccentPressed = a.AccentPressedColor;
            value.FontFamily = Font.Name;
            value.FontSize = Font.Size;
            return value;
        }
        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e)
        {
            if (ClientRectangle.Contains(PointToClient(MousePosition))) return;
            hovered = false;
            Invalidate();
            base.OnMouseLeave(e);
        }
        protected override void OnEnter(EventArgs e) { Invalidate(); base.OnEnter(e); }
        protected override void OnLeave(EventArgs e) { Invalidate(); base.OnLeave(e); }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(ZarpaPaint.EffectiveBackColor(Parent));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.Clear(ZarpaPaint.EffectiveBackColor(Parent));
            Rectangle bounds = ClientRectangle; bounds.Inflate(-DpiScale.Stroke(1), -DpiScale.Stroke(1));
            using (GraphicsPath path = RoundedRectangle(bounds, DpiScale.X(radius)))
            using (SolidBrush fill = new SolidBrush(surface))
            using (Pen stroke = new Pen(ContainsFocus || hovered ? accent : border,
                ContainsFocus ? DpiScale.X(1.5F) : DpiScale.Stroke(1)))
            { e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(stroke, path); }
        }

        internal static GraphicsPath RoundedRectangle(Rectangle b, int r)
        {
            int d = r * 2; GraphicsPath p = new GraphicsPath();
            p.AddArc(b.Left, b.Top, d, d, 180, 90); p.AddArc(b.Right - d, b.Top, d, d, 270, 90);
            p.AddArc(b.Right - d, b.Bottom - d, d, d, 0, 90); p.AddArc(b.Left, b.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }
    }

    internal sealed class RibbonModernTextBoxHost : RibbonModernFieldHost
    {
        internal readonly TextBox Editor;
        internal RibbonModernTextBoxHost()
        {
            Editor = new TextBox { BorderStyle = BorderStyle.None, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
            Editor.GotFocus += delegate { Invalidate(); }; Editor.LostFocus += delegate { Invalidate(); }; Controls.Add(Editor);
        }
        protected override void OnResize(EventArgs e) { Editor.Location = DpiScale.Point(new Point(8, 6)); Editor.Size = new Size(Math.Max(DpiScale.X(10), Width - DpiScale.X(16)), Math.Max(DpiScale.Y(15), Height - DpiScale.Y(10))); base.OnResize(e); }
        protected override void ApplyChildColors(RibbonAppearance a) { Editor.BackColor = a.SurfaceColor; Editor.ForeColor = a.TextColor; }
    }

    internal sealed class RibbonModernComboBoxHost : RibbonModernFieldHost
    {
        internal readonly ZarpaComboEditor Editor;
        internal RibbonModernComboBoxHost()
        {
            Cursor = Cursors.Hand;
            Editor = new ZarpaComboEditor { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
            Editor.GotFocus += delegate { Invalidate(); }; Editor.LostFocus += delegate { Invalidate(); }; Controls.Add(Editor);
        }
        protected override void OnResize(EventArgs e) { Editor.Location = DpiScale.Point(new Point(3, 3)); Editor.Size = new Size(Math.Max(DpiScale.X(20), Width - DpiScale.X(6)), Math.Max(DpiScale.Y(21), Height - DpiScale.Y(6))); base.OnResize(e); }
        protected override void ApplyChildColors(RibbonAppearance a) { Editor.ApplyTheme(CreateEditorTheme(a)); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (Enabled && e.Button == MouseButtons.Left && !Editor.Bounds.Contains(e.Location)) Editor.Focus();
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            // The child owns clicks on its surface. The host only completes the narrow border hit area.
            if (Enabled && e.Button == MouseButtons.Left && !Editor.Bounds.Contains(e.Location)) Editor.OpenDropDown();
        }
    }

    internal sealed class RibbonModernDateHost : RibbonModernFieldHost
    {
        internal readonly ZarpaDateEditor Editor;
        internal RibbonModernDateHost()
        {
            Cursor = Cursors.Hand; Editor = new ZarpaDateEditor { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
            Editor.GotFocus += delegate { Invalidate(); }; Editor.LostFocus += delegate { Invalidate(); }; Controls.Add(Editor);
        }
        protected override void OnResize(EventArgs e) { Editor.Location = DpiScale.Point(new Point(8, 3)); Editor.Size = new Size(Math.Max(DpiScale.X(20), Width - DpiScale.X(16)), Math.Max(DpiScale.Y(21), Height - DpiScale.Y(6))); base.OnResize(e); }
        protected override void ApplyChildColors(RibbonAppearance a) { Editor.ApplyTheme(CreateEditorTheme(a)); }
    }

    internal sealed class RibbonModernNumericHost : RibbonModernFieldHost
    {
        internal readonly ZarpaNumericEditor Editor;
        internal RibbonModernNumericHost()
        {
            Editor = new ZarpaNumericEditor { Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top };
            Editor.GotFocus += delegate { Invalidate(); }; Editor.LostFocus += delegate { Invalidate(); }; Controls.Add(Editor);
        }
        protected override void OnResize(EventArgs e) { Editor.Location = DpiScale.Point(new Point(8, 3)); Editor.Size = new Size(Math.Max(DpiScale.X(20), Width - DpiScale.X(16)), Math.Max(DpiScale.Y(18), Height - DpiScale.Y(6))); base.OnResize(e); }
        protected override void ApplyChildColors(RibbonAppearance a) { Editor.ApplyTheme(CreateEditorTheme(a)); }
    }

    internal sealed class RibbonModernCheckBoxHost : Control, IRibbonModernHost
    {
        private Color text = Color.FromArgb(26, 26, 30), border = Color.FromArgb(198, 198, 206), accent = Color.FromArgb(79, 70, 229), surface = Color.White;
        private bool hovered;
        private ZarpaDpiScale dpiScale = new ZarpaDpiScale(96, 96);
        internal bool Checked { get; set; }
        internal bool ThreeState { get; set; }
        internal event EventHandler CheckedChanged;

        internal RibbonModernCheckBoxHost()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
            Cursor = Cursors.Hand; TabStop = true;
        }
        public void ApplyAppearance(RibbonAppearance a) { text = a.TextColor; border = a.StrongBorderColor; accent = a.AccentColor; surface = a.SurfaceColor; BackColor = a.CanvasColor; Invalidate(); }
        public void ApplyDpiScale(ZarpaDpiScale scale) { if (scale == null) return; dpiScale = scale; Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { hovered = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovered = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnClick(EventArgs e) { Checked = !Checked; if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty); Invalidate(); base.OnClick(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            int boxSize = dpiScale.X(18);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; Rectangle box = new Rectangle(dpiScale.X(2), (Height - boxSize) / 2, boxSize, boxSize);
            using (GraphicsPath path = RibbonModernFieldHost.RoundedRectangle(box, dpiScale.X(4)))
            using (SolidBrush fill = new SolidBrush(Checked ? accent : surface))
            using (Pen stroke = new Pen(hovered || Focused ? accent : border)) { e.Graphics.FillPath(fill, path); e.Graphics.DrawPath(stroke, path); }
            if (Checked) TextRenderer.DrawText(e.Graphics, "✓", Font, box, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(e.Graphics, Text, Font, new Rectangle(dpiScale.X(27), 0, Width - dpiScale.X(29), Height), Enabled ? text : SystemColors.GrayText,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }
}
