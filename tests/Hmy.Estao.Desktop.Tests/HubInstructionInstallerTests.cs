using System.Text;
using Hmy.Estao.Core.Harnesses.Installer;
using Hmy.Estao.Desktop.HarnessHub;

namespace Hmy.Estao.Desktop.Tests;

public sealed class HubInstructionInstallerTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "estao-instructions-" + Guid.NewGuid().ToString("N"));
    private HarnessAdapter Adapter(string id) => new(id, Path.Combine(_directory, "home"), _ => null);
    private static byte[] Content(string body = "Check out files before editing.") => Encoding.UTF8.GetBytes(
        "---\nname: tfvc-workspaces\ndescription: Rules for TFVC workspaces.\ndescriptions:\n  es: Reglas TFVC.\n---\n# TFVC\n\n" + body);

    [Theory]
    [InlineData("codex", ".codex", "AGENTS.md")]
    [InlineData("claude", ".claude", "CLAUDE.md")]
    [InlineData("copilot", ".copilot", "copilot-instructions.md")]
    [InlineData("opencode", ".config/opencode", "AGENTS.md")]
    public async Task InstallsInNativePersonalInstructionFilesAndStripsMetadata(string id, string profile, string filename)
    {
        var adapter = Adapter(id);
        var expected = Path.Combine(_directory, "home", profile.Replace('/', Path.DirectorySeparatorChar), filename);
        Assert.Equal(expected, HubInstructionInstaller.Destination(adapter));
        await HubInstructionInstaller.InstallAsync(adapter, "global-instructions", Content(), Path.Combine(_directory, "data"), default);
        var installed = await File.ReadAllTextAsync(expected);
        Assert.Contains("# TFVC", installed);
        Assert.Contains("Check out files before editing.", installed);
        Assert.DoesNotContain("description:", installed);
        Assert.DoesNotContain("name:", installed);
        Assert.False(Directory.Exists(adapter.SkillsRoot));
    }

    [Fact]
    public async Task AppendsWithoutReplacingExistingInstructionsAndKeepsExactBackup()
    {
        var adapter = Adapter("claude");
        var destination = HubInstructionInstaller.Destination(adapter);
        Directory.CreateDirectory(adapter.Profile);
        var original = Encoding.UTF8.GetBytes("# Personal rules\r\nKeep these rules.\r\n");
        await File.WriteAllBytesAsync(destination, original);
        var data = Path.Combine(_directory, "data");
        await HubInstructionInstaller.InstallAsync(adapter, "global-instructions", Content(), data, default);
        Assert.StartsWith(Encoding.UTF8.GetString(original), await File.ReadAllTextAsync(destination));
        var backup = Assert.Single(Directory.GetFiles(Path.Combine(data, "instruction-backups"), "*.md", SearchOption.AllDirectories));
        Assert.Equal(original, await File.ReadAllBytesAsync(backup));
        var once = await File.ReadAllBytesAsync(destination);
        await HubInstructionInstaller.InstallAsync(adapter, "global-instructions", Content(), data, default);
        Assert.Equal(once, await File.ReadAllBytesAsync(destination));
        Assert.Single(Directory.GetFiles(Path.Combine(data, "instruction-backups"), "*.md", SearchOption.AllDirectories));
        await Assert.ThrowsAsync<IOException>(() => HubInstructionInstaller.InstallAsync(adapter, "global-instructions", Content("Changed guidance"), data, default));
        Assert.Equal(once, await File.ReadAllBytesAsync(destination));
    }

    [Fact]
    public async Task CodexUsesActiveOverrideAndLeavesInactiveBaseAlone()
    {
        var adapter = Adapter("codex");
        Directory.CreateDirectory(adapter.Profile);
        var path = Path.Combine(adapter.Profile, "AGENTS.override.md");
        await File.WriteAllTextAsync(path, "Active override");
        await File.WriteAllTextAsync(Path.Combine(adapter.Profile, "AGENTS.md"), "Inactive base");
        await HubInstructionInstaller.InstallAsync(adapter, "global-instructions", Content(), Path.Combine(_directory, "data"), default);
        Assert.StartsWith("Active override", await File.ReadAllTextAsync(path));
        Assert.Contains("# TFVC", await File.ReadAllTextAsync(path));
        Assert.Equal("Inactive base", await File.ReadAllTextAsync(Path.Combine(adapter.Profile, "AGENTS.md")));
    }

    [Fact]
    public async Task RejectsEmptyBodyAndInvalidTextWithoutWriting()
    {
        var adapter = Adapter("claude");
        var data = Path.Combine(_directory, "data");
        await Assert.ThrowsAsync<InvalidDataException>(() => HubInstructionInstaller.InstallAsync(adapter, "global-instructions", Encoding.UTF8.GetBytes("---\nname: empty\n---\n"), data, default));
        await Assert.ThrowsAsync<DecoderFallbackException>(() => HubInstructionInstaller.InstallAsync(adapter, "global-instructions", [0xff], data, default));
        Assert.False(File.Exists(HubInstructionInstaller.Destination(adapter)));
    }

    [Fact]
    public void DescriptionAndTranslationsComeFromOptionalFrontmatter()
    {
        var document = InstructionDocument.Read(Content());
        Assert.Equal("Rules for TFVC workspaces.", document.Metadata!.Description);
        Assert.Equal("Reglas TFVC.", document.Metadata.Descriptions["es"]);
        Assert.StartsWith("# TFVC", document.Body);
        Assert.Null(InstructionDocument.Read(Encoding.UTF8.GetBytes("Plain markdown")).Metadata);
        Assert.Equal("Plain markdown", InstructionDocument.Read(Encoding.UTF8.GetBytes("Plain markdown")).Body);
        Assert.NotNull(InstructionDocument.Read(Encoding.UTF8.GetPreamble().Concat(Content()).ToArray()).Metadata);
    }

    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
}
