using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Harnesses;
using System.IO.Compression;
using System.Text.Json;

namespace Hmy.Estao.Tests;

public sealed class HarnessCatalogRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"estao-catalog-{Guid.NewGuid():N}");

    [Fact]
    public async Task publishes_individual_skill_with_catalog_metadata_and_compatibility()
    {
        var sourceRoot = Path.Combine(_root, "source");
        var skillRoot = Path.Combine(sourceRoot, ".agents", "skills", "review");
        Directory.CreateDirectory(skillRoot);
        await File.WriteAllTextAsync(Path.Combine(skillRoot, "SKILL.md"), "---\nname: review\ndescription: Review changes.\n---");
        await File.WriteAllTextAsync(Path.Combine(skillRoot, "notes.md"), "Use the team checklist.");
        var repository = Repository("company", Path.Combine(_root, "hub"));
        var profile = HarnessCatalog.CreateDefaultProfile("codex", sourceRoot);
        var service = new HarnessCatalogRepository();

        var candidate = Assert.Single(await service.DiscoverCandidatesAsync(profile),
            item => item.Feature == HarnessFeatureIds.Skills);
        var published = await service.PublishLocalAsync(repository, profile, candidate.Key,
            Draft("review", "Review skill"), "Test User");

        Assert.Equal(HarnessFeatureIds.Skills, published.Manifest.Type);
        Assert.Equal("Review changes.", published.Manifest.CapabilityDescription);
        Assert.Equal(HarnessCompatibilityStates.Native, published.Manifest.Compatibility["codex"]);
        Assert.Equal(HarnessCompatibilityStates.Converted, published.Manifest.Compatibility["claude"]);
        Assert.Equal(2, (await HarnessHubService.ReadManifestAsync(published.PackagePath)).Artifacts.Count);
        await RemoveCapabilityDescriptionAsync(published.PackagePath);
        var listed = Assert.Single(await service.ListAsync([repository]));
        Assert.Equal("review", listed.Manifest.Id);
        Assert.Equal("Review changes.", listed.Manifest.CapabilityDescription);
        Assert.Equal("Test User", listed.Manifest.OwnerName);
    }

    [Fact]
    public async Task published_versions_are_immutable_and_next_version_is_suggested()
    {
        var sourceRoot = Path.Combine(_root, "immutable-source");
        var skillRoot = Path.Combine(sourceRoot, ".agents", "skills", "one");
        Directory.CreateDirectory(skillRoot);
        await File.WriteAllTextAsync(Path.Combine(skillRoot, "SKILL.md"), "content");
        var profile = HarnessCatalog.CreateDefaultProfile("codex", sourceRoot);
        var repository = Repository("company", Path.Combine(_root, "immutable-hub"));
        var service = new HarnessCatalogRepository();
        var candidate = Assert.Single(await service.DiscoverCandidatesAsync(profile),
            item => item.Feature == HarnessFeatureIds.Skills);
        await service.PublishLocalAsync(repository, profile, candidate.Key, Draft("one", "One"), "Tests");

        await Assert.ThrowsAsync<IOException>(() => service.PublishLocalAsync(repository, profile, candidate.Key,
            Draft("one", "One"), "Tests"));
        Assert.Equal("1.3.0", HarnessCatalogRepository.SuggestNextVersion(["1.0.0", "1.2.4"]));
        Assert.Equal("1.2.5", HarnessCatalogRepository.SuggestNextVersion(["1.2.4"], "patch"));
        Assert.Equal("2.0.0", HarnessCatalogRepository.SuggestNextVersion(["1.2.4"], "major"));
    }

    [Fact]
    public async Task short_semantic_version_is_normalized_before_creating_repository_path()
    {
        var sourceRoot = Path.Combine(_root, "short-version-source");
        var skillRoot = Path.Combine(sourceRoot, ".agents", "skills", "versioned");
        Directory.CreateDirectory(skillRoot);
        await File.WriteAllTextAsync(Path.Combine(skillRoot, "SKILL.md"), "content");
        var profile = HarnessCatalog.CreateDefaultProfile("codex", sourceRoot);
        var repository = Repository("company", Path.Combine(_root, "short-version-hub"));
        var service = new HarnessCatalogRepository();
        var candidate = Assert.Single(await service.DiscoverCandidatesAsync(profile),
            item => item.Feature == HarnessFeatureIds.Skills);

        var published = await service.PublishLocalAsync(repository, profile, candidate.Key,
            Draft("versioned", "Versioned", "2"), "Tests");

        Assert.Equal("2.0.0", published.Manifest.Version);
        Assert.Contains(Path.Combine("versioned", "2.0.0"), published.PackagePath);
    }

    [Fact]
    public async Task discovers_and_publishes_each_mcp_server_individually()
    {
        var sourceRoot = Path.Combine(_root, "mcp-source");
        Directory.CreateDirectory(Path.Combine(sourceRoot, ".codex"));
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, ".codex", "config.toml"), """
            [mcp_servers.alpha]
            command = "alpha"

            [mcp_servers.beta]
            command = "beta"
            """);
        var profile = HarnessCatalog.CreateDefaultProfile("codex", sourceRoot);
        profile.Features = [HarnessFeatureIds.Mcp];
        var repository = Repository("company", Path.Combine(_root, "mcp-hub"));
        var service = new HarnessCatalogRepository();

        var candidates = await service.DiscoverCandidatesAsync(profile);
        Assert.Equal(["alpha", "beta"], candidates.Select(candidate => candidate.Name).Order().ToArray());
        var beta = candidates.Single(candidate => candidate.Name == "beta");
        var published = await service.PublishLocalAsync(repository, profile, beta.Key,
            Draft("beta-mcp", "Beta MCP"), "Tests");
        var package = await HarnessHubService.ReadManifestAsync(published.PackagePath);
        var artifact = Assert.Single(package.Artifacts);
        Assert.Equal("beta.json", artifact.LogicalPath);
    }

    [Fact]
    public async Task lists_multiple_repositories_and_publishes_collection_references()
    {
        var first = Repository("company", Path.Combine(_root, "multi-company"));
        var second = Repository("labs", Path.Combine(_root, "multi-labs"));
        var sourceRoot = Path.Combine(_root, "multi-source");
        var skillRoot = Path.Combine(sourceRoot, ".agents", "skills", "shared");
        Directory.CreateDirectory(skillRoot);
        await File.WriteAllTextAsync(Path.Combine(skillRoot, "SKILL.md"), "shared");
        var profile = HarnessCatalog.CreateDefaultProfile("codex", sourceRoot);
        var service = new HarnessCatalogRepository();
        var candidate = Assert.Single(await service.DiscoverCandidatesAsync(profile),
            item => item.Feature == HarnessFeatureIds.Skills);
        var artifact = await service.PublishLocalAsync(first, profile, candidate.Key,
            Draft("shared", "Shared"), "Tests");
        var collection = await service.PublishCollectionAsync(second,
            Draft("starter", "Starter collection"), "Tests",
            [new HarnessArtifactReference
            {
                RepositoryId = first.Id, ArtifactId = artifact.Manifest.Id,
                Version = artifact.Manifest.Version, Enabled = true
            }], snapshot: false);

        var listed = await service.ListAsync([first, second]);
        Assert.Equal(2, listed.Count);
        Assert.Equal(HarnessCatalogItemTypes.Collection, collection.Manifest.Type);
        Assert.Equal("shared", Assert.Single(collection.Manifest.References).ArtifactId);
    }

    [Fact]
    public async Task environment_store_detects_drift_and_keeps_project_root_machine_local()
    {
        var configPath = Path.Combine(_root, "config", "config.json");
        var project = Path.Combine(_root, "project");
        Directory.CreateDirectory(project);
        var config = new HarnessEnvironmentConfig
        {
            Id = "codex-project",
            Name = "Codex Project",
            HarnessId = "codex",
            Scope = "project",
            RootPath = project,
            Managed = true
        };
        var document = HarnessEnvironmentStore.FromConfig(config);
        var managedPath = Path.Combine(project, "managed.md");
        await File.WriteAllTextAsync(managedPath, "original");
        document.Artifacts.Add(new HarnessEnvironmentArtifact
        {
            RepositoryId = "company",
            ArtifactId = "instructions",
            Version = "1.0.0",
            ManagedFiles = [new HarnessManagedFileState
            {
                Path = "managed.md", Sha256 = await HashAsync(managedPath)
            }]
        });
        var store = new HarnessEnvironmentStore(configPath);
        await store.SaveAsync(document);
        await File.WriteAllTextAsync(managedPath, "changed");

        var loaded = Assert.IsType<HarnessEnvironmentDocument>(await store.LoadAsync(config));
        var drift = Assert.Single(await new HarnessEnvironmentSyncService().DetectDriftAsync(loaded));
        Assert.Equal("modified", drift.State);
        Assert.Equal("managed.md", drift.Path);
        Assert.True(File.Exists(Path.Combine(project, ".estao", "environment.json")));
    }

    [Fact]
    public async Task multi_artifact_apply_rolls_back_completed_install_when_later_item_fails()
    {
        var sourceRoot = Path.Combine(_root, "rollback-source");
        Directory.CreateDirectory(Path.Combine(sourceRoot, ".codex"));
        await File.WriteAllTextAsync(Path.Combine(sourceRoot, ".codex", "AGENTS.md"), "new instructions");
        var source = HarnessCatalog.CreateDefaultProfile("codex", sourceRoot);
        source.Features = [HarnessFeatureIds.Instructions];
        var repository = Repository("company", Path.Combine(_root, "rollback-hub"));
        var candidates = await new HarnessCatalogRepository().DiscoverCandidatesAsync(source);
        var valid = await new HarnessCatalogRepository().PublishLocalAsync(repository, source,
            Assert.Single(candidates).Key, Draft("instructions", "Instructions"), "Tests");
        var targetRoot = Path.Combine(_root, "rollback-target");
        var targetFile = Path.Combine(targetRoot, ".copilot", "copilot-instructions.md");
        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
        await File.WriteAllTextAsync(targetFile, "original instructions");
        var invalidPath = Path.Combine(_root, "broken.estao");
        await File.WriteAllTextAsync(invalidPath, "broken");
        var invalid = new HarnessCatalogEntry("company", "Company", new HarnessCatalogManifest
        {
            Id = "broken",
            Name = "Broken",
            Type = HarnessFeatureIds.Skills,
            Version = "1.0.0"
        }, invalidPath, new FileInfo(invalidPath).Length);
        var environment = new HarnessEnvironmentDocument
        {
            Id = "copilot-project",
            Name = "Copilot project",
            HarnessId = "copilot",
            Scope = "project",
            RootPath = targetRoot
        };

        await Assert.ThrowsAnyAsync<Exception>(() => new HarnessEnvironmentSyncService().ApplyAtomicAsync(environment,
            [new HarnessSyncPlanItem(valid), new HarnessSyncPlanItem(invalid)]));

        Assert.Equal("original instructions", await File.ReadAllTextAsync(targetFile));
    }

    private static HarnessRepositoryConfig Repository(string id, string path) => new()
    {
        Id = id,
        Name = id,
        Path = path,
        Enabled = true
    };

    private static HarnessCatalogDraft Draft(string id, string name, string version = "1.0.0") => new(
        id, name, "Summary", "Description", version, "engineering", "Initial", [],
        ["personal", "project"]);

    private static async Task<string> HashAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream)).ToLowerInvariant();
    }

    private static async Task RemoveCapabilityDescriptionAsync(string packagePath)
    {
        await using var stream = new FileStream(packagePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Update);
        var entry = archive.GetEntry("catalog.json")!;
        HarnessCatalogManifest manifest;
        await using (var input = entry.Open())
            manifest = (await JsonSerializer.DeserializeAsync<HarnessCatalogManifest>(input))!;
        entry.Delete();
        manifest.CapabilityDescription = string.Empty;
        await using var output = archive.CreateEntry("catalog.json").Open();
        await JsonSerializer.SerializeAsync(output, manifest);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
