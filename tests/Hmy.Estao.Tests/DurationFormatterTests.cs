using Hmy.Estao.Core.Formatting;

namespace Hmy.Estao.Tests;

public sealed class DurationFormatterTests
{
    [Theory]
    [InlineData(0, 0, 0, 1, "1m")]
    [InlineData(0, 0, 32, 0, "32m")]
    [InlineData(0, 7, 32, 0, "7h 32m")]
    [InlineData(1, 0, 0, 0, "1d 0h")]
    [InlineData(5, 7, 32, 0, "5d 7h")]
    [InlineData(5, 19, 32, 0, "5d 19h")]
    public void compact_output_uses_the_most_appropriate_units(
        int days,
        int hours,
        int minutes,
        int seconds,
        string expected)
    {
        var duration = new TimeSpan(days, hours, minutes, seconds);

        Assert.Equal(expected, DurationFormatter.ToCompact(duration));
    }
}
