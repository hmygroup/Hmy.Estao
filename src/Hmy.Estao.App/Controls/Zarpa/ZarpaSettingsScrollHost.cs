using System.ComponentModel;
using ZarpaSuite.Controls;

namespace Hmy.Estao.App.Controls.Zarpa;

/// <summary>
/// Settings viewport that uses the same painted scrollbar as the usage
/// popover. The native WinForms AutoScroll chrome is deliberately disabled.
/// </summary>
internal sealed class ZarpaSettingsScrollHost : Panel, IZarpaThemeAware
{
    private readonly Panel _content = new();
    private readonly ZarpaScrollBar _scrollBar = new() { Orientation = Orientation.Vertical, Dock = DockStyle.Right, Width = 9 };
    private ZarpaThemeTokens? _theme;

    public ZarpaSettingsScrollHost()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Color.Transparent;
        _content.BackColor = Color.Transparent;
        _content.Dock = DockStyle.None;
        _content.Margin = Padding.Empty;
        _content.Padding = Padding.Empty;
        _content.ControlAdded += (_, args) =>
        {
            if (args.Control is null) return;
            WireMouseWheel(args.Control);
            args.Control.SizeChanged += (_, _) => UpdateScrollRange();
            UpdateScrollRange();
        };
        _content.ControlRemoved += (_, _) => UpdateScrollRange();
        _content.Layout += (_, _) => UpdateScrollRange();
        _scrollBar.ValueChanged += (_, _) => LayoutContent();
        Controls.Add(_content);
        Controls.Add(_scrollBar);
        MouseWheel += (_, args) => ScrollBy(args.Delta);
    }

    [Browsable(false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Control Content
    {
        get => _content.Controls.Count == 1 ? _content.Controls[0] : _content;
        set
        {
            while (_content.Controls.Count > 0) _content.Controls[0].Dispose();
            value.Dock = DockStyle.Top;
            value.AutoSize = true;
            _content.Controls.Add(value);
            WireMouseWheel(value);
            UpdateScrollRange();
        }
    }

    public void ApplyTheme(ZarpaThemeTokens value)
    {
        _theme = value;
        BackColor = value.Canvas;
        _content.BackColor = value.Canvas;
        _scrollBar.ApplyTheme(value);
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateScrollRange();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ScrollBy(e.Delta);
    }

    private void UpdateScrollRange()
    {
        if (IsDisposed || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
        var viewportWidth = Math.Max(1, ClientSize.Width - _scrollBar.Width);
        var contentHeight = _content.Padding.Vertical;
        foreach (Control child in _content.Controls)
        {
            if (child.Visible) contentHeight += child.Height + child.Margin.Vertical;
        }

        _content.Width = viewportWidth;
        _content.Height = Math.Max(ClientSize.Height, contentHeight);
        _scrollBar.SetRange(_content.Height, ClientSize.Height);
        LayoutContent();
    }

    private void LayoutContent()
    {
        _content.Left = 0;
        _content.Top = -_scrollBar.Value;
        _content.Width = Math.Max(1, ClientSize.Width - _scrollBar.Width);
    }

    private void ScrollBy(int delta)
    {
        if (delta == 0 || !_scrollBar.Enabled) return;
        _scrollBar.Value -= Math.Sign(delta) * Math.Max(1, ClientSize.Height / 5);
    }

    private void WireMouseWheel(Control control)
    {
        control.MouseWheel -= ChildMouseWheel;
        control.MouseWheel += ChildMouseWheel;
        foreach (Control child in control.Controls) WireMouseWheel(child);
    }

    private void ChildMouseWheel(object? sender, MouseEventArgs e) => ScrollBy(e.Delta);
}
