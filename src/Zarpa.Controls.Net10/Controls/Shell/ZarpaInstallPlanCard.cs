using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace ZarpaSuite.Controls;

[ToolboxItem(true)]
[DefaultProperty("TitleText")]
public sealed class ZarpaInstallPlanCard : Control, IZarpaThemeAware
{
    private ZarpaThemeTokens theme = new(null);
    private string titleText = "Capability";
    private string operation = "Install";
    private string sourceVersion = "—";
    private string targetVersion = "—";
    private string destination = string.Empty;
    private string source = string.Empty;
    private string description = string.Empty;
    private string iconKey = "ic_fluent_arrow_download_24_regular";
    private bool installed;

    public ZarpaInstallPlanCard()
    {
        Size = new Size(760, 112);
        Margin = new Padding(6);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
    }

    [Category("Contenido"), DefaultValue("Capability")] public string TitleText { get => titleText; set { titleText = value ?? string.Empty; Invalidate(); } }
    [Category("Contenido"), DefaultValue("Install")] public string Operation { get => operation; set { operation = value ?? string.Empty; Invalidate(); } }
    [Category("Contenido"), DefaultValue("—")] public string SourceVersion { get => sourceVersion; set { sourceVersion = value ?? "—"; Invalidate(); } }
    [Category("Contenido"), DefaultValue("—")] public string TargetVersion { get => targetVersion; set { targetVersion = value ?? "—"; Invalidate(); } }
    [Category("Contenido"), DefaultValue("")] public string Destination { get => destination; set { destination = value ?? string.Empty; Invalidate(); } }
    [Category("Contenido"), DefaultValue("")] public string Source { get => source; set { source = value ?? string.Empty; Invalidate(); } }
    [Category("Contenido"), DefaultValue("")] public string Description { get => description; set { description = value ?? string.Empty; Invalidate(); } }
    [Category("Icono"), DefaultValue("")] public string IconKey { get => iconKey; set { iconKey = value ?? string.Empty; Invalidate(); } }
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)] public bool Installed { get => installed; set { installed = value; Invalidate(); } }

    public void ApplyTheme(ZarpaThemeTokens value)
    {
        theme = value;
        Font = new Font(theme.FontFamily, theme.FontSize);
        BackColor = theme.Canvas;
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(Parent?.BackColor ?? theme.Canvas);

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var card = new Rectangle(1, 1, Width - 3, Height - 3);
        var accent = Installed ? theme.Success : Operation.Equals("Update", StringComparison.OrdinalIgnoreCase) ? theme.Warning : theme.Accent;
        var fill = Installed ? ZarpaPaint.Blend(theme.Surface, theme.Success, .06F) : theme.Surface;
        ZarpaPaint.FillRounded(e.Graphics, fill, card, Math.Max(10, theme.GroupCornerRadius));
        ZarpaPaint.DrawRounded(e.Graphics, ZarpaPaint.Blend(theme.Border, accent, .30F), card,
            Math.Max(10, theme.GroupCornerRadius), 1);
        using (var stripe = new SolidBrush(accent)) e.Graphics.FillRectangle(stripe, card.Left, card.Top + 10, 4, card.Height - 20);

        var iconSurface = new Rectangle(18, 18, 42, 42);
        ZarpaPaint.FillRounded(e.Graphics, ZarpaPaint.Blend(theme.SurfaceRaised, accent, .12F), iconSurface, theme.CornerRadius);
        FluentIconCatalog.TryDraw(e.Graphics, iconKey, new Rectangle(28, 28, 22, 22), accent, 21F);
        using var titleFont = new Font(Font, FontStyle.Bold);
        TextRenderer.DrawText(e.Graphics, titleText, titleFont, new Rectangle(76, 13, Width - 270, 24), theme.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(e.Graphics, description, Font, new Rectangle(76, 38, Width - 310, 20), theme.TextMuted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        DrawPill(e.Graphics, new Rectangle(Width - 172, 14, 148, 24), Operation.ToUpperInvariant(), accent);
        TextRenderer.DrawText(e.Graphics, $"{sourceVersion}  →  {targetVersion}", Font,
            new Rectangle(76, 66, 180, 20), theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(e.Graphics, $"Source  {source}", Font, new Rectangle(270, 66, 190, 20), theme.TextMuted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(e.Graphics, destination, Font, new Rectangle(470, 66, Width - 490, 20), theme.TextMuted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        TextRenderer.DrawText(e.Graphics, Installed ? "INSTALLED" : Operation.Equals("Update", StringComparison.OrdinalIgnoreCase) ? "UPDATE AVAILABLE" : "READY TO APPLY",
            Font, new Rectangle(Width - 190, 88, 166, 17), accent, TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
    }

    private void DrawPill(Graphics graphics, Rectangle bounds, string text, Color accent)
    {
        ZarpaPaint.FillRounded(graphics, ZarpaPaint.Blend(theme.Surface, accent, .14F), bounds, theme.CornerRadius);
        TextRenderer.DrawText(graphics, text, Font, bounds, accent,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}
