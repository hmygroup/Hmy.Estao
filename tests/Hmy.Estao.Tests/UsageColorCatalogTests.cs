using Hmy.Estao.Core.Configuration;

namespace Hmy.Estao.Tests;

public sealed class UsageColorCatalogTests
{
    private readonly UsageColorConfig _config = new()
    {
        Enabled = true,
        WarningPercent = 75,
        WarningColor = "#F59E0B",
        CriticalPercent = 90,
        CriticalColor = "#EF4444"
    };

    [Theory]
    [InlineData(.7499, UsageColorLevel.Default)]
    [InlineData(.75, UsageColorLevel.Warning)]
    [InlineData(.8999, UsageColorLevel.Warning)]
    [InlineData(.90, UsageColorLevel.Critical)]
    [InlineData(2, UsageColorLevel.Critical)]
    public void level_for_uses_configured_thresholds(double used, UsageColorLevel expected)
    {
        Assert.Equal(expected, UsageColorCatalog.LevelFor(_config, used));
    }

    [Fact]
    public void disabled_colors_always_use_the_default_level()
    {
        _config.Enabled = false;

        Assert.Equal(UsageColorLevel.Default, UsageColorCatalog.LevelFor(_config, 1));
        Assert.Null(UsageColorCatalog.ColorFor(_config, 1));
    }
}
