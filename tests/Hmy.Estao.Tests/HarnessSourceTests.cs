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
    public async Task discovery_partitions_curated_and_other_items_and_normalizes_localized_categories()
    {
        var fixture = new Fixture();
        fixture.File("skills/a/SKILL.md", "---\nname: a\ndescription: a skill\ndescriptions:\n  es: Una\n---\nBody\n");
        fixture.Skill("skills/b", "b");
        fixture.Skill("plugins/a", "a"); // Same folder name: stable identity remains unique and source-bound.
        fixture.Skill("skills/other", "other");
        fixture.File("docs/howto.md", "Instructions");
        fixture.File("assets/license.txt", "License");
        fixture.Catalog(new HubCatalog
        {
            SchemaVersion = 1,
            DefaultLanguage = "en",
            Categories = new() { ["core"] = new() { ["en"] = "Core", ["es"] = "General" }, ["database"] = new() { ["en"] = "Database" } },
            Items =
            [
                new() { Id = "curated-a", Type = "skill", Path = "skills/a", Categories = " core, database,core " },
                new() { Id = "curated-b", Type = "skill", Path = "skills/b", Categories = "core" },
                new() { Id = "howto", Type = "instruction", Path = "docs/howto.md", Categories = "core" },
                new() { Id = "license", Type = "file", Path = "assets/license.txt", Categories = "core" }
            ]
        });
        using var client = fixture.Client();
        var library = await new GitHubRepositorySource(client).DiscoverSkillsAsync("fake-token");

        Assert.Null(library.CatalogError);
        Assert.Equal("en", library.DefaultLanguage);
        Assert.Equal("Core", library.Categories!["core"]["en"]);
        Assert.Equal(2, library.Skills.Count(s => s.Curated && s.ItemType == "skill"));
        Assert.Equal(2, library.Skills.Count(s => s.Curated && s.ItemType != "skill"));
        Assert.Equal(["core", "database"], library.Skills.Single(s => s.Id == "curated-a").Categories);
        Assert.Equal("Una", library.Skills.Single(s => s.Id == "curated-a").Descriptions!["es"]);
        Assert.Equal("a skill", library.Skills.Single(s => s.Id == "curated-a").Description);
        Assert.Empty(library.Skills.Single(s => s.Id == "howto").Description);
        Assert.All(library.Skills.Where(s => s.Curated && s.ItemType == "skill"), s => Assert.Equal(HarnessAdapter.Ids, s.CompatibleHarnesses));
        Assert.Single(library.Skills, s => s.Source == "plugins/a");
        Assert.NotEqual("a", library.Skills.Single(s => s.Source == "plugins/a").Id);
        Assert.True(library.Skills.Single(s => s.Source == "skills/other").Installable);
        Assert.False(library.Skills.Single(s => s.Source == "skills/other").Curated);
    }

    [Fact]
    public async Task unlisted_skills_also_read_translations_from_frontmatter()
    {
        var fixture = new Fixture();
        fixture.File("skills/other/SKILL.md", "---\nname: other\ndescription: Standard fallback\ndescriptions:\n  es: Otra skill\n---\nBody\n");
        using var client = fixture.Client();
        var source = new GitHubRepositorySource(client);
        var library = await source.DiscoverSkillsAsync("fake-token");
        var skill = Assert.Single(library.Skills);
        Assert.False(skill.Curated);
        Assert.Equal("Standard fallback", skill.Description);
        Assert.Equal("Otra skill", skill.Descriptions!["es"]);
        var adapter = new HarnessAdapter("claude", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), _ => null);
        var prepared = await source.PrepareAsync("fake-token", adapter, sourcePath: skill.Source);
        Assert.Equal("Standard fallback", Assert.Single(prepared.Artifacts!).Description);
    }

    [Theory]
    [InlineData("description")]
    [InlineData("descriptions")]
    public async Task catalog_description_fields_are_rejected_not_used_as_overrides(string field)
    {
        var fixture = new Fixture();
        fixture.Skill("skills/a", "a");
        fixture.RawCatalog("{\"schemaVersion\":1,\"defaultLanguage\":\"en\",\"categories\":{},\"items\":[" +
            "{\"id\":\"a\",\"type\":\"skill\",\"path\":\"skills/a\",\"categories\":\"\",\"" + field + "\":{\"en\":\"Override\"}}]}");
        using var client = fixture.Client();
        var library = await new GitHubRepositorySource(client).DiscoverSkillsAsync("fake-token");
        Assert.NotNull(library.CatalogError);
        var skill = Assert.Single(library.Skills);
        Assert.False(skill.Curated);
        Assert.Equal("a skill", skill.Description);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task missing_or_invalid_catalog_does_not_hide_safe_other_skills(bool malformed)
    {
        var fixture = new Fixture();
        fixture.Skill("skills/other", "other");
        if (malformed) fixture.RawCatalog("{ invalid json");
        using var client = fixture.Client();
        var library = await new GitHubRepositorySource(client).DiscoverSkillsAsync("fake-token");
        Assert.NotNull(library.CatalogError);
        var skill = Assert.Single(library.Skills);
        Assert.Equal("skills/other", skill.Source);
        Assert.False(skill.Curated);
        Assert.True(skill.Installable);
    }

    [Fact]
    public async Task missing_catalog_entries_are_skipped_without_hiding_available_entries()
    {
        var fixture = new Fixture();
        fixture.Skill("skills/available", "available");
        fixture.File("docs/available.md", "Available instructions");
        fixture.Catalog(new HubCatalog
        {
            SchemaVersion = 1, DefaultLanguage = "en", Categories = [],
            Items =
            [
                new() { Id = "available", Type = "skill", Path = "skills/available", Categories = "" },
                new() { Id = "available-guide", Type = "instruction", Path = "docs/available.md", Categories = "" },
                new() { Id = "prd", Type = "skill", Path = ".agents/skills/prd", Categories = "" },
                new() { Id = "sqlcmd-shofia-skill", Type = "skill", Path = ".agents/skills/sqlcmd-shofia-skill", Categories = "" },
                new() { Id = "missing-guide", Type = "instruction", Path = "docs/missing.md", Categories = "" },
                new() { Id = "missing-file", Type = "file", Path = "templates/missing.json", Categories = "" }
            ]
        });
        using var client = fixture.Client();
        var source = new GitHubRepositorySource(client);
        var library = await source.DiscoverSkillsAsync("fake-token");

        Assert.Null(library.CatalogError);
        Assert.Equal(["available", "available-guide"], library.Skills.Select(skill => skill.Id));
        Assert.All(library.Skills, skill => Assert.True(skill.Curated));
        var available = Assert.Single(library.Skills, skill => skill.ItemType == "skill");
        Assert.True(available.Installable);
        var adapter = new HarnessAdapter("claude", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), _ => null);
        var prepared = await source.PrepareAsync("fake-token", adapter);
        Assert.Equal("available", Assert.Single(prepared.Artifacts!).Id);
        Assert.Equal(Bytes("Available instructions"), await source.ReadCatalogFileAsync("fake-token", "docs/available.md", Commit));
    }

    [Fact]
    public async Task entirely_missing_catalog_entries_do_not_hide_unlisted_skills()
    {
        var fixture = new Fixture();
        fixture.Skill("skills/other", "other");
        fixture.Catalog(new HubCatalog { SchemaVersion = 1, DefaultLanguage = "en", Categories = new() { ["core"] = new() { ["en"] = "Core" } },
            Items = [new() { Id = "bad", Type = "skill", Path = "skills/missing", Categories = "core" }] });
        using var client = fixture.Client();
        var library = await new GitHubRepositorySource(client).DiscoverSkillsAsync("fake-token");
        Assert.Null(library.CatalogError);
        Assert.False(Assert.Single(library.Skills).Curated);
    }

    [Fact]
    public async Task skills_with_missing_dependencies_stay_visible_but_cannot_be_prepared()
    {
        var fixture = new Fixture();
        foreach (var id in new[] { "available", "dependent", "transitive" }) fixture.Skill("skills/" + id, id);
        fixture.Catalog(new HubCatalog
        {
            SchemaVersion = 1, DefaultLanguage = "en", Categories = [],
            Items =
            [
                new() { Id = "available", Type = "skill", Path = "skills/available", Categories = "" },
                new() { Id = "missing", Type = "skill", Path = "skills/missing", Categories = "" },
                new() { Id = "dependent", Type = "skill", Path = "skills/dependent", Categories = "", Dependencies = ["missing"] },
                new() { Id = "transitive", Type = "skill", Path = "skills/transitive", Categories = "", Dependencies = ["dependent"] }
            ]
        });
        using var client = fixture.Client();
        var source = new GitHubRepositorySource(client);
        var library = await source.DiscoverSkillsAsync("fake-token");
        Assert.Null(library.CatalogError);
        Assert.Equal(3, library.Skills.Count);
        Assert.All(library.Skills, skill => Assert.True(skill.Curated));
        Assert.True(Assert.Single(library.Skills, skill => skill.Id == "available").Installable);
        foreach (var skill in library.Skills.Where(skill => skill.Id != "available"))
        {
            Assert.False(skill.Installable);
            Assert.Contains("dependency is missing", skill.Issue);
        }
        var adapter = new HarnessAdapter("claude", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), _ => null);
        var prepared = await source.PrepareAsync("fake-token", adapter);
        Assert.Equal("available", Assert.Single(prepared.Artifacts!).Id);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.PrepareAsync("fake-token", adapter, sourcePath: "skills/dependent"));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.PrepareAsync("fake-token", adapter, sourcePath: "skills/transitive"));
    }

    [Fact]
    public async Task catalog_rejects_invalid_language_fallback_categories_and_dependencies()
    {
        foreach (var invalid in new Func<HubCatalog>[]
        {
            () => Catalog("EN", "en", "core", []),
            () => Catalog("en", "es", "core", []),
            () => Catalog("en", "en", "unknown", []),
            () => Catalog("en", "en", "core", ["missing"])
        })
        {
            var fixture = new Fixture(); fixture.Skill("skills/a", "a"); fixture.Catalog(invalid());
            using var client = fixture.Client();
            var library = await new GitHubRepositorySource(client).DiscoverSkillsAsync("fake-token");
            Assert.NotNull(library.CatalogError);
            Assert.False(Assert.Single(library.Skills).Curated);
        }

        static HubCatalog Catalog(string language, string labelLanguage, string itemCategory, List<string> dependencies) => new()
        {
            SchemaVersion = 1, DefaultLanguage = language,
            Categories = new() { ["core"] = new() { [labelLanguage] = "Core" } },
            Items = [new() { Id = "a", Type = "skill", Path = "skills/a",
                Categories = itemCategory, Dependencies = dependencies }]
        };
    }

    [Fact]
    public async Task prepare_pins_commit_installs_curated_dependency_closure_and_explicit_unlisted_source_only()
    {
        var fixture = new Fixture();
        fixture.Skill("skills/a", "a"); fixture.Skill("skills/b", "b"); fixture.Skill("skills/other", "other");
        fixture.File("docs/readme.md", "not installed");
        fixture.Catalog(new HubCatalog { SchemaVersion = 1, DefaultLanguage = "en",
            Categories = new() { ["core"] = new() { ["en"] = "Core" } },
            Items =
            [
                new() { Id = "registered-a", Type = "skill", Path = "skills/a", Categories = "core", Dependencies = ["registered-b"] },
                new() { Id = "registered-b", Type = "skill", Path = "skills/b", Categories = "core" },
                new() { Id = "doc", Type = "file", Path = "docs/readme.md", Categories = "core" }
            ] });
        using var client = fixture.Client();
        var source = new GitHubRepositorySource(client);
        var adapter = new HarnessAdapter("claude", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), _ => null);
        var curated = await source.PrepareAsync("fake-token", adapter);
        Assert.Equal(Commit, curated.Commit);
        Assert.Equal(new[] { "registered-a", "registered-b" }, curated.Artifacts!.Select(a => a.Id));
        Assert.All(curated.Files, file => Assert.Contains(file.ArtifactId, new[] { "registered-a", "registered-b" }));
        var dependencyClosure = await source.PrepareAsync("fake-token", adapter, sourcePath: "skills/a");
        Assert.Equal(new[] { "registered-b", "registered-a" }, dependencyClosure.Artifacts!.Select(a => a.Id));
        var explicitItem = await source.PrepareAsync("fake-token", adapter, sourcePath: "skills/other");
        Assert.Equal(new[] { "other" }, explicitItem.Artifacts!.Select(a => a.Id));
        Assert.All(explicitItem.Files, file => Assert.Equal("other", file.ArtifactId));
    }

    [Fact]
    public async Task catalog_file_export_is_whitelisted_at_the_pinned_revision()
    {
        var fixture = new Fixture();
        fixture.Skill("skills/a", "a");
        fixture.File("docs/howto.md", "Safe export bytes");
        fixture.File("docs/unlisted.md", "Must not export");
        fixture.Catalog(new HubCatalog { SchemaVersion = 1, DefaultLanguage = "en",
            Categories = new() { ["core"] = new() { ["en"] = "Core" } },
            Items = [new() { Id = "howto", Type = "instruction", Path = "docs/howto.md", Categories = "core" }] });
        using var client = fixture.Client();
        var source = new GitHubRepositorySource(client);
        Assert.Equal(Bytes("Safe export bytes"), await source.ReadCatalogFileAsync("fake-token", "docs/howto.md", Commit));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.ReadCatalogFileAsync("fake-token", "docs/unlisted.md", Commit));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.ReadCatalogFileAsync("fake-token", "skills/a/SKILL.md", Commit));
    }

    [Fact]
    public async Task discovered_other_ids_remain_unique_and_match_review_artifact_ids()
    {
        var fixture = new Fixture();
        fixture.Skill("skills/a", "a");
        fixture.Skill("plugins/a", "a");
        fixture.Skill("skills/custom-id", "custom-id");
        fixture.Catalog(new HubCatalog { SchemaVersion = 1, DefaultLanguage = "en", Categories = [],
            Items = [new() { Id = "custom-id", Type = "skill", Path = "skills/a", Categories = "" }] });
        using var client = fixture.Client();
        var source = new GitHubRepositorySource(client);
        var library = await source.DiscoverSkillsAsync("fake-token");
        Assert.Null(library.CatalogError);
        Assert.Equal(library.Skills.Count, library.Skills.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("custom-id", library.Skills.Single(item => item.Curated).Id);
        var adapter = new HarnessAdapter("claude", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), _ => null);
        foreach (var item in library.Skills)
        {
            var review = await source.PrepareAsync("fake-token", adapter, reference: library.Commit, sourcePath: item.Source);
            Assert.Equal(item.Id, Assert.Single(review.Artifacts!).Id);
            Assert.All(review.Files, file => Assert.Equal(item.Id, file.ArtifactId));
        }
    }

    [Theory]
    [InlineData("unknown-language")]
    [InlineData("null-categories")]
    [InlineData("null-default")]
    [InlineData("null-dependency")]
    public async Task malformed_catalog_fields_do_not_abort_other_skill_discovery(string failure)
    {
        var fixture = new Fixture(); fixture.Skill("skills/a", "a");
        var catalog = new HubCatalog { SchemaVersion = 1, DefaultLanguage = "en", Categories = [],
            Items = [new() { Id = "a", Type = "skill", Path = "skills/a", Categories = "" }] };
        if (failure == "unknown-language") catalog.DefaultLanguage = "zz";
        if (failure == "null-categories") catalog.Items[0].Categories = null!;
        if (failure == "null-default") catalog.DefaultLanguage = null!;
        if (failure == "null-dependency") catalog.Items[0].Dependencies = [null!];
        fixture.Catalog(catalog);
        using var client = fixture.Client();
        var library = await new GitHubRepositorySource(client).DiscoverSkillsAsync("fake-token");
        Assert.NotNull(library.CatalogError);
        var item = Assert.Single(library.Skills);
        Assert.False(item.Curated);
        Assert.True(item.Installable);
    }

    [Fact]
    public async Task root_skill_is_visible_but_not_installable_as_a_folder()
    {
        var fixture = new Fixture();
        fixture.File("SKILL.md", "---\nname: root-skill\ndescription: Root description\n---\n");
        using var client = fixture.Client();
        var library = await new GitHubRepositorySource(client).DiscoverSkillsAsync("fake-token");
        var item = Assert.Single(library.Skills);
        Assert.Equal("root-skill", item.Name);
        Assert.Equal(".", item.Source);
        Assert.False(item.Installable);
        Assert.NotNull(item.Issue);
    }

    [Fact]
    public async Task pinned_prepare_and_export_reject_a_different_returned_commit()
    {
        var fixture = new Fixture { ReturnedCommit = new string('c', 40) };
        using var client = fixture.Client();
        var source = new GitHubRepositorySource(client);
        var adapter = new HarnessAdapter("claude", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), _ => null);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.PrepareAsync("fake-token", adapter, reference: Commit, sourcePath: "skills/a"));
        await Assert.ThrowsAsync<InvalidDataException>(() => source.ReadCatalogFileAsync("fake-token", "docs/howto.md", Commit));
    }

    [Fact]
    public async Task rejects_unsafe_tree_and_corrupt_blobs()
    {
        var fixture = new Fixture(); fixture.Skill("skills/a", "a"); fixture.CorruptBlobs = true;
        using var client = fixture.Client();
        await Assert.ThrowsAsync<InvalidDataException>(() => new GitHubRepositorySource(client).DiscoverSkillsAsync("fake-token"));
        fixture.CorruptBlobs = false; fixture.UnsafePath = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => new GitHubRepositorySource(client).DiscoverSkillsAsync("fake-token"));
        fixture.UnsafePath = false; fixture.Truncated = true;
        await Assert.ThrowsAsync<InvalidDataException>(() => new GitHubRepositorySource(client).DiscoverSkillsAsync("fake-token"));
    }

    [Fact]
    public async Task prepare_omits_invalid_curated_metadata_and_rejects_it_when_explicitly_requested()
    {
        var fixture = new Fixture();
        fixture.File("skills/a/SKILL.md", "---\nname: not-a\ndescription: Bad identity\n---\n");
        fixture.Catalog(new HubCatalog { SchemaVersion = 1, DefaultLanguage = "en",
            Categories = new() { ["core"] = new() { ["en"] = "Core" } },
            Items = [new() { Id = "a-registered-id", Type = "skill", Path = "skills/a", Categories = "core" }] });
        using var client = fixture.Client();
        var source = new GitHubRepositorySource(client);
        var library = await source.DiscoverSkillsAsync("fake-token");
        var listed = Assert.Single(library.Skills);
        Assert.True(listed.Curated);
        Assert.False(listed.Installable);
        Assert.Empty(listed.CompatibleHarnesses);
        var adapter = new HarnessAdapter("claude", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), _ => null);
        var snapshot = await source.PrepareAsync("fake-token", adapter);
        Assert.Empty(snapshot.Artifacts!);
        await Assert.ThrowsAsync<InvalidDataException>(() => source.PrepareAsync("fake-token", adapter, sourcePath: "skills/a"));
    }

    private sealed class Fixture
    {
        private readonly Dictionary<string, byte[]> _blobs = new(StringComparer.Ordinal);
        public bool CorruptBlobs { get; set; }
        public bool UnsafePath { get; set; }
        public bool Truncated { get; set; }
        public string ReturnedCommit { get; set; } = Commit;
        private static byte[] CatalogBytes(HubCatalog catalog) => JsonSerializer.SerializeToUtf8Bytes(catalog, InstallerJson.Options);
        public void Skill(string path, string name) => File(path + "/SKILL.md", $"---\nname: {name}\ndescription: {name} skill\n---\nBody\n");
        public void File(string path, string text) => _blobs[path] = Bytes(text);
        public void Catalog(HubCatalog catalog) => RawCatalog(Encoding.UTF8.GetString(CatalogBytes(catalog)));
        public void RawCatalog(string text) => File("hub-manifest.json", text);
        public HttpClient Client() => new(new Handler(Respond));
        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.PathAndQuery;
            object response;
            if (path.EndsWith("/commits/main", StringComparison.Ordinal) || path.EndsWith("/commits/" + Commit, StringComparison.Ordinal))
                response = new { sha = ReturnedCommit, commit = new { tree = new { sha = Tree } } };
            else if (path.Contains("/git/trees/", StringComparison.Ordinal))
            {
                var entries = _blobs.Select(pair => new { path = pair.Key, sha = InstallerPaths.GitBlob(pair.Value), size = pair.Value.Length, mode = "100644", type = "blob" }).ToList();
                if (UnsafePath) entries.Add(new { path = "../evil", sha = new string('c', 40), size = 1, mode = "100644", type = "blob" });
                response = new { truncated = Truncated, tree = entries };
            }
            else if (path.Contains("/git/blobs/", StringComparison.Ordinal))
            {
                var sha = path[(path.LastIndexOf('/') + 1)..];
                var bytes = _blobs.Values.First(value => InstallerPaths.GitBlob(value) == sha);
                if (CorruptBlobs) bytes = Bytes("tampered");
                response = new { encoding = "base64", content = Convert.ToBase64String(bytes) };
            }
            else response = new { full_name = GitHubRepositorySource.Repository };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response)) };
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }
}
