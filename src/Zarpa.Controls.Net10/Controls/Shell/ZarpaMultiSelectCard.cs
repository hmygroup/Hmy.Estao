using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ZarpaSuite.Controls;

[ToolboxItem(true)]
[DefaultProperty("TitleText")]
public class ZarpaMultiSelectCard : Control, IZarpaThemeAware
{
    private ZarpaThemeTokens theme = new(null);
    private string titleText = "Capability";
    private string descriptionText = string.Empty;
    private string metadataText = string.Empty;
    private string badgeText = string.Empty;
    private string badgeStyle = "neutral";
    private string iconKey = string.Empty;
    private bool selected;
    private bool installed;
    private bool updateAvailable;
    private bool hot;
    private bool selectable = true;

    public ZarpaMultiSelectCard()
    {
        Size = new Size(336, 184);
        Margin = new Padding(7);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.CheckButton;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.Selectable, true);
    }

    [Category("Contenido"), DefaultValue("Capability")]
    public string TitleText { get => titleText; set { titleText = value ?? string.Empty; AccessibleName = titleText; Invalidate(); } }
    [Category("Contenido"), DefaultValue("")]
    public string DescriptionText { get => descriptionText; set { descriptionText = value ?? string.Empty; AccessibleDescription = value; Invalidate(); } }
    [Category("Contenido"), DefaultValue("")]
    public string MetadataText { get => metadataText; set { metadataText = value ?? string.Empty; Invalidate(); } }
    [Category("Contenido"), DefaultValue("")]
    public string BadgeText { get => badgeText; set { badgeText = value ?? string.Empty; Invalidate(); } }
    [Category("Contenido"), DefaultValue("neutral")]
    public string BadgeStyle { get => badgeStyle; set { badgeStyle = value ?? "neutral"; Invalidate(); } }
    [Category("Icono"), DefaultValue("")]
    public string IconKey { get => iconKey; set { iconKey = value ?? string.Empty; Invalidate(); } }
    [Category("Estado"), DefaultValue(false)]
    public bool Selected
    {
        get => selected;
        set
        {
            if (selected == value) return;
            selected = value;
            AccessibleDefaultActionDescription = value ? "Remove from selection" : "Add to selection";
            Invalidate();
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }
    [Category("Estado"), DefaultValue(true)]
    public bool Selectable { get => selectable; set { selectable = value; Invalidate(); } }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Installed
    {
        get => installed;
        set
        {
            if (installed == value) return;
            installed = value;
            Enabled = !value;
            if (value) selected = false;
            BadgeText = value ? "INSTALLED" : selected ? "SELECTED" : BadgeText;
            BadgeStyle = value ? "success" : selected ? "accent" : "neutral";
            AccessibleDefaultActionDescription = value ? "Already installed" : "Add to selection";
            Invalidate();
        }
    }

    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool UpdateAvailable
    {
        get => updateAvailable;
        set
        {
            updateAvailable = value;
            if (value)
            {
                Enabled = true;
                BadgeText = "UPDATE";
                BadgeStyle = "warning";
            }
            Invalidate();
        }
    }

    public event EventHandler SelectionChanged;

    public void ApplyTheme(ZarpaThemeTokens value)
    {
        theme = value;
        Font = new Font(theme.FontFamily, theme.FontSize);
        BackColor = theme.Canvas;
        ForeColor = theme.Text;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = false; Invalidate(); }
    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (Selectable && !Installed && Enabled) Selected = !Selected;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter) { OnClick(EventArgs.Empty); e.Handled = true; e.SuppressKeyPress = true; }
    }
    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(Parent?.BackColor ?? theme.Canvas);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var card = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
        var radius = Math.Max(8, theme.GroupCornerRadius);
        var statusAccent = UpdateAvailable ? theme.Warning : theme.Accent;
        var selectedAccent = ZarpaPaint.Blend(statusAccent, theme.SurfaceRaised, .28F);
        var fill = UpdateAvailable && !Selected ? ZarpaPaint.Blend(theme.Surface, theme.Warning, .10F) :
            Selected ? ZarpaPaint.Blend(theme.Surface, selectedAccent, .18F) : hot ?
            ZarpaPaint.Blend(theme.Surface, theme.SurfaceRaised, .5F) : theme.Surface;
        ZarpaPaint.FillRounded(e.Graphics, fill, card, radius);
        ZarpaPaint.DrawRounded(e.Graphics, UpdateAvailable ? selectedAccent : Selected ? selectedAccent : hot ? theme.BorderStrong : theme.Border,
            card, radius, Selected ? 3 : 1);
        if (Selected)
            using (var stripe = new SolidBrush(selectedAccent))
                e.Graphics.FillRectangle(stripe, card.Left, card.Top + radius, 4, Math.Max(1, card.Height - radius * 2));

        var iconSurface = new Rectangle(16, 16, 46, 46);
        ZarpaPaint.FillRounded(e.Graphics, Selected ? selectedAccent : theme.SurfaceRaised, iconSurface, theme.CornerRadius);
        FluentIconCatalog.TryDraw(e.Graphics, iconKey,
            new Rectangle(iconSurface.Left + 11, iconSurface.Top + 11, 24, 24),
            Selected ? theme.Text : theme.Accent, 22F);

        using var titleFont = new Font(Font, FontStyle.Bold);
        TextRenderer.DrawText(e.Graphics, titleText, titleFont, new Rectangle(78, 14, Width - 184, 25),
            theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        DrawBadge(e.Graphics);
        TextRenderer.DrawText(e.Graphics, descriptionText, Font, new Rectangle(78, 45, Width - 94, 67),
            theme.TextMuted, TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        using (var divider = new Pen(theme.Border)) e.Graphics.DrawLine(divider, 16, Height - 39, Width - 16, Height - 39);
        TextRenderer.DrawText(e.Graphics, metadataText, Font, new Rectangle(16, Height - 32, Width - 116, 20),
            theme.TextMuted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(e.Graphics, Installed ? "INSTALLED" : UpdateAvailable ? "UPDATE AVAILABLE" : Selected ? "SELECTED" : "SELECT",
            Font, new Rectangle(Width - 100, Height - 32, 84, 20),
            Installed ? theme.Success : UpdateAvailable ? theme.Warning : Selected ? selectedAccent : theme.TextMuted,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(card, -4, -4), theme.Text, fill);
    }

    private void DrawBadge(Graphics graphics)
    {
        if (string.IsNullOrWhiteSpace(badgeText)) return;
        var width = Math.Min(112, TextRenderer.MeasureText(graphics, badgeText, Font).Width + 18);
        var bounds = new Rectangle(Width - width - 16, 15, width, 25);
        var accent = badgeStyle.Equals("success", StringComparison.OrdinalIgnoreCase) ? theme.Success :
            badgeStyle.Equals("warning", StringComparison.OrdinalIgnoreCase) ? theme.Warning :
            badgeStyle.Equals("accent", StringComparison.OrdinalIgnoreCase) ? theme.Accent : theme.TextMuted;
        var surface = badgeStyle.Equals("neutral", StringComparison.OrdinalIgnoreCase) ? theme.SurfaceRaised :
            ZarpaPaint.Blend(theme.Surface, accent, .14F);
        ZarpaPaint.FillRounded(graphics, surface, bounds, theme.CornerRadius);
        TextRenderer.DrawText(graphics, badgeText, Font, bounds, accent,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}
