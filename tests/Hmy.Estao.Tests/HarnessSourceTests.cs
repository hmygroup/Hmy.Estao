using System.Net;
using System.Text;
using System.Text.Json;
using Hmy.Estao.Core.Harnesses.Installer;

namespace Hmy.Estao.Tests;

public sealed class HarnessSourceTests
{
    private static readonly string Commit = new('a', 40);
    private static readonly string Tree = new('b', 40);
    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    [Fact]
    public void bootstrap_covers_current_skills_and_explicit_exclusions_not_historical_prompts()
    {
        var manifest = ManifestContract.Bootstrap();
        Assert.Equal(22, manifest.Artifacts.Count);
        Assert.All(manifest.Artifacts, a => Assert.Equal("skill", a.Category));
        Assert.Single(manifest.Artifacts.Single(a => a.Id == "hatch-pet").Harnesses);
        Assert.Contains(manifest.Exclusions, e => e.Source == "prompts" && e.Classification == "external-runtime");
        foreach (var id in HarnessAdapter.Ids)
            Assert.Equal(id == "codex" ? 22 : 21, manifest.Artifacts.Count(a => a.Harnesses.Contains(id)));
    }

    [Theory]
    [InlineData("120000")]
    [InlineData("160000")]
    public void manifest_rejects_links_and_submodules(string mode)
    {
        var manifest = Minimal();
        Assert.Throws<InvalidDataException>(() => ManifestContract.Validate(manifest, Commit, Tree, [new("skills/test/SKILL.md", Commit, 1, mode)]));
    }

    [Fact]
    public void contract_rejects_new_files_mismatched_bootstrap_duplicates_and_missing_dependencies()
    {
        var entries = new[] { new SourceEntry("skills/test/SKILL.md", Commit, 1, "100644") };
        ManifestContract.Validate(Minimal(), Commit, Tree, entries);
        Assert.Throws<InvalidDataException>(() => ManifestContract.Validate(Minimal(), Commit, Tree, [.. entries, new("new.md", Commit, 1, "100644")]));
        var pinned = Minimal(); pinned.Commit = new string('c', 40);
        Assert.Throws<InvalidDataException>(() => ManifestContract.Validate(pinned, Commit, Tree, entries));
        var dependencies = Minimal(); dependencies.Artifacts[0].Dependencies = ["missing"];
        Assert.Throws<InvalidDataException>(() => ManifestContract.Validate(dependencies, Commit, Tree, entries));
        Assert.Throws<InvalidDataException>(() => ManifestContract.Validate(Minimal(), Commit, Tree, [.. entries, new("SKILLS/test/skill.md", Commit, 1, "100644")]));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task authorization_errors_are_sanitized_and_never_include_response_secrets(HttpStatusCode status)
    {
        using var client = new HttpClient(new Handler(_ =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent("private-response-secret") };
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            return response;
        }));
        var error = await Assert.ThrowsAsync<GitHubAccessException>(() => new GitHubRepositorySource(client).VerifyAccessAsync("fake-token"));
        Assert.Equal(status, error.Status);
        Assert.DoesNotContain("private-response-secret", error.Message);
        Assert.DoesNotContain("fake-token", error.Message);
    }

    [Fact]
    public async Task retries_transient_http_errors_but_not_bad_authorization()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(_ =>
        {
            calls++;
            return new HttpResponseMessage(calls == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
                { Content = new StringContent("[]") };
        }));
        await new GitHubRepositorySource(client).VerifyAccessAsync("fake-token");
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task retrieves_a_single_verified_snapshot_and_rejects_corruption_or_truncation()
    {
        var content = Bytes("---\nname: test\ndescription: Test skill\n---\nBody remains intact.\n");
        var hash = InstallerPaths.GitBlob(content);
        var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(Minimal(), InstallerJson.Options);
        var manifestHash = InstallerPaths.GitBlob(manifestBytes);
        var truncated = false;
        var corrupt = false;
        var requests = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            requests.Add(path);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            object response = path.EndsWith("/commits/main", StringComparison.Ordinal) ? new { sha = Commit, commit = new { tree = new { sha = Tree } } } :
                path.Contains("/git/trees/", StringComparison.Ordinal) ? new { truncated, tree = new[] {
                    new { path = "skills/test/SKILL.md", sha = hash, size = content.Length, mode = "100644", type = "blob" },
                    new { path = "estao-harness.json", sha = manifestHash, size = manifestBytes.Length, mode = "100644", type = "blob" } } } :
                path.EndsWith("/" + manifestHash, StringComparison.Ordinal) ? new { encoding = "base64", content = Convert.ToBase64String(manifestBytes) } :
                path.EndsWith("/" + hash, StringComparison.Ordinal) ? new { encoding = "base64", content = Convert.ToBase64String(corrupt ? Bytes("tampered") : content) } :
                new { full_name = GitHubRepositorySource.Repository };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response)) };
        }));
        var adapter = new HarnessAdapter("claude", Path.Combine(Path.GetTempPath(), "estao-source-fixture"), _ => null);
        var source = new GitHubRepositorySource(client);
        var snapshot = await source.PrepareAsync("fake-token", adapter);
        Assert.Equal(Commit, snapshot.Commit);
        Assert.Equal(content, Assert.Single(snapshot.Files).Content);
        var artifact = Assert.Single(snapshot.Artifacts!);
        Assert.Equal("Test skill", artifact.Description);
        Assert.Equal("test", artifact.Id);
        Assert.Equal("skills/test", artifact.Source);
        Assert.Single(requests, p => p.EndsWith("/commits/main", StringComparison.Ordinal));
        Assert.Contains(requests, p => p.Contains("/git/trees/" + Tree, StringComparison.Ordinal));
        corrupt = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => source.PrepareAsync("fake-token", adapter));
        truncated = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => source.PrepareAsync("fake-token", adapter));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task discovery_finds_repository_and_shared_skills_without_approving_stale_manifest(bool stale)
    {
        var blobs = new Dictionary<string, byte[]>();
        void Add(string path, string text) => blobs[path] = Bytes(text);
        Add("skills/repository-only/SKILL.md", "---\nname: repository-only\ndescription: Repository skill\n---\nBody\n");
        Add("skills/shared/SKILL.md", "---\nname: shared\ndescription: Shared skill\n---\nBody\n");
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new SourceManifest
        {
            Commit = stale ? new string('c', 40) : Commit, // Old pin must not prevent browsing or imply approval.
            Artifacts = [new() { Id = "shared", Source = "skills/shared", Harnesses = ["claude", "codex"] }],
            Exclusions = [new("skills/repository-only", "unsupported", "Not approved for installation.")]
        }, InstallerJson.Options);
        blobs["estao-harness.json"] = manifest;
        var entries = blobs.Select(pair => new { path = pair.Key, sha = InstallerPaths.GitBlob(pair.Value), size = pair.Value.Length, mode = "100644", type = "blob" }).ToArray();
        using var client = new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            object response = path.EndsWith("/commits/main", StringComparison.Ordinal)
                ? new { sha = Commit, commit = new { tree = new { sha = Tree } } }
                : path.Contains("/git/trees/", StringComparison.Ordinal) ? new { truncated = false, tree = entries }
                : path.Contains("/git/blobs/", StringComparison.Ordinal)
                    ? new { encoding = "base64", content = Convert.ToBase64String(blobs.Values.Single(bytes => InstallerPaths.GitBlob(bytes) == path[(path.LastIndexOf('/') + 1)..])) }
                    : new { full_name = GitHubRepositorySource.Repository };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response)) };
        }));
        var library = await new GitHubRepositorySource(client).DiscoverSkillsAsync("fake-token");
        Assert.Equal(Commit, library.Commit);
        Assert.Equal(2, library.Skills.Count);
        var approved = library.Skills.Single(skill => skill.Id == "shared");
        Assert.Equal(!stale, approved.Installable);
        Assert.Equal(["claude", "codex"], approved.CompatibleHarnesses);
        var unapproved = library.Skills.Single(skill => skill.Id == "repository-only");
        Assert.False(unapproved.Installable);
        Assert.Empty(unapproved.CompatibleHarnesses);
    }

    [Fact]
    public async Task discovery_keeps_root_and_malformed_skills_visible_without_a_manifest()
    {
        var blobs = new Dictionary<string, byte[]>
        {
            ["SKILL.md"] = Bytes("---\nname: root-skill\ndescription: Root skill\n---\n"),
            ["skills/broken/SKILL.md"] = Bytes("---\nname: [unclosed\ndescription: broken\n---\n"),
            ["skills/quoted/SKILL.md"] = Bytes("---\n'name': \"quoted\"\ndescription: Quoted YAML keys are valid\n---\n")
        };
        var entries = blobs.Select(pair => new { path = pair.Key, sha = InstallerPaths.GitBlob(pair.Value), size = pair.Value.Length, mode = "100644", type = "blob" }).ToArray();
        using var client = new HttpClient(new Handler(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            object response = path.EndsWith("/commits/main", StringComparison.Ordinal)
                ? new { sha = Commit, commit = new { tree = new { sha = Tree } } }
                : path.Contains("/git/trees/", StringComparison.Ordinal) ? new { truncated = false, tree = entries }
                : path.Contains("/git/blobs/", StringComparison.Ordinal)
                    ? new { encoding = "base64", content = Convert.ToBase64String(blobs.Values.Single(bytes => InstallerPaths.GitBlob(bytes) == path[(path.LastIndexOf('/') + 1)..])) }
                    : new { full_name = GitHubRepositorySource.Repository };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response)) };
        }));
        var library = await new GitHubRepositorySource(client).DiscoverSkillsAsync("fake-token");
        Assert.Equal(3, library.Skills.Count);
        Assert.All(library.Skills, skill => Assert.False(skill.Installable));
        Assert.Equal(".", library.Skills.Single(skill => skill.Id == "root-skill").Source);
        Assert.NotNull(library.Skills.Single(skill => skill.Id == "broken").Issue);
        Assert.Null(library.Skills.Single(skill => skill.Id == "quoted").Issue);
    }

    [Fact]
    public async Task real_github_snapshot_can_be_prepared_in_an_isolated_profile_when_explicitly_enabled()
    {
        var token = Environment.GetEnvironmentVariable("HMY_ESTAO_TEST_GITHUB_TOKEN");
        if (string.IsNullOrWhiteSpace(token)) return; // Opt-in integration, never uses personal configuration.
        var root = Path.Combine(Path.GetTempPath(), "estao-live-source-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var client = GitHubRepositorySource.CreateClient();
            var adapter = new HarnessAdapter("codex", Path.Combine(root, "profile"), _ => null);
            var snapshot = await new GitHubRepositorySource(client).PrepareAsync(token, adapter, reference: ManifestContract.Bootstrap().Commit);
            Assert.Equal(22, snapshot.Coverage.Count(c => c.Classification == "installable"));
            Assert.Equal(450, snapshot.Files.Count);
            var installer = new HarnessInstallationService(Path.Combine(root, "state"));
            var result = await installer.ApplyAsync(adapter, await installer.PlanAsync(adapter, snapshot), false);
            Assert.Equal(450, result.Created);
            Assert.All((await installer.PlanAsync(adapter, snapshot)).Files, f => Assert.Equal(InstallAction.AlreadyPresent, f.Action));
            // Reuse the already verified immutable bytes to exercise all adapters without downloading four copies.
            var manifest = ManifestContract.Bootstrap();
            foreach (var id in new[] { "claude", "copilot", "opencode" })
            {
                var target = new HarnessAdapter(id, Path.Combine(root, id), _ => null);
                var files = new List<PreparedFile>();
                foreach (var artifact in manifest.Artifacts.Where(a => a.Harnesses.Contains(id)))
                {
                    var payloads = snapshot.Files.Where(f => f.ArtifactId == artifact.Id).Select(f =>
                    {
                        var relative = Path.GetRelativePath(Path.Combine(adapter.SkillsRoot, artifact.Id), f.RelativePath).Replace('\\', '/');
                        return (new SourceEntry(artifact.Source + "/" + relative, InstallerPaths.GitBlob(f.Content), f.Content.Length, "100644"), f.Content);
                    }).ToList();
                    files.AddRange(target.Prepare(artifact, payloads));
                }
                var prepared = snapshot with { Files = files };
                Assert.Equal(21, files.Select(f => f.ArtifactId).Distinct().Count());
                var applied = await installer.ApplyAsync(target, await installer.PlanAsync(target, prepared), false);
                Assert.Equal(files.Count, applied.Created);
                Assert.All((await installer.PlanAsync(target, prepared)).Files, f => Assert.Equal(InstallAction.AlreadyPresent, f.Action));
            }
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static SourceManifest Minimal() => new()
    {
        Version = "test", Artifacts = [new() { Id = "test", Source = "skills/test", Harnesses = ["claude"] }]
    };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
