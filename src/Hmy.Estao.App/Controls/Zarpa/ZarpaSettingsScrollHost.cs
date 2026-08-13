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
    private readonly System.Windows.Forms.Timer _scrollRangeTimer;
    private ZarpaThemeTokens? _theme;
    private bool _updatingScrollRange;

    public ZarpaSettingsScrollHost()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Color.Transparent;
        _content.BackColor = Color.Transparent;
        _content.Dock = DockStyle.None;
        _content.Margin = Padding.Empty;
        _content.Padding = Padding.Empty;
        _scrollRangeTimer = new System.Windows.Forms.Timer { Interval = 50 };
        _scrollRangeTimer.Tick += (_, _) =>
        {
            _scrollRangeTimer.Stop();
            UpdateScrollRange();
        };
        _content.ControlAdded += (_, args) =>
        {
            if (args.Control is null) return;
            WireMouseWheel(args.Control);
            args.Control.SizeChanged += (_, _) => RequestScrollRangeUpdate();
            RequestScrollRangeUpdate();
        };
        _content.ControlRemoved += (_, _) => RequestScrollRangeUpdate();
        _content.Layout += (_, _) => RequestScrollRangeUpdate();
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
            RequestScrollRangeUpdate();
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

    public void ScrollTo(Control control)
    {
        if (control is null || control.IsDisposed) return;
        _content.PerformLayout();
        var offset = 0;
        if (IsHandleCreated && control.IsHandleCreated)
        {
            var targetPoint = control.PointToScreen(Point.Empty);
            var viewportPoint = PointToScreen(Point.Empty);
            offset = _scrollBar.Value + targetPoint.Y - viewportPoint.Y;
        }
        else
        {
            Control? current = control;
            while (current is not null && !ReferenceEquals(current, _content))
            {
                offset += current.Top;
                current = current.Parent;
            }
            if (current is null) return;
        }
        _scrollBar.Value = Math.Clamp(offset, 0, _scrollBar.MaximumValue);
        LayoutContent();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // The side navigation animates its width. Reflowing the full settings
        // tree on every animation tick is the expensive part, so coalesce the
        // range recalculation until the resize burst has settled.
        LayoutContent();
        RequestScrollRangeUpdate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ScrollBy(e.Delta);
    }

    private void UpdateScrollRange()
    {
        if (_updatingScrollRange || IsDisposed || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;

        _updatingScrollRange = true;
        var contentWidthChanged = false;
        try
        {
            var viewportWidth = Math.Max(1, ClientSize.Width - _scrollBar.Width);
            var contentHeight = _content.Padding.Vertical;
            foreach (Control child in _content.Controls)
            {
                if (child.Visible) contentHeight += child.Height + child.Margin.Vertical;
            }

            if (_content.Width != viewportWidth)
            {
                _content.Width = viewportWidth;
                contentWidthChanged = true;
            }
            var targetContentHeight = Math.Max(ClientSize.Height, contentHeight);
            if (_content.Height != targetContentHeight) _content.Height = targetContentHeight;
            _scrollBar.SetRange(_content.Height, ClientSize.Height);
            LayoutContent();
        }
        finally
        {
            _updatingScrollRange = false;
            if (contentWidthChanged) RequestScrollRangeUpdate();
        }
    }

    private void RequestScrollRangeUpdate()
    {
        if (_updatingScrollRange || IsDisposed || !IsHandleCreated) return;
        _scrollRangeTimer.Stop();
        _scrollRangeTimer.Start();
    }

    private void LayoutContent()
    {
        _content.Left = 0;
        _content.Top = -_scrollBar.Value;
    }

    private void ScrollBy(int delta)
    {
        _scrollBar.ScrollByWheel(delta);
    }

    private void WireMouseWheel(Control control)
    {
        control.MouseWheel -= ChildMouseWheel;
        control.MouseWheel += ChildMouseWheel;
        foreach (Control child in control.Controls) WireMouseWheel(child);
    }

    private void ChildMouseWheel(object? sender, MouseEventArgs e) => ScrollBy(e.Delta);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _scrollRangeTimer.Stop();
            _scrollRangeTimer.Dispose();
        }

        base.Dispose(disposing);
    }
}
