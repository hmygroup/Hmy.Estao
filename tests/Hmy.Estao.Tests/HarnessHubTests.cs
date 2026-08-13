using System.IO.Compression;
using System.Text.Json;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Harnesses;

namespace Hmy.Estao.Tests;

public sealed class HarnessHubTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"estao-harness-{Guid.NewGuid():N}");

    [Fact]
    public async Task codex_package_converts_skills_instructions_and_mcp_to_copilot()
    {
        var sourceRoot = Path.Combine(_directory, "source");
        var hub = Path.Combine(_directory, "hub");
        var skillDirectory = Path.Combine(sourceRoot, ".agents", "skills", "review");
        Directory.CreateDirectory(skillDirectory);
        await File.WriteAllTextAsync(Path.Combine(skillDirectory, "SKILL.md"), "---\nname: review\ndescription: Review changes.\n---\nReview carefully.");
        Directory.CreateDirectory(Path.Combine(sourceRoot, ".codex"));
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, ".codex", "AGENTS.md"), "Always run tests.");
        Directory.CreateDirectory(Path.Combine(sourceRoot, ".codex", "agents"));
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, ".codex", "agents", "reviewer.toml"), """"
            name = "reviewer"
            description = "Reviews changes"
            sandbox_mode = "read-only"
            developer_instructions = """
            Review changes for correctness and missing tests.
            """
            """");
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, ".codex", "config.toml"), """
            [mcp_servers.github]
            command = "npx"
            args = ["-y", "@example/server"]

            [mcp_servers.github.env]
            GITHUB_TOKEN = "super-secret-value"
            """);
        var source = HarnessCatalog.CreateDefaultProfile("codex", sourceRoot);
        source.Features.Add(HarnessFeatureIds.Settings);

        var published = await new HarnessHubService().PublishAsync(hub, source,
            new HarnessPackageDraft("team-kit", "Team kit", "Shared setup", "1.2.0", "Estao Tests"));

        var listed = Assert.Single(await new HarnessHubService().ListAsync(hub));
        Assert.Equal(published.Path, listed.Path);
        Assert.Contains(listed.Manifest.Artifacts, item => item.Feature == HarnessFeatureIds.Skills);
        Assert.Contains(listed.Manifest.Artifacts, item => item.Feature == HarnessFeatureIds.Mcp && item.Redacted);

        var targetRoot = Path.Combine(_directory, "target");
        var target = HarnessCatalog.CreateDefaultProfile("copilot", targetRoot);
        var result = await new HarnessPackageInstaller().InstallAsync(published.Path, target);

        Assert.True(File.Exists(Path.Combine(targetRoot, ".copilot", "skills", "review", "SKILL.md")));
        var convertedAgent = await File.ReadAllTextAsync(Path.Combine(targetRoot, ".copilot", "agents", "reviewer.md"));
        Assert.Contains("name: \"reviewer\"", convertedAgent);
        Assert.Contains("Review changes for correctness", convertedAgent);
        Assert.DoesNotContain("sandbox_mode", convertedAgent);
        Assert.Equal("Always run tests.", (await File.ReadAllTextAsync(
            Path.Combine(targetRoot, ".copilot", "copilot-instructions.md"))).Trim());
        var mcpText = await File.ReadAllTextAsync(Path.Combine(targetRoot, ".copilot", "mcp-config.json"));
        Assert.Contains("github", mcpText);
        Assert.Contains("${ENV:GITHUB_TOKEN}", mcpText);
        Assert.DoesNotContain("super-secret-value", mcpText);
        Assert.Contains(result.Warnings, warning => warning.Contains("MCP servers were translated", StringComparison.Ordinal));
        Assert.Contains(result.SkippedArtifacts, item => item.StartsWith("settings:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task disabled_target_feature_is_not_installed()
    {
        var sourceRoot = Path.Combine(_directory, "source-disabled");
        var skillDirectory = Path.Combine(sourceRoot, ".agents", "skills", "review");
        Directory.CreateDirectory(skillDirectory);
        await File.WriteAllTextAsync(Path.Combine(skillDirectory, "SKILL.md"), "---\nname: review\ndescription: Review.\n---");
        var source = HarnessCatalog.CreateDefaultProfile("codex", sourceRoot);
        source.Features = [HarnessFeatureIds.Skills];
        var package = await new HarnessHubService().PublishAsync(Path.Combine(_directory, "hub-disabled"), source,
            new HarnessPackageDraft("skills", "Skills", "", "1.0.0", "Tests"));
        var target = HarnessCatalog.CreateDefaultProfile("copilot", Path.Combine(_directory, "target-disabled"));
        target.Features.Remove(HarnessFeatureIds.Skills);

        var result = await new HarnessPackageInstaller().InstallAsync(package.Path, target);

        Assert.Empty(result.InstalledFiles);
        Assert.Contains(result.SkippedArtifacts, item => item.Contains("(disabled)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task codex_publish_includes_legacy_user_skills_rules_and_plugin_registrations()
    {
        var sourceRoot = Path.Combine(_directory, "source-complete-codex");
        var customSkill = Path.Combine(sourceRoot, ".codex", "skills", "team-skill");
        var bundledSkill = Path.Combine(sourceRoot, ".codex", "skills", ".system", "bundled");
        Directory.CreateDirectory(customSkill);
        Directory.CreateDirectory(bundledSkill);
        await File.WriteAllTextAsync(Path.Combine(customSkill, "SKILL.md"), "---\nname: team-skill\ndescription: Team workflow.\n---");
        await File.WriteAllTextAsync(Path.Combine(bundledSkill, "SKILL.md"), "---\nname: bundled\ndescription: Do not package.\n---");
        var rules = Path.Combine(sourceRoot, ".codex", "rules");
        Directory.CreateDirectory(rules);
        await File.WriteAllTextAsync(Path.Combine(rules, "default.rules"), "prefix_rule(pattern=[\"dotnet\"], decision=\"allow\")");
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, ".codex", "config.toml"), """
            model = "gpt-test"

            [marketplaces.team]
            source = "https://example.invalid/marketplace"

            [plugins."team-tools@team"]
            enabled = true

            [mcp_servers.example]
            command = "example"
            """);
        var source = HarnessCatalog.CreateDefaultProfile("codex", sourceRoot);

        var package = await new HarnessHubService().PublishAsync(Path.Combine(_directory, "hub-complete-codex"), source,
            new HarnessPackageDraft("complete", "Complete Codex", "", "1.0.0", "Tests"));

        Assert.Contains(package.Manifest.Artifacts, item => item.Feature == HarnessFeatureIds.Skills && item.LogicalPath == "team-skill/SKILL.md");
        Assert.DoesNotContain(package.Manifest.Artifacts, item => item.LogicalPath.Contains("bundled", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(package.Manifest.Artifacts, item => item.Feature == HarnessFeatureIds.Rules && item.LogicalPath == "default.rules");
        Assert.Contains(package.Manifest.Artifacts, item => item.Feature == HarnessFeatureIds.Plugins && item.LogicalPath == "plugins.toml");

        var targetRoot = Path.Combine(_directory, "target-complete-codex");
        Directory.CreateDirectory(Path.Combine(targetRoot, ".codex"));
        await File.WriteAllTextAsync(Path.Combine(targetRoot, ".codex", "config.toml"), "model = \"keep-me\"\n");
        var target = HarnessCatalog.CreateDefaultProfile("codex", targetRoot);
        target.Features.Remove(HarnessFeatureIds.Mcp);
        await new HarnessPackageInstaller().InstallAsync(package.Path, target);

        Assert.True(File.Exists(Path.Combine(targetRoot, ".agents", "skills", "team-skill", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(targetRoot, ".codex", "rules", "default.rules")));
        var installedConfig = await File.ReadAllTextAsync(Path.Combine(targetRoot, ".codex", "config.toml"));
        Assert.Contains("model = \"keep-me\"", installedConfig);
        Assert.Contains("[marketplaces.team]", installedConfig);
        Assert.Contains("[plugins.\"team-tools@team\"]", installedConfig);
        Assert.DoesNotContain("[mcp_servers.example]", installedConfig);
    }

    [Fact]
    public async Task install_backs_up_existing_files_before_overwrite()
    {
        var sourceRoot = Path.Combine(_directory, "source-backup");
        Directory.CreateDirectory(Path.Combine(sourceRoot, ".codex"));
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, ".codex", "AGENTS.md"), "New instructions");
        var source = HarnessCatalog.CreateDefaultProfile("codex", sourceRoot);
        source.Features = [HarnessFeatureIds.Instructions];
        var package = await new HarnessHubService().PublishAsync(Path.Combine(_directory, "hub-backup"), source,
            new HarnessPackageDraft("instructions", "Instructions", "", "1.0.0", "Tests"));
        var targetRoot = Path.Combine(_directory, "target-backup");
        var existing = Path.Combine(targetRoot, ".copilot", "copilot-instructions.md");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        await File.WriteAllTextAsync(existing, "Old instructions");
        var target = HarnessCatalog.CreateDefaultProfile("copilot", targetRoot);

        var result = await new HarnessPackageInstaller().InstallAsync(package.Path, target);

        Assert.NotNull(result.BackupDirectory);
        Assert.Equal("New instructions", (await File.ReadAllTextAsync(existing)).Trim());
        Assert.Contains(Directory.EnumerateFiles(result.BackupDirectory!, "*", SearchOption.AllDirectories),
            path => File.ReadAllText(path) == "Old instructions");
    }

    [Fact]
    public async Task restore_point_restores_overwritten_files_and_removes_files_created_by_install()
    {
        var sourceRoot = Path.Combine(_directory, "source-restore");
        var changedSkill = Path.Combine(sourceRoot, ".agents", "skills", "changed");
        var newSkill = Path.Combine(sourceRoot, ".agents", "skills", "new-skill");
        Directory.CreateDirectory(changedSkill);
        Directory.CreateDirectory(newSkill);
        await File.WriteAllTextAsync(Path.Combine(changedSkill, "SKILL.md"), "new content");
        await File.WriteAllTextAsync(Path.Combine(newSkill, "SKILL.md"), "created by package");
        var source = HarnessCatalog.CreateDefaultProfile("codex", sourceRoot);
        source.Features = [HarnessFeatureIds.Skills];
        var package = await new HarnessHubService().PublishAsync(Path.Combine(_directory, "hub-restore"), source,
            new HarnessPackageDraft("restore", "Restore test", "", "1.0.0", "Tests"));

        var targetRoot = Path.Combine(_directory, "target-restore");
        var existing = Path.Combine(targetRoot, ".copilot", "skills", "changed", "SKILL.md");
        var created = Path.Combine(targetRoot, ".copilot", "skills", "new-skill", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        await File.WriteAllTextAsync(existing, "original content");
        var target = HarnessCatalog.CreateDefaultProfile("copilot", targetRoot);

        var install = await new HarnessPackageInstaller().InstallAsync(package.Path, target);
        var point = Assert.Single(await new HarnessRestoreService().ListAsync(target));
        var restored = await new HarnessRestoreService().RestoreAsync(point, target);

        Assert.NotNull(install.BackupDirectory);
        Assert.Equal("original content", await File.ReadAllTextAsync(existing));
        Assert.False(File.Exists(created));
        Assert.Contains(existing, restored.RestoredFiles);
        Assert.Contains(created, restored.RemovedFiles);
    }

    [Theory]
    [InlineData("../escape.md")]
    [InlineData("C:/escape.md")]
    [InlineData("payload/../../escape.md")]
    public void unsafe_package_paths_are_rejected(string value)
    {
        Assert.Throws<InvalidDataException>(() => HarnessHubService.ValidateRelativePath(value));
    }

    [Fact]
    public async Task corrupt_package_is_ignored_by_hub_listing()
    {
        var packages = Path.Combine(_directory, "hub-corrupt", "packages");
        Directory.CreateDirectory(packages);
        await File.WriteAllTextAsync(Path.Combine(packages, "broken.estao"), "not a zip");

        var result = await new HarnessHubService().ListAsync(Path.Combine(_directory, "hub-corrupt"));

        Assert.Empty(result);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
