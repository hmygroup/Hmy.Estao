using Hmy.Estao.Core.Harnesses.Installer;
using Hmy.Estao.Desktop.HarnessHub;

namespace Hmy.Estao.Desktop.Tests;

public sealed class HubToolInventoryTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "estao-inventory-" + Guid.NewGuid().ToString("N"));
    private HarnessAdapter Adapter(string id) => new(id, _home, _ => null);
    private static HubSkill Item(string id, string type) => new(id, id, "Utility", true, false, [], [],
        @"\\server\share\folder", Curated: true, ItemType: type);

    [Fact]
    public async Task McpUsesNativeConfigurationsAndRetainsUnlistedServers()
    {
        var codex = Adapter("codex");
        Directory.CreateDirectory(codex.Profile);
        await File.WriteAllTextAsync(codex.McpPath, "[mcp_servers.database]\ncommand = 'db'\n");
        var claude = Adapter("claude");
        await File.WriteAllTextAsync(claude.McpPath, """{"mcpServers":{"extra":{"command":"db","env":{"TOKEN":"secret"}}}}""");
        var result = await HubToolInventory.ScanAsync(["codex", "claude"], Adapter, [Item("database", "mcp")]);
        Assert.Equal("codex", Assert.Single(result.Single(item => item.Id == "database").Occurrences).ToolId);
        Assert.Equal("claude", Assert.Single(result.Single(item => item.Name == "extra").Occurrences).ToolId);
        Assert.DoesNotContain("secret", System.Text.Json.JsonSerializer.Serialize(result));
    }

    [Fact]
    public async Task CliExecutableIsSharedAcrossDetectedToolsAndNeverExecuted()
    {
        var result = await HubToolInventory.ScanAsync(["codex", "claude"], Adapter,
            [Item("utility", "cli"), Item("missing", "cli")], findExecutable: name => name == "utility" ? @"C:\bin\utility.cmd" : null);
        Assert.Equal(2, result.Single(item => item.Id == "utility").Occurrences.Count);
        Assert.All(result.Single(item => item.Id == "utility").Occurrences, copy => Assert.True(copy.Shared));
        Assert.Empty(result.Single(item => item.Id == "missing").Occurrences);
    }

    [Fact]
    public async Task JsoncAndCopilotReuseNativeMcpReader()
    {
        foreach (var id in new[] { "opencode", "copilot" })
        {
            var adapter = Adapter(id);
            Directory.CreateDirectory(adapter.Profile);
            var json = id == "opencode"
                ? """{/* comment */ "mcp":{"servers":{"database":{"type":"local","command":["db"],},},},}"""
                : """{"mcpServers":{"database":{"command":"db"}}}""";
            await File.WriteAllTextAsync(adapter.McpPath, json);
        }
        var result = await HubToolInventory.ScanAsync(["opencode", "copilot"], Adapter, [Item("database", "mcp")]);
        Assert.Equal(2, Assert.Single(result).Occurrences.Count);
        Assert.All(Assert.Single(result).Occurrences, copy => Assert.Null(copy.Error));
    }

    [Fact]
    public async Task MalformedConfigurationIsUnknownRatherThanMissing()
    {
        var adapter = Adapter("opencode");
        Directory.CreateDirectory(adapter.Profile);
        await File.WriteAllTextAsync(adapter.McpPath, "{invalid");
        var result = await HubToolInventory.ScanAsync(["opencode"], Adapter, [Item("database", "mcp")]);
        var copy = Assert.Single(Assert.Single(result).Occurrences);
        Assert.NotNull(copy.Error);
        Assert.Empty(copy.Path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_home)) Directory.Delete(_home, true);
    }
}
