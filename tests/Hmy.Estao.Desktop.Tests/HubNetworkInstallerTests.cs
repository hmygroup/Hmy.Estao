using System.Text.Json.Nodes;
using Hmy.Estao.Core.Harnesses.Installer;
using Hmy.Estao.Desktop.HarnessHub;

namespace Hmy.Estao.Desktop.Tests;

public sealed class HubNetworkInstallerTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "estao-packages-" + Guid.NewGuid().ToString("N"));
    private string Data => Path.Combine(_home, "data");
    private string _userPath = "";
    private HarnessAdapter Adapter(string id) => new(id, _home, _ => null);
    private HubNetworkInstaller Installer(Func<string, string?>? find = null, Action<string>? write = null) =>
        new(Data, Adapter, find ?? (_ => null), () => _userPath, write ?? (value => _userPath = value));

    private HubSkill Package(string type, string id = "utility", string? executableName = null)
    {
        var folder = Path.Combine(_home, "share", type, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, (executableName ?? id) + ".exe"), "version one");
        return new(id, id, "Utility", true, true, HarnessAdapter.Ids, [], folder, Curated: true, ItemType: type);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    [InlineData("copilot")]
    [InlineData("opencode")]
    public async Task McpInstallUpdateUninstallPreservesOtherServersAndSettings(string id)
    {
        var item = Package("mcp");
        var adapter = Adapter(id);
        Directory.CreateDirectory(Path.GetDirectoryName(adapter.McpPath)!);
        var before = id == "codex" ? "model = 'example'\n[mcp_servers.other]\ncommand = 'other'\n"
            : id == "opencode" ? """{/* comment */ "model":"example", "mcp":{"servers":{"other":{"type":"local","command":["other"]}}}}"""
            : """{"model":"example","mcpServers":{"other":{"command":"other"}}}""";
        await File.WriteAllTextAsync(adapter.McpPath, before);
        await Installer().InstallAsync(item, [id], default);
        var config = await HubMcpConfiguration.ReadAsync(adapter, default);
        var first = config.Executable(item.Id)!;
        Assert.True(File.Exists(first));
        Assert.Equal("version one", await File.ReadAllTextAsync(first));
        Assert.Equal("other", config.Executable("other"));
        if (id == "opencode")
        {
            var root = JsonNode.Parse(await File.ReadAllTextAsync(adapter.McpPath))!;
            Assert.Equal("local", root["mcp"]!["servers"]!["utility"]!["type"]!.GetValue<string>());
        }
        await File.WriteAllTextAsync(Path.Combine(item.Source!, "utility.exe"), "version two");
        await Installer().InstallAsync(item, [id], default);
        config = await HubMcpConfiguration.ReadAsync(adapter, default);
        var updated = config.Executable(item.Id)!;
        Assert.NotEqual(first, updated);
        Assert.False(File.Exists(first));
        Assert.Equal("version two", await File.ReadAllTextAsync(updated));
        await Installer().UninstallAsync(item, [id], default);
        config = await HubMcpConfiguration.ReadAsync(adapter, default);
        Assert.Null(config.Executable(item.Id));
        Assert.False(File.Exists(updated));
        Assert.Equal("other", config.Executable("other"));
        Assert.Contains("example", await File.ReadAllTextAsync(adapter.McpPath));
        Assert.Contains(Directory.GetFiles(Path.Combine(Data, "network-backups"), "*", SearchOption.AllDirectories),
            path => Path.GetFileName(path) == Path.GetFileName(adapter.McpPath) && File.ReadAllText(path) == before);
    }

    [Fact]
    public async Task CliIsInstalledGloballyUpdatedAndRemovedWithoutAnyHarness()
    {
        var item = Package("cli", "tengu-cli", "tengu");
        var installer = Installer();
        await installer.InstallAsync(item, [], default);
        var installed = installer.FindCli(item)!;
        Assert.EndsWith("tengu.exe", installed);
        Assert.True(File.Exists(installed));
        await File.WriteAllTextAsync(Path.Combine(item.Source!, "tengu.exe"), "new version");
        await installer.InstallAsync(item, [], default);
        var updated = installer.FindCli(item)!;
        Assert.NotEqual(installed, updated);
        Assert.False(File.Exists(installed));
        Assert.Equal("new version", await File.ReadAllTextAsync(updated));
        await installer.UninstallAsync(item, [], default);
        Assert.Null(installer.FindCli(item));
        Assert.False(File.Exists(updated));
        Assert.DoesNotContain(Path.GetDirectoryName(updated)!, _userPath);
    }

    [Fact]
    public async Task ManualCliIsRemovedButOtherCommandsInSharedBinArePreserved()
    {
        var item = Package("cli");
        var bin = Path.Combine(_home, "bin");
        Directory.CreateDirectory(bin);
        var manual = Path.Combine(bin, "utility.exe");
        await File.WriteAllTextAsync(manual, "manual");
        await File.WriteAllTextAsync(Path.Combine(bin, "other.exe"), "other");
        _userPath = bin;
        await Installer().UninstallAsync(item, [], default);
        Assert.False(File.Exists(manual));
        Assert.True(File.Exists(Path.Combine(bin, "other.exe")));
        Assert.Equal(bin, _userPath);
    }

    [Fact]
    public async Task ManualMcpPublishedFolderAndDependenciesAreRemoved()
    {
        var item = Package("mcp");
        var folder = Path.Combine(_home, "manual-package");
        Directory.CreateDirectory(Path.Combine(folder, "es"));
        var manual = Path.Combine(folder, "Schema.exe");
        await File.WriteAllTextAsync(manual, "manual");
        await File.WriteAllTextAsync(Path.ChangeExtension(manual, ".deps.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(folder, "library.dll"), "dependency");
        await File.WriteAllTextAsync(Path.Combine(folder, "es", "resources.dll"), "resource");
        var config = await HubMcpConfiguration.ReadAsync(Adapter("claude"), default);
        config.Install(item.Id, manual);
        await config.WriteAsync(Data, default);
        await Installer().UninstallAsync(item, ["claude"], default);
        Assert.False(Directory.Exists(folder));
        Assert.Null((await HubMcpConfiguration.ReadAsync(Adapter("claude"), default)).Executable(item.Id));
    }

    [Fact]
    public async Task RemovingOneMcpTargetKeepsFilesUsedByAnotherTarget()
    {
        var item = Package("mcp");
        await Installer().InstallAsync(item, ["claude", "codex"], default);
        var executable = (await HubMcpConfiguration.ReadAsync(Adapter("claude"), default)).Executable(item.Id)!;
        await Installer().UninstallAsync(item, ["claude"], default);
        Assert.True(File.Exists(executable));
        Assert.Equal(executable, (await HubMcpConfiguration.ReadAsync(Adapter("codex"), default)).Executable(item.Id));
        await Installer().UninstallAsync(item, ["codex"], default);
        Assert.False(File.Exists(executable));
    }

    [Fact]
    public async Task PathFailureRestoresManuallyInstalledCli()
    {
        var item = Package("cli");
        var bin = Path.Combine(_home, "bin");
        Directory.CreateDirectory(bin);
        var manual = Path.Combine(bin, "utility.exe");
        await File.WriteAllTextAsync(manual, "manual");
        _userPath = bin;
        var calls = 0;
        var installer = Installer(write: value => { if (++calls == 1) throw new IOException("Simulated PATH failure"); _userPath = value; });
        await Assert.ThrowsAsync<IOException>(() => installer.InstallAsync(item, [], default));
        Assert.Equal("manual", await File.ReadAllTextAsync(manual));
        Assert.Equal(bin, _userPath);
    }

    [Fact]
    public async Task LockedManualExecutableLeavesFilesAndConfigUntouched()
    {
        var item = Package("mcp");
        var folder = Path.Combine(_home, "manual");
        Directory.CreateDirectory(folder);
        var manual = Path.Combine(folder, "utility.exe");
        await File.WriteAllTextAsync(manual, "manual");
        var config = await HubMcpConfiguration.ReadAsync(Adapter("claude"), default);
        config.Install(item.Id, manual);
        await config.WriteAsync(Data, default);
        var before = await File.ReadAllBytesAsync(Adapter("claude").McpPath);
        using var locked = File.Open(manual, FileMode.Open, FileAccess.Read, FileShare.Read);
        await Assert.ThrowsAsync<IOException>(() => Installer().UninstallAsync(item, ["claude"], default));
        Assert.Equal(before, await File.ReadAllBytesAsync(Adapter("claude").McpPath));
        Assert.True(File.Exists(manual));
    }

    [Fact]
    public async Task MalformedConfigurationAbortsBeforeInstallingOtherTargets()
    {
        var item = Package("mcp");
        Directory.CreateDirectory(Adapter("codex").Profile);
        await File.WriteAllTextAsync(Adapter("codex").McpPath, "[malformed");
        await Assert.ThrowsAsync<InvalidDataException>(() => Installer().InstallAsync(item, ["claude", "codex"], default));
        Assert.False(File.Exists(Adapter("claude").McpPath));
    }

    [Fact]
    public async Task NewMcpInstallDoesNotReadUnselectedToolsConfigurations()
    {
        var item = Package("mcp");
        Directory.CreateDirectory(Adapter("codex").Profile);
        await File.WriteAllTextAsync(Adapter("codex").McpPath, "[malformed");
        await Installer().InstallAsync(item, ["claude"], default);
        Assert.NotNull((await HubMcpConfiguration.ReadAsync(Adapter("claude"), default)).Executable(item.Id));
    }

    [Fact]
    public async Task RuntimeHostsAreNeverDeletedForMcpUninstall()
    {
        var item = Package("mcp");
        var config = await HubMcpConfiguration.ReadAsync(Adapter("claude"), default);
        config.Install(item.Id, @"C:\dotnet\dotnet.exe");
        await config.WriteAsync(Data, default);
        await Assert.ThrowsAsync<InvalidDataException>(() => Installer().UninstallAsync(item, ["claude"], default));
        Assert.NotNull((await HubMcpConfiguration.ReadAsync(Adapter("claude"), default)).Executable(item.Id));
    }

    [Fact]
    public async Task FailedSecondTargetRestoresFirstConfigurationAndManualFiles()
    {
        var item = Package("mcp");
        var manualFolder = Path.Combine(_home, "utility");
        Directory.CreateDirectory(manualFolder);
        var executable = Path.Combine(manualFolder, "utility.exe");
        await File.WriteAllTextAsync(executable, "manual");
        foreach (var id in new[] { "claude", "codex" })
        {
            var config = await HubMcpConfiguration.ReadAsync(Adapter(id), default);
            config.Install(item.Id, executable);
            await config.WriteAsync(Data, default);
        }
        var before = await File.ReadAllBytesAsync(Adapter("claude").McpPath);
        using var locked = File.Open(Adapter("codex").McpPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var exception = await Record.ExceptionAsync(() => Installer().InstallAsync(item, ["claude", "codex"], default));
        Assert.True(exception is IOException or UnauthorizedAccessException);
        Assert.Equal(before, await File.ReadAllBytesAsync(Adapter("claude").McpPath));
        Assert.Equal("manual", await File.ReadAllTextAsync(executable));
        Assert.Empty(Directory.GetFiles(Path.Combine(Data, "hub-packages"), "*.exe", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task MissingRuntimeDoesNotActivateThePackage()
    {
        var item = Package("cli");
        await File.WriteAllTextAsync(Path.Combine(item.Source!, "utility.runtimeconfig.json"),
            """{"runtimeOptions":{"framework":{"name":"Microsoft.NETCore.App","version":"999.0.0"}}}""");
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => Installer().InstallAsync(item, [], default));
        Assert.Contains("999.0", exception.Message);
        Assert.Equal("", _userPath);
        Assert.Empty(Directory.GetFiles(Path.Combine(Data, "hub-packages"), "*.exe", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ExistingMcpEnvironmentAndArgumentsSurviveUpdate()
    {
        var item = Package("mcp");
        Directory.CreateDirectory(Path.GetDirectoryName(Adapter("claude").McpPath)!);
        await File.WriteAllTextAsync(Adapter("claude").McpPath,
            """{"mcpServers":{"Utility":{"command":"old.exe","args":["--stdio"],"env":{"EXAMPLE_SETTING":"value"}}}}""");
        await Installer().InstallAsync(item, ["claude"], default);
        var root = JsonNode.Parse(await File.ReadAllTextAsync(Adapter("claude").McpPath))!;
        Assert.Null(root["mcpServers"]!["utility"]);
        Assert.Equal("--stdio", root["mcpServers"]!["Utility"]!["args"]![0]!.GetValue<string>());
        Assert.Equal("value", root["mcpServers"]!["Utility"]!["env"]!["EXAMPLE_SETTING"]!.GetValue<string>());
        await Installer().UninstallAsync(item, ["claude"], default);
        root = JsonNode.Parse(await File.ReadAllTextAsync(Adapter("claude").McpPath))!;
        Assert.Empty((JsonObject)root["mcpServers"]!);
    }

    [Fact]
    public async Task CancelledInstallDoesNotChangePathOrConfiguration()
    {
        var item = Package("cli");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Installer().InstallAsync(item, [], cts.Token));
        Assert.Equal("", _userPath);
    }

    [Fact]
    public void PathEditsPreserveUnrelatedEntriesAndAvoidDuplicates()
    {
        Assert.Equal(@"C:\package;C:\one;C:\two", HubNetworkInstaller.AddPath(@"C:\one;c:\PACKAGE\;C:\two", @"C:\package"));
        Assert.Equal(@"C:\one;C:\two", HubNetworkInstaller.RemovePath(@"C:\one;C:\package;C:\two", @"C:\package"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_home)) Directory.Delete(_home, true);
    }
}
