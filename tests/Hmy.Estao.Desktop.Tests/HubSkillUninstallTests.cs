using Hmy.Estao.Core.Harnesses.Installer;
using Hmy.Estao.Desktop.HarnessHub;

namespace Hmy.Estao.Desktop.Tests;

public sealed class HubSkillUninstallTests : IDisposable
{
    private readonly string _home = Path.Combine(Path.GetTempPath(), "estao-uninstall-" + Guid.NewGuid().ToString("N"));
    private HarnessAdapter Adapter(string id) => new(id, _home, _ => null);
    private static string WriteSkill(string root, string name)
    {
        var directory = Path.Combine(root, name);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "SKILL.md"), $"---\nname: {name}\ndescription: Test skill\n---\n");
        File.WriteAllText(Path.Combine(directory, "asset.txt"), "Keep this resource");
        return directory;
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    [InlineData("copilot")]
    [InlineData("opencode")]
    public void SystemCachesAreFilteredButPersonalOverridesAreNot(string id)
    {
        var root = Adapter(id).SkillsRoot;
        var system = WriteSkill(Path.Combine(root, ".system"), "bundled");
        var marked = WriteSkill(Path.Combine(root, "cache"), "cached");
        File.WriteAllText(Path.Combine(root, "cache", ".codex-system-skills.marker"), "fingerprint");
        WriteSkill(root, "skill-creator"); // A name is not proof of system ownership.
        var skills = HubSkillInventory.Scan([id], Adapter);
        Assert.Equal("skill-creator", Assert.Single(skills).Name);
        Assert.Empty(HubSkillInventory.Scan([id], Adapter, skillDirectories: new HashSet<string> { system, marked }));
        Assert.Throws<InvalidDataException>(() => HubSkillSafety.ValidateUserSkill(Adapter(id), Path.Combine(system, "SKILL.md")));
        Assert.Throws<InvalidDataException>(() => HubSkillSafety.ValidateUserSkill(Adapter(id), Path.Combine(marked, "SKILL.md")));
    }

    [Fact]
    public void CodexProfileSystemCacheIsNotExposedAsInstalled()
    {
        var root = Path.Combine(Adapter("codex").Profile, "skills");
        WriteSkill(Path.Combine(root, ".system"), "builtin");
        Assert.Empty(HubSkillInventory.Scan(["codex"], Adapter));
    }

    [Fact]
    public void UninstallMovesWholeFoldersAndDeduplicatesSharedObservations()
    {
        var shared = WriteSkill(Adapter("codex").SkillsRoot, "sample");
        var claude = WriteSkill(Adapter("claude").SkillsRoot, "sample");
        var unrelated = WriteSkill(Adapter("claude").SkillsRoot, "other");
        var skill = Assert.Single(HubSkillInventory.Scan(HarnessAdapter.Ids, Adapter), item => item.Name == "sample");
        Assert.True(skill.Occurrences.Count > 2);
        var data = Path.Combine(_home, "data");
        HubSkillUninstaller.Uninstall(skill, Adapter, data, default);
        Assert.False(Directory.Exists(shared));
        Assert.False(Directory.Exists(claude));
        Assert.True(Directory.Exists(unrelated));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(data, "uninstalled-skills"), "asset.txt", SearchOption.AllDirectories).Length);
        Assert.DoesNotContain(HubSkillInventory.Scan(HarnessAdapter.Ids, Adapter), item => item.Name == "sample");
    }

    [Fact]
    public void ChangedCopyPreventsAnyRemoval()
    {
        var first = WriteSkill(Adapter("codex").SkillsRoot, "sample");
        var second = WriteSkill(Adapter("claude").SkillsRoot, "sample");
        var skill = Assert.Single(HubSkillInventory.Scan(HarnessAdapter.Ids, Adapter));
        File.AppendAllText(Path.Combine(second, "SKILL.md"), "Local change");
        Assert.Throws<IOException>(() => HubSkillUninstaller.Uninstall(skill, Adapter, Path.Combine(_home, "data"), default));
        Assert.True(Directory.Exists(first));
        Assert.True(Directory.Exists(second));
    }

    [Fact]
    public void NewlyMarkedSystemCacheCannotBeUninstalledFromStaleInventory()
    {
        var root = Adapter("codex").SkillsRoot;
        var directory = WriteSkill(root, "sample");
        var skill = Assert.Single(HubSkillInventory.Scan(["codex"], Adapter));
        File.WriteAllText(Path.Combine(directory, ".codex-system-skills.marker"), "fingerprint");
        Assert.Throws<InvalidDataException>(() => HubSkillUninstaller.Uninstall(skill, Adapter, Path.Combine(_home, "data"), default));
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public void ArbitraryPathsAndRootLevelSkillsCannotAuthorizeRemoval()
    {
        var outside = WriteSkill(_home, "outside");
        Assert.Throws<InvalidDataException>(() => HubSkillSafety.ValidateUserSkill(Adapter("codex"), Path.Combine(outside, "SKILL.md")));
        Assert.Throws<InvalidDataException>(() => HubSkillSafety.ValidateUserSkill(Adapter("codex"), Path.Combine(Adapter("codex").SkillsRoot, "SKILL.md")));
    }

    public void Dispose() { if (Directory.Exists(_home)) Directory.Delete(_home, recursive: true); }
}
