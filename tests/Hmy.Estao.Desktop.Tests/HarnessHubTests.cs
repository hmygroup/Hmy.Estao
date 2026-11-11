using System.Text;
using Hmy.Estao.Core.Harnesses.Installer;
using Hmy.Estao.Desktop.HarnessHub;

namespace Hmy.Estao.Desktop.Tests;

public sealed class HarnessHubTests
{
    [Fact]
    public void EveryToolHasAnOfficialHttpsInstallationLink()
    {
        foreach (var id in HarnessAdapter.Ids)
        {
            var uri = new Uri(HubTools.Website(id));
            Assert.Equal("https", uri.Scheme);
            Assert.False(string.IsNullOrWhiteSpace(uri.Host));
        }
        Assert.Throws<ArgumentException>(() => HubTools.Website("arbitrary-path"));
    }

    [Fact]
    public void DetectionRequiresAnExecutableNotJustAnExistingConfigurationFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "estao-hub-detect-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "config.json"), "{}");
            Assert.False(HubTools.DetectIn("claude", [dir]));
            File.WriteAllText(Path.Combine(dir, "claude.cmd"), "test fixture; never execute");
            Assert.True(HubTools.DetectIn("claude", [$"\"{dir}\""]));
            Assert.False(HubTools.DetectIn("copilot", [dir]));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LocalInventoryUnionsSharedRootsAndKeepsDifferentCopies()
    {
        var home = Path.Combine(Path.GetTempPath(), "estao-hub-inventory-" + Guid.NewGuid().ToString("N"));
        try
        {
            var codex = new HarnessAdapter("codex", home, _ => null);
            var claude = new HarnessAdapter("claude", home, _ => null);
            Directory.CreateDirectory(Path.Combine(codex.SkillsRoot, "same"));
            Directory.CreateDirectory(Path.Combine(claude.SkillsRoot, "same"));
            File.WriteAllText(Path.Combine(codex.SkillsRoot, "same", "SKILL.md"), "---\nname: same\ndescription: Codex copy\n---\n");
            File.WriteAllText(Path.Combine(claude.SkillsRoot, "same", "SKILL.md"), "---\nname: same\ndescription: Claude copy\n---\n");
            var skills = HubSkillInventory.Scan(["codex", "claude"], id => id == "codex" ? codex : claude);
            var skill = Assert.Single(skills);
            Assert.Equal("same", skill.Id);
            Assert.Equal(2, skill.Occurrences.Count);
            Assert.All(skill.Occurrences, occurrence => Assert.NotNull(occurrence.Hash));
            Assert.Contains("different", skill.Issue, StringComparison.OrdinalIgnoreCase);
        }
        finally { if (Directory.Exists(home)) Directory.Delete(home, recursive: true); }
    }

    [Fact]
    public void SelectionPreservesWholeSkillFoldersAndRequiresDependencies()
    {
        var bytes = Encoding.UTF8.GetBytes("resource");
        var snapshot = new PreparedSnapshot("commit", "tree", "1",
            [new("base", "base/SKILL.md", bytes), new("base", "base/references/guide.md", bytes),
                new("dependent", "dependent/SKILL.md", bytes)], [],
            new Dictionary<string, IReadOnlyList<string>> { ["base"] = [], ["dependent"] = ["base"] });
        Assert.Throws<ArgumentException>(() => HubReviewBuilder.Select(snapshot, ["missing"]));
        Assert.Throws<ArgumentException>(() => HubReviewBuilder.Select(snapshot, ["dependent"]));
        var chosen = HubReviewBuilder.Select(snapshot, ["base"]);
        Assert.Equal(2, chosen.Files.Count);
        Assert.All(chosen.Files, file => Assert.Equal("base", file.ArtifactId));
        Assert.Equal(3, HubReviewBuilder.Select(snapshot, ["base", "dependent"]).Files.Count);
        Assert.Empty(HubReviewBuilder.Select(snapshot, []).Files);
    }

    [Fact]
    public void ReviewShowsMetadataAndSkillLevelStatusNotOnlyFileLogs()
    {
        var home = Path.Combine(Path.GetTempPath(), "estao-hub-review-" + Guid.NewGuid().ToString("N"));
        var adapter = new HarnessAdapter("claude", home, _ => null);
        var bytes = Encoding.UTF8.GetBytes("test");
        var files = new[] {
            new PlannedFile("skill", Path.Combine(adapter.SkillsRoot, "skill", "SKILL.md"), bytes, null, "hash", InstallAction.Create, "New file"),
            new PlannedFile("skill", Path.Combine(adapter.SkillsRoot, "skill", "asset.txt"), bytes, "before", "after", InstallAction.Conflict, "Keep local edits")
        };
        var snapshot = new PreparedSnapshot("commit", "tree", "1", [],
            [new("prompts", "external-runtime", "ChatGPT prompts, not native skills", 3)],
            new Dictionary<string, IReadOnlyList<string>> { ["skill"] = [] },
            [new("skill", "skill", ".agents/skills/skill", "A helpful skill", ["Needs a browser"])]);
        var plan = new InstallPlan("claude", adapter.Profile, "commit", "1", files, snapshot.Coverage);
        var review = HubReviewBuilder.Build(adapter, snapshot, plan);
        var artifact = Assert.Single(review.Artifacts);
        Assert.Equal("conflict", artifact.Status);
        Assert.Equal("A helpful skill", artifact.Description);
        Assert.Equal("Needs a browser", Assert.Single(artifact.Requirements));
        Assert.Equal(2, artifact.FileCount);
        Assert.Equal("prompts", Assert.Single(review.Exclusions).Source);
        Assert.Equal(adapter.SkillsRoot, review.Destination);
    }

    [Theory]
    [InlineData(InstallAction.Create, "new")]
    [InlineData(InstallAction.Update, "update")]
    [InlineData(InstallAction.Merge, "update")]
    [InlineData(InstallAction.AlreadyPresent, "present")]
    [InlineData(InstallAction.Conflict, "conflict")]
    public void ReviewMapsEveryInstallerAction(InstallAction action, string expected)
    {
        var adapter = new HarnessAdapter("claude", Path.Combine(Path.GetTempPath(), "estao-hub-action"), _ => null);
        var snapshot = new PreparedSnapshot("commit", "tree", "1", [], []);
        var plan = new InstallPlan("claude", adapter.Profile, "commit", "1",
            [new("skill", Path.Combine(adapter.SkillsRoot, "skill", "SKILL.md"), [], null, "hash", action, "reason")], []);
        Assert.Equal(expected, Assert.Single(HubReviewBuilder.Build(adapter, snapshot, plan).Artifacts).Status);
    }

    [Fact]
    public async Task SharedGlobalSkillsAreNotDuplicatedDuringMultiToolInstallation()
    {
        var dir = Path.Combine(Path.GetTempPath(), "estao-hub-shared-" + Guid.NewGuid().ToString("N"));
        try
        {
            var codex = new HarnessAdapter("codex", dir, _ => null);
            var bytes = Encoding.UTF8.GetBytes("same complete skill");
            var first = new PreparedSnapshot("commit", "tree", "1", [new("shared", Path.Combine(codex.SkillsRoot, "shared", "SKILL.md"), bytes)], []);
            var service = new HarnessInstallationService(Path.Combine(dir, "data"));
            var firstPlan = await service.PlanAsync(codex, first);
            Assert.Equal(1, (await service.ApplyAsync(codex, firstPlan, true)).Created);
            var file = Assert.Single(first.Files).RelativePath;
            var timestamp = File.GetLastWriteTimeUtc(file);
            foreach (var id in new[] { "copilot", "opencode" })
            {
                var adapter = new HarnessAdapter(id, dir, _ => null);
                var snapshot = first with { Files = [new("shared", Path.Combine(adapter.SkillsRoot, "shared", "SKILL.md"), bytes)] };
                var plan = await service.PlanAsync(adapter, snapshot);
                Assert.Equal(InstallAction.AlreadyPresent, Assert.Single(plan.Files).Action);
                var result = await service.ApplyAsync(adapter, plan, true);
                Assert.Equal(0, result.Created);
                Assert.Equal(1, result.Unchanged);
                Assert.Equal(file, Assert.Single(snapshot.Files).RelativePath);
                Assert.Equal(bytes, await File.ReadAllBytesAsync(file));
                Assert.Equal(timestamp, File.GetLastWriteTimeUtc(file));
                Assert.False(Directory.Exists(Path.Combine(adapter.Profile, "skills")));
            }
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }
}
