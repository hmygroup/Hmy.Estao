using System.Drawing;
using Hmy.Estao.Desktop;

namespace Hmy.Estao.Desktop.Tests;

public sealed class PopoverPlacementTests
{
    private static readonly Size PopoverSize = new(500, 700);

    [Fact]
    public void AlignsWithOverlayLeftAndOpensAboveWhenThereIsRoom()
    {
        var overlay = new Rectangle(600, 1000, 300, 38);
        var result = PopoverPlacement.Calculate(PopoverSize, overlay, new Rectangle(0, 0, 1920, 1040));
        Assert.Equal(new Rectangle(600, 300, 500, 700), result);
        Assert.Equal(overlay.Left, result.Left);
        Assert.Equal(overlay.Top, result.Bottom);
    }

    [Fact]
    public void ShiftsLeftWhenThePopoverWouldOverflowTheRightEdge()
    {
        var result = PopoverPlacement.Calculate(PopoverSize, new Rectangle(1750, 1000, 150, 38), new Rectangle(0, 0, 1920, 1040));
        Assert.Equal(new Rectangle(1420, 300, 500, 700), result);
    }

    [Fact]
    public void OpensBelowOverlayWhenThereIsNotEnoughRoomAbove()
    {
        var result = PopoverPlacement.Calculate(PopoverSize, new Rectangle(500, 100, 300, 38), new Rectangle(0, 0, 1920, 1040));
        Assert.Equal(new Rectangle(500, 138, 500, 700), result);
    }

    [Fact]
    public void KeepsTheWholeWindowVisibleWhenNeitherSideHasEnoughSpace()
    {
        var result = PopoverPlacement.Calculate(PopoverSize, new Rectangle(500, 500, 300, 38), new Rectangle(0, 0, 1920, 1040));
        Assert.Equal(new Rectangle(500, 0, 500, 700), result);
    }

    [Fact]
    public void RespectsNegativeMonitorCoordinatesAndTaskbarWorkingArea()
    {
        var area = new Rectangle(-1920, 40, 1920, 1000);
        var result = PopoverPlacement.Calculate(PopoverSize, new Rectangle(-300, 1000, 250, 38), area);
        Assert.Equal(new Rectangle(-500, 300, 500, 700), result);
        Assert.True(area.Contains(result));
    }

    [Fact]
    public void ShrinksToFitWhenMonitorIsSmallerThanTheUsualPopover()
    {
        var area = new Rectangle(1920, 0, 400, 600);
        var result = PopoverPlacement.Calculate(PopoverSize, new Rectangle(2000, 500, 200, 38), area);
        Assert.Equal(area, result);
    }

    [Fact]
    public void TrayAnchorStillOpensAboveTheCursor()
    {
        var result = PopoverPlacement.Calculate(PopoverSize, new Rectangle(new Point(700, 1000), Size.Empty), new Rectangle(0, 0, 1920, 1040));
        Assert.Equal(new Rectangle(700, 300, 500, 700), result);
    }
}
