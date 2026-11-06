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
            var copilot = new HarnessAdapter("copilot", dir, _ => null);
            var bytes = Encoding.UTF8.GetBytes("same complete skill");
            var first = new PreparedSnapshot("commit", "tree", "1", [new("shared", Path.Combine(codex.SkillsRoot, "shared", "SKILL.md"), bytes)], []);
            var second = first with { Files = [new("shared", Path.Combine(copilot.SkillsRoot, "shared", "SKILL.md"), bytes)] };
            var service = new HarnessInstallationService(Path.Combine(dir, "data"));
            var firstPlan = await service.PlanAsync(codex, first);
            Assert.Equal(1, (await service.ApplyAsync(codex, firstPlan, true)).Created);
            var secondPlan = await service.PlanAsync(copilot, second);
            Assert.Equal(InstallAction.AlreadyPresent, Assert.Single(secondPlan.Files).Action);
            Assert.Equal(0, (await service.ApplyAsync(copilot, secondPlan, true)).Created);
            Assert.False(File.Exists(Path.Combine(copilot.SkillsRoot, "shared", "SKILL.md")));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }
}
