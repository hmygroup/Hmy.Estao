using Hmy.Estao.Core.Harnesses.Installer;
using Hmy.Estao.Desktop.HarnessHub;

namespace Hmy.Estao.Desktop.Tests;

public sealed class HubSkillInventoryTests
{
    [Fact]
    public void LocalSkillsExposeFrontmatterTranslationsAndKeepTheStandardFallback()
    {
        using var fixture = new Fixture();
        fixture.Skill("translated", "---\nname: translated\ndescription: Standard fallback\ndescriptions:\n  es: Descripción traducida\n  fr: Description traduite\n---\n");
        fixture.Skill("legacy", "---\nname: legacy\ndescription: Legacy description\n---\n");

        var skills = Scan(fixture);
        var translated = Assert.Single(skills, skill => skill.Name == "translated");
        Assert.Null(translated.Issue);
        Assert.Equal("Standard fallback", translated.Description);
        Assert.Equal("Descripción traducida", translated.Descriptions!["es"]);
        var legacy = Assert.Single(skills, skill => skill.Name == "legacy");
        Assert.Equal("Legacy description", legacy.Description);
        Assert.Empty(legacy.Descriptions!);
    }

    [Fact]
    public void InvalidTranslationMetadataIsReportedWithoutHidingOtherSkills()
    {
        using var fixture = new Fixture();
        fixture.Skill("broken", "---\nname: broken\ndescription: Fallback\ndescriptions: [Not a mapping]\n---\n");
        fixture.Skill("healthy", "---\nname: healthy\ndescription: Healthy\n---\n");
        var skills = Scan(fixture);
        Assert.NotNull(Assert.Single(skills, skill => skill.Name == "broken").Issue);
        Assert.Null(Assert.Single(skills, skill => skill.Name == "healthy").Issue);
    }

    [Fact]
    public void MalformedYamlIsKeptAsAnIssueOccurrenceAndDoesNotStopOtherSkills()
    {
        using var fixture = new Fixture();
        fixture.Skill("broken", "---\nname: [unterminated\n---\n");
        fixture.Skill("healthy", "---\nname: healthy\ndescription: Good skill\n---\n");

        var skills = Scan(fixture);

        Assert.Equal(2, skills.Count);
        var broken = skills.Single(skill => skill.Name == "broken");
        Assert.Null(Assert.Single(broken.Occurrences).Hash);
        Assert.NotNull(broken.Issue);
        Assert.NotNull(Assert.Single(skills.Single(skill => skill.Name == "healthy").Occurrences).Hash);
    }

    [Theory]
    [InlineData("---\ndescription: No name\n---\n", "name")]
    [InlineData("---\nname: other\ndescription: Wrong folder\n---\n", "does not match folder")]
    [InlineData("---\nname: bad/name\ndescription: Unsafe\n---\n", "single safe folder name")]
    public void MissingOrInvalidMetadataIdentityIsAnIssueUnderTheFolderIdentity(string content, string issue)
    {
        using var fixture = new Fixture();
        fixture.Skill("folder", content);

        var skill = Assert.Single(Scan(fixture));

        Assert.Equal("folder", skill.Id);
        Assert.Equal("folder", skill.Name);
        Assert.Contains(issue, skill.Issue, StringComparison.OrdinalIgnoreCase);
        Assert.Null(Assert.Single(skill.Occurrences).Hash);
    }

    [Fact]
    public void FindsNestedSystemSkillsAndStopsAtTheSkillFolder()
    {
        using var fixture = new Fixture();
        fixture.Skill(Path.Combine(".system", "tools", "nested"), "---\nname: nested\ndescription: Nested\n---\n");
        var assets = Path.Combine(fixture.Adapter.SkillsRoot, ".system", "tools", "nested", "assets");
        Directory.CreateDirectory(assets);
        File.WriteAllText(Path.Combine(assets, "SKILL.md"),
            "---\nname: assets\ndescription: Must not be visited\n---\n");

        var skill = Assert.Single(Scan(fixture));

        Assert.Equal("nested", skill.Name);
        Assert.Single(skill.Occurrences);
    }

    [Fact]
    public void CodexPrimaryRootIsSharedWhenAnotherDetectedToolObservesIt()
    {
        using var fixture = new Fixture();
        var copilot = new HarnessAdapter("copilot", fixture.Home, _ => null);
        var codex = new HarnessAdapter("codex", fixture.Home, _ => null);
        var shared = Path.Combine(codex.SkillsRoot, "shared");
        Directory.CreateDirectory(shared);
        File.WriteAllText(Path.Combine(shared, "SKILL.md"), "---\nname: shared\ndescription: Shared skill\n---\n");

        var skills = HubSkillInventory.Scan(["codex", "copilot"], id => id == "codex" ? codex : copilot);

        var skill = Assert.Single(skills);
        Assert.Equal(2, skill.Occurrences.Count);
        Assert.All(skill.Occurrences, occurrence => Assert.True(occurrence.Shared));
    }

    [Fact]
    public void CancellationIsPropagated()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => HubSkillInventory.Scan(["claude"], _ => fixture.Adapter,
            cancellationToken: cancellation.Token));
    }

    [Fact]
    public void AdapterAndRootFailuresUseCallbackRatherThanInventingSkills()
    {
        using var fixture = new Fixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Adapter.SkillsRoot)!);
        File.WriteAllText(fixture.Adapter.SkillsRoot, "not a directory");
        var errors = new List<(string ToolId, string Error)>();

        var skills = HubSkillInventory.Scan(["failed-adapter", "claude"], id => id == "failed-adapter"
            ? throw new IOException("adapter failed") : fixture.Adapter,
            (toolId, error) => errors.Add((toolId, error)));

        Assert.Empty(skills);
        Assert.Contains(errors, item => item.ToolId == "failed-adapter" && item.Error.Contains("adapter failed"));
        Assert.Contains(errors, item => item.ToolId == "claude");
    }

    [Fact]
    public void SkillLimitIsReportedWithoutCreatingRootNamedFakeSkill()
    {
        using var fixture = new Fixture();
        for (var i = 0; i <= 2000; i++)
            fixture.Skill($"skill-{i:D4}", $"---\nname: skill-{i:D4}\ndescription: Skill\n---\n");
        var errors = new List<string>();

        var skills = HubSkillInventory.Scan(["claude"], _ => fixture.Adapter, (_, error) => errors.Add(error));

        Assert.Contains(errors, error => error.Contains("2000 skill scan limit"));
        Assert.Equal(2000, skills.Count);
        Assert.DoesNotContain(skills, skill => skill.Name == Path.GetFileName(fixture.Adapter.SkillsRoot));
    }

    private static IReadOnlyList<HubSkill> Scan(Fixture fixture) =>
        HubSkillInventory.Scan(["claude"], _ => fixture.Adapter);

    private sealed class Fixture : IDisposable
    {
        public string Home { get; } = Path.Combine(Path.GetTempPath(), "estao-hub-inventory-test-" + Guid.NewGuid().ToString("N"));
        public HarnessAdapter Adapter { get; }

        public Fixture() => Adapter = new HarnessAdapter("claude", Home, _ => null);

        public void Skill(string folder, string content)
        {
            var directory = Path.Combine(Adapter.SkillsRoot, folder);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "SKILL.md"), content);
        }

        public void Dispose()
        {
            if (Directory.Exists(Home)) Directory.Delete(Home, recursive: true);
        }
    }
}
