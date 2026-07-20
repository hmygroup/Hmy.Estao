using Hmy.Estao.Core.Configuration;

namespace Hmy.Estao.Tests;

public sealed class ProviderCatalogTests
{
    [Theory]
    [InlineData("github-copilot", "copilot")]
    [InlineData("open-code", "opencode")]
    [InlineData("Claude", "claude")]
    public void normalizes_supported_aliases(string input, string expected)
    {
        Assert.Equal(expected, ProviderCatalog.NormalizeId(input));
    }
}
