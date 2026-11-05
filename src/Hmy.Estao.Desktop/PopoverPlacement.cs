namespace Hmy.Estao.Desktop;

internal static class PopoverPlacement
{
    public static Rectangle Calculate(Size requestedSize, Rectangle anchor, Rectangle workingArea)
    {
        // On small displays, fitting the whole window takes priority over its usual size.
        var width = Math.Min(requestedSize.Width, workingArea.Width);
        var height = Math.Min(requestedSize.Height, workingArea.Height);
        var left = Math.Clamp(anchor.Left, workingArea.Left, workingArea.Right - width);
        var top = anchor.Top - height;
        if (top < workingArea.Top && anchor.Bottom + height <= workingArea.Bottom)
            top = anchor.Bottom;
        top = Math.Clamp(top, workingArea.Top, workingArea.Bottom - height);
        return new Rectangle(left, top, width, height);
    }
}
