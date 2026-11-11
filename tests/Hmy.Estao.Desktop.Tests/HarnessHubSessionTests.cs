using System.Net;
using System.Text;
using System.Text.Json;
using Hmy.Estao.Core.Harnesses.Installer;
using Hmy.Estao.Core.Security;
using Hmy.Estao.Desktop.HarnessHub;

namespace Hmy.Estao.Desktop.Tests;

public sealed class HarnessHubSessionTests
{
    [Fact]
    public async Task UninstallWorksWithoutAuthorizationAndRefreshesAllSharedObservers()
    {
        using var fixture = new Fixture();
        var file = Path.Combine(fixture.Adapter("codex").SkillsRoot, "personal", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "---\nname: personal\ndescription: Offline personal skill\n---\n");
        using var session = fixture.Session(candidates: _ => Task.FromResult<IReadOnlyList<string>>([]));
        session.Command(new("check")); await session.CurrentOperation;
        Assert.False(session.State.Connected);
        Assert.NotEmpty(Assert.Single(session.State.Skills!).Occurrences);
        fixture.Requests.Clear();

        session.Command(new("uninstall-skill", SkillId: "personal")); await session.CurrentOperation;

        Assert.Null(session.State.Error);
        Assert.False(session.State.Busy);
        Assert.Empty(session.State.Skills!);
        Assert.Empty(fixture.Requests);
        Assert.False(File.Exists(file));
        Assert.Equal("hub.uninstall_complete", session.State.ProgressMessage?.Code);
        Assert.Throws<ArgumentException>(() => session.Command(new("uninstall-skill", SkillId: "personal")));
    }

    [Fact]
    public async Task CheckRefreshesExternallyAddedAndRemovedSkills()
    {
        using var fixture = new Fixture();
        using var session = fixture.Session(candidates: _ => Task.FromResult<IReadOnlyList<string>>([]));
        session.Command(new("check")); await session.CurrentOperation;
        var directory = Path.Combine(fixture.Adapter("claude").SkillsRoot, "added");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "SKILL.md"), "---\nname: added\ndescription: Added externally\n---\n");
        session.Command(new("check")); await session.CurrentOperation;
        Assert.Equal("added", Assert.Single(session.State.Skills!).Name);
        Directory.Delete(directory, recursive: true);
        session.Command(new("check")); await session.CurrentOperation;
        Assert.Empty(session.State.Skills!);
    }

    [Fact]
    public async Task MultiToolInstallationReusesImmutableDownloadsAndTheVerifiedCredential()
    {
        using var fixture = new Fixture { IncludeExtraSkills = true };
        var credentialSearches = 0;
        using var session = fixture.Session(candidates: _ =>
        {
            credentialSearches++;
            return Task.FromResult<IReadOnlyList<string>>(["fixture-token"]);
        });
        session.Command(new("check")); await session.CurrentOperation;
        fixture.Requests.Clear();
        session.Command(new("install-skill", SkillId: "sample")); await session.CurrentOperation;

        Assert.Null(session.State.Error);
        Assert.Equal(1, credentialSearches);
        Assert.Equal(3, session.State.Results.Count(result => result.Outcome == "installed"));
        Assert.Single(fixture.Requests, path => path.EndsWith("/commits/" + Fixture.Commit, StringComparison.Ordinal));
        var gitObjects = fixture.Requests.Where(path => path.Contains("/git/", StringComparison.Ordinal)).GroupBy(path => path).ToArray();
        Assert.NotEmpty(gitObjects);
        Assert.All(gitObjects, requests => Assert.Single(requests));
    }

    [Fact]
    public async Task RepositoryInventoryDownloadsSkillMetadataConcurrently()
    {
        using var fixture = new Fixture { IncludeExtraSkills = true };
        var hashes = new[] { "base", "unrelated" }.Select(id => InstallerPaths.GitBlob(
            Encoding.UTF8.GetBytes($"---\nname: {id}\ndescription: Test skill\n---\n"))).Append(InstallerPaths.GitBlob(
            Encoding.UTF8.GetBytes("---\nname: sample\ndescription: Shared test skill\n---\nFixture, never execute.\n"))).ToHashSet();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var concurrentReads = 0;
        fixture.BeforeRead = async (request, ct) =>
        {
            if (!hashes.Contains(request.RequestUri!.Segments.Last())) return;
            if (Interlocked.Increment(ref concurrentReads) == 3) entered.TrySetResult();
            await release.Task.WaitAsync(ct);
        };
        using var session = fixture.Session();
        session.Command(new("check"));
        try { await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { release.TrySetResult(); }
        await session.CurrentOperation;

        Assert.Null(session.State.Error);
        Assert.True(session.State.LibraryLoaded);
        Assert.Equal(3, session.State.Skills!.Count);
    }

    [Fact]
    public async Task InstallingOneSkillRefreshesOnlyItsDependenciesAndSharedOccurrences()
    {
        using var fixture = new Fixture { IncludeExtraSkills = true };
        var local = Path.Combine(fixture.Adapter("claude").SkillsRoot, "personal", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        await File.WriteAllTextAsync(local, "---\nname: personal\ndescription: Personal skill\n---\n");
        using var session = fixture.Session();
        session.Command(new("check")); await session.CurrentOperation;
        var personal = Assert.Single(session.State.Skills!, skill => skill.Id == "personal");
        var unrelated = Assert.Single(session.State.Skills!, skill => skill.Id == "unrelated");
        await File.WriteAllTextAsync(local, "Changed externally; do not rescan this unrelated skill.");

        session.Command(new("install-skill", "codex", SkillId: "sample"));
        await session.CurrentOperation;

        Assert.Null(session.State.Error);
        Assert.Same(personal, Assert.Single(session.State.Skills!, skill => skill.Id == "personal"));
        Assert.Same(unrelated, Assert.Single(session.State.Skills!, skill => skill.Id == "unrelated"));
        foreach (var id in new[] { "sample", "base" })
        {
            var installed = Assert.Single(session.State.Skills!, skill => skill.Id == id);
            Assert.Contains(installed.Occurrences, occurrence => occurrence.ToolId == "codex");
            Assert.Contains(installed.Occurrences, occurrence => occurrence.ToolId == "copilot");
        }
    }

    [Fact]
    public async Task TargetedRefreshRetainsOtherToolsCopiesOfTheInstalledSkill()
    {
        using var fixture = new Fixture();
        var file = Path.Combine(fixture.Adapter("claude").SkillsRoot, "sample", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "---\nname: sample\ndescription: Personal sample\n---\n");
        using var session = fixture.Session();
        session.Command(new("check")); await session.CurrentOperation;
        session.Command(new("install-skill", "codex", SkillId: "sample")); await session.CurrentOperation;

        Assert.Null(session.State.Error);
        var skill = Assert.Single(session.State.Skills!, skill => skill.Id == "sample");
        Assert.Contains(skill.Occurrences, occurrence => occurrence.ToolId == "claude" && occurrence.Path == file);
        Assert.Contains(skill.Occurrences, occurrence => occurrence.ToolId == "codex");
        Assert.NotNull(skill.Issue);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("codex")]
    [InlineData("claude")]
    public async Task DirectSkillInstallationUsesTheChosenTargetsAndIncludesDependencies(string? target)
    {
        using var fixture = new Fixture { IncludeExtraSkills = true };
        using var session = fixture.Session();
        session.Command(new("install-skill", target, SkillId: "sample"));
        await session.CurrentOperation;

        Assert.Null(session.State.Error);
        Assert.Null(session.State.Review);
        Assert.False(session.State.Busy);
        Assert.Equal(target is null ? new[] { "codex", "claude", "copilot" } : [target],
            session.State.Results.Where(result => result.Outcome == "installed").Select(result => result.Id));
        foreach (var id in new[] { "codex", "claude" })
        {
            Assert.Equal(target is null || target == id, File.Exists(Path.Combine(fixture.Adapter(id).SkillsRoot, "sample", "SKILL.md")));
            Assert.Equal(target is null || target == id, File.Exists(Path.Combine(fixture.Adapter(id).SkillsRoot, "base", "SKILL.md")));
            Assert.False(File.Exists(Path.Combine(fixture.Adapter(id).SkillsRoot, "unrelated", "SKILL.md")));
        }
    }

    [Fact]
    public async Task DirectSkillInstallationPreservesLocalConflicts()
    {
        using var fixture = new Fixture();
        var file = Path.Combine(fixture.Adapter("claude").SkillsRoot, "sample", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "Personal skill");
        using var session = fixture.Session();
        session.Command(new("install-skill", "claude", SkillId: "sample"));
        await session.CurrentOperation;

        Assert.Null(session.State.Error);
        Assert.Null(session.State.Review);
        Assert.Equal("Personal skill", await File.ReadAllTextAsync(file));
        Assert.Contains(session.State.Results, result => result.Id == "claude" && result.Outcome == "partial" && result.Conflicts == 1);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("copilot")]
    [InlineData("opencode")]
    public async Task SelectingOneSharedToolRefreshesAllThreeObserversWithoutInstallingForClaude(string target)
    {
        using var fixture = new Fixture { IncludeOpenCode = true, IncludeExtraSkills = true };
        using var session = fixture.Session();
        session.Command(new("check")); await session.CurrentOperation;
        session.Command(new("install-skill", target, SkillId: "sample")); await session.CurrentOperation;

        Assert.Null(session.State.Error);
        var result = Assert.Single(session.State.Results);
        Assert.Equal(target, result.Id);
        Assert.Equal("installed", result.Outcome);
        Assert.Equal(2, result.Created);
        foreach (var id in new[] { "sample", "base" })
        {
            var skill = Assert.Single(session.State.Skills!, skill => skill.Id == id);
            Assert.Equal(["codex", "copilot", "opencode"], skill.Occurrences.Select(occurrence => occurrence.ToolId).Order());
            Assert.All(skill.Occurrences, occurrence =>
            {
                Assert.True(occurrence.Shared);
                Assert.Equal(Path.Combine(fixture.Directory, "home", ".agents", "skills", id, "SKILL.md"), occurrence.Path);
            });
            Assert.Null(skill.Issue);
        }
        Assert.False(Directory.Exists(fixture.Adapter("claude").SkillsRoot));
        foreach (var id in new[] { "codex", "copilot", "opencode" })
            Assert.False(Directory.Exists(Path.Combine(fixture.Adapter(id).Profile, "skills")));
    }

    [Fact]
    public async Task InstallingForAllFourToolsWritesTheSharedSkillOnlyOnce()
    {
        using var fixture = new Fixture { IncludeOpenCode = true };
        using var session = fixture.Session();
        session.Command(new("install-skill", SkillId: "sample")); await session.CurrentOperation;

        Assert.Null(session.State.Error);
        Assert.Equal(4, session.State.Results.Count);
        Assert.All(session.State.Results, result => Assert.Equal("installed", result.Outcome));
        Assert.Equal(2, session.State.Results.Sum(result => result.Created));
        foreach (var id in new[] { "copilot", "opencode" })
            Assert.Contains(session.State.Results, result => result.Id == id && result.Created == 0 && result.Unchanged == 1);
        var skill = Assert.Single(session.State.Skills!);
        Assert.Equal(2, skill.Occurrences.Select(occurrence => occurrence.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(skill.Occurrences, occurrence => occurrence.ToolId == "claude" &&
            occurrence.Path == Path.Combine(fixture.Adapter("claude").Profile, "skills", "sample", "SKILL.md"));
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("copilot")]
    [InlineData("opencode")]
    public async Task SharedRootConflictsArePreservedRegardlessOfSelectedTool(string target)
    {
        using var fixture = new Fixture { IncludeOpenCode = true };
        var file = Path.Combine(fixture.Directory, "home", ".agents", "skills", "sample", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        const string content = "---\nname: sample\ndescription: Personal shared skill\n---\nDo not replace.";
        await File.WriteAllTextAsync(file, content);
        using var session = fixture.Session();
        session.Command(new("install-skill", target, SkillId: "sample")); await session.CurrentOperation;

        Assert.Null(session.State.Error);
        Assert.Equal(content, await File.ReadAllTextAsync(file));
        var result = Assert.Single(session.State.Results);
        Assert.Equal(target, result.Id);
        Assert.Equal("partial", result.Outcome);
        Assert.Equal(1, result.Conflicts);
        Assert.Equal(0, result.Created);
        var skill = Assert.Single(session.State.Skills!);
        Assert.Equal(["codex", "copilot", "opencode"], skill.Occurrences.Select(occurrence => occurrence.ToolId).Order());
        Assert.False(Directory.Exists(fixture.Adapter("claude").SkillsRoot));
        foreach (var id in new[] { "codex", "copilot", "opencode" })
            Assert.False(Directory.Exists(Path.Combine(fixture.Adapter(id).Profile, "skills")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("unknown")]
    public async Task DirectInstallationRequiresAValidSkill(string? skillId)
    {
        using var fixture = new Fixture();
        using var session = fixture.Session();
        if (skillId is null)
            Assert.Throws<ArgumentException>(() => session.Command(new("install-skill", SkillId: skillId)));
        else
        {
            session.Command(new("install-skill", SkillId: skillId));
            await session.CurrentOperation;
            Assert.NotNull(session.State.Error);
        }
        Assert.Null(session.State.Review);
        Assert.False(File.Exists(Path.Combine(fixture.Adapter("codex").SkillsRoot, "sample", "SKILL.md")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AuthorizationRemainsUnknownUntilTheInitialCheckCompletes(bool connected)
    {
        using var fixture = new Fixture { DenyAccess = !connected };
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = fixture.Session(candidates: ct =>
        {
            entered.TrySetResult();
            return release.Task.WaitAsync(ct);
        });
        Assert.False(session.State.AuthorizationChecked);
        session.Command(new("check"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.State.CheckingAuthorization);
        Assert.False(session.State.AuthorizationChecked);
        release.SetResult(["fixture-token"]);
        await session.CurrentOperation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.State.AuthorizationChecked);
        Assert.False(session.State.CheckingAuthorization);
        Assert.Equal(connected, session.State.Connected);
    }

    [Fact]
    public async Task FailedAccessChecksDoNotPretendTheUserIsSignedOut()
    {
        using var fixture = new Fixture();
        using var session = fixture.Session(candidates: _ => throw new HttpRequestException("Fixture network failure"));
        session.Command(new("check"));
        await session.CurrentOperation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(session.State.AuthorizationChecked);
        Assert.False(session.State.CheckingAuthorization);
        Assert.NotNull(session.State.Error);
        Assert.Equal("error.network", session.State.ErrorMessage!.Code);
    }

    [Fact]
    public async Task CancellingAnInitialAccessCheckClearsCheckingAndAllowsRetry()
    {
        using var fixture = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = fixture.Session(candidates: ct =>
        {
            entered.TrySetResult();
            return release.Task.WaitAsync(ct);
        });
        session.Command(new("check"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        session.Command(new("cancel"));
        await session.CurrentOperation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(session.State.CheckingAuthorization);
        Assert.False(session.State.AuthorizationChecked);
        Assert.False(session.State.Busy);
        release.SetResult(["fixture-token"]);
        session.Command(new("check"));
        await session.CurrentOperation.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(session.State.AuthorizationChecked);
        Assert.True(session.State.Connected);
    }

    [Fact]
    public async Task InstallAllUsesOneCommitSkipsMissingToolsAndPreservesLocalConflicts()
    {
        using var fixture = new Fixture();
        var claude = fixture.Adapter("claude");
        var existing = Path.Combine(claude.SkillsRoot, "sample", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        await File.WriteAllTextAsync(existing, "Personal skill. Must not be overwritten.");
        using var session = fixture.Session();
        session.Command(new("check")); await session.CurrentOperation;
        Assert.True(session.State.Connected);
        fixture.Requests.Clear();
        session.Command(new("review")); await session.CurrentOperation;
        var review = Assert.IsType<HubReview>(session.State.Review);
        Assert.Equal(3, review.Tools.Count);
        Assert.Equal("conflict", Assert.Single(review.Tools.Single(t => t.Id == "claude").Artifacts).Status);
        Assert.Equal("new", Assert.Single(review.Tools.Single(t => t.Id == "codex").Artifacts).Status);
        Assert.Contains(session.State.Results, r => r.Id == "opencode" && r.Outcome == "skipped");
        Assert.DoesNotContain(fixture.Requests, r => r.EndsWith("/commits/main", StringComparison.Ordinal));
        Assert.Contains(fixture.Requests, r => r.EndsWith("/commits/" + Fixture.Commit, StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(fixture.Adapter("codex").SkillsRoot, "sample", "SKILL.md")));

        session.Command(new("install", ReviewId: review.Id, Selection: review.Tools.ToDictionary(t => t.Id, _ => new[] { "sample" })));
        await session.CurrentOperation;
        Assert.Null(session.State.Review);
        Assert.Contains(session.State.Results, r => r.Id == "codex" && r.Outcome == "installed" && r.Created == 1);
        Assert.Contains(session.State.Results, r => r.Id == "claude" && r.Outcome == "partial" && r.Conflicts == 1);
        Assert.Contains(session.State.Results, r => r.Id == "copilot" && r.Outcome == "installed" && r.Created == 0 && r.Unchanged == 1);
        Assert.Equal("Personal skill. Must not be overwritten.", await File.ReadAllTextAsync(existing));
        Assert.True(File.Exists(Path.Combine(fixture.Adapter("copilot").SkillsRoot, "sample", "SKILL.md")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Adapter("copilot").Profile, "skills")));
        var sample = Assert.Single(session.State.Skills!, skill => skill.Id == "sample");
        Assert.True(sample.InLibrary);
        Assert.Contains(sample.Occurrences, occurrence => occurrence.ToolId == "codex");
    }

    [Fact]
    public async Task LocalChangesAfterReviewFailThatToolButDoNotStopOthers()
    {
        using var fixture = new Fixture();
        using var session = fixture.Session();
        session.Command(new("review")); await session.CurrentOperation;
        var review = Assert.IsType<HubReview>(session.State.Review);
        var path = Path.Combine(fixture.Adapter("codex").SkillsRoot, "sample", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, "Created after review");
        session.Command(new("install", ReviewId: review.Id, Selection: review.Tools.ToDictionary(t => t.Id, _ => new[] { "sample" })));
        await session.CurrentOperation;
        Assert.Contains(session.State.Results, r => r.Id == "codex" && r.Outcome == "failed");
        Assert.Contains(session.State.Results, r => r.Id == "claude" && r.Outcome == "installed");
        Assert.Equal("Created after review", await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task InvalidReviewAndArtifactIdsNeverWriteFiles()
    {
        using var fixture = new Fixture();
        using var session = fixture.Session();
        session.Command(new("review", "codex")); await session.CurrentOperation;
        var review = Assert.IsType<HubReview>(session.State.Review);
        Assert.Throws<ArgumentException>(() => session.Command(new("install", ReviewId: "stale", Selection: new() { ["codex"] = ["sample"] })));
        Assert.Throws<ArgumentException>(() => session.Command(new("install", ReviewId: review.Id, Selection: new() { ["codex"] = ["unknown"] })));
        Assert.False(File.Exists(Path.Combine(fixture.Adapter("codex").SkillsRoot, "sample", "SKILL.md")));
    }

    [Fact]
    public async Task CopilotTokenIsReusedOnlyAfterPrivateRepositoryAccessIsVerified()
    {
        using var fixture = new Fixture();
        var tokenFile = Path.Combine(fixture.Directory, "oauth-secrets.json");
        await new SecureOAuthTokenStore(tokenFile).SaveAsync("copilot", new OAuthToken("copilot-test-token", "fixture"));
        var before = await File.ReadAllBytesAsync(tokenFile);
        using var session = fixture.Session("copilot-test-token");
        session.Command(new("check")); await session.CurrentOperation;
        Assert.True(session.State.Connected);
        Assert.Contains("Copilot", session.State.Connection);
        fixture.DenyAccess = true;
        session.Command(new("check")); await session.CurrentOperation;
        Assert.False(session.State.Connected);
        Assert.Contains("cannot read", session.State.Connection);
        Assert.Equal(before, await File.ReadAllBytesAsync(tokenFile));
    }

    [Fact]
    public async Task RepositoryLibraryFailureKeepsLocalSkillsAvailable()
    {
        using var fixture = new Fixture { FailTree = true };
        var local = Path.Combine(fixture.Adapter("codex").SkillsRoot, "personal", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(local)!);
        await File.WriteAllTextAsync(local, "---\nname: personal\ndescription: Local skill\n---\nBody");
        using var session = fixture.Session();
        session.Command(new("check")); await session.CurrentOperation;
        Assert.False(session.State.LibraryLoaded);
        Assert.NotNull(session.State.LibraryError);
        Assert.Contains(session.State.Skills!, skill => skill.Id == "personal" && !skill.InLibrary);
    }

    [Fact]
    public async Task StandardSkillsCanBeReviewedForAllDetectedToolsWithoutAuthoredHarnessLists()
    {
        using var fixture = new Fixture();
        using var session = fixture.Session();
        session.Command(new("review", SkillId: "sample")); await session.CurrentOperation;
        var review = Assert.IsType<HubReview>(session.State.Review);
        Assert.Equal(["codex", "claude", "copilot"], review.Tools.Select(tool => tool.Id));
        Assert.DoesNotContain(session.State.Results, result => result.Outcome == "failed");
    }

    [Fact]
    public async Task LibraryAndLocalOccurrencesAreReplacedOnRefreshRatherThanAccumulated()
    {
        using var fixture = new Fixture();
        var file = Path.Combine(fixture.Adapter("codex").SkillsRoot, "sample", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "---\nname: sample\ndescription: Personal sample\n---\n");
        using var session = fixture.Session();
        for (var i = 0; i < 2; i++)
        {
            session.Command(new("check")); await session.CurrentOperation;
            Assert.Null(session.State.Error);
            Assert.True(session.State.LibraryLoaded);
            Assert.Equal(Fixture.Commit, session.State.LibraryCommit);
            var skill = Assert.Single(session.State.Skills!);
            Assert.True(skill.InLibrary);
            Assert.True(skill.Installable);
            Assert.Equal(2, skill.Occurrences.Count); // Codex and Copilot observe the same root.
        }
        File.Delete(file);
        session.Command(new("check")); await session.CurrentOperation;
        Assert.Empty(Assert.Single(session.State.Skills!).Occurrences);
    }

    [Fact]
    public async Task SingleSkillReviewIncludesOnlyTheSkillAndItsDependencyClosure()
    {
        using var fixture = new Fixture { IncludeExtraSkills = true };
        using var session = fixture.Session();
        session.Command(new("review", "codex", SkillId: "sample")); await session.CurrentOperation;
        Assert.Null(session.State.Error);
        var review = Assert.IsType<HubReview>(session.State.Review);
        Assert.Equal(["base", "sample"], Assert.Single(review.Tools).Artifacts.Select(artifact => artifact.Id).Order());
        Assert.DoesNotContain(Assert.Single(review.Tools).Artifacts, artifact => artifact.Id == "unrelated");
    }

    [Fact]
    public async Task CatalogIncludesLocalizedSkillsInstructionsAndFilesWithoutInventedCompatibilityWarnings()
    {
        using var fixture = new Fixture { IncludeFileItems = true, LocalizedSkill = true };
        using var session = fixture.Session();
        session.Command(new("check")); await session.CurrentOperation;
        Assert.Null(session.State.Error);
        Assert.Null(session.State.CatalogError);
        Assert.Equal("en", session.State.CatalogDefaultLanguage);
        Assert.Equal("General", session.State.CatalogCategories!["core"]["es"]);
        Assert.Equal(["file", "instruction", "skill"], session.State.Skills!.Select(item => item.ItemType).Order());
        Assert.All(session.State.Skills!, item =>
        {
            Assert.True(item.Curated);
            Assert.Equal(["core"], item.Categories);
            if (item.ItemType == "skill") Assert.Equal("Skill de prueba", item.Descriptions!["es"]);
            else
            {
                Assert.Null(item.Descriptions);
                Assert.Empty(item.Description);
            }
            Assert.Null(item.Issue);
            Assert.Equal(item.ItemType == "skill", item.Installable);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrInvalidManifestLeavesOtherSkillsReviewable(bool invalid)
    {
        using var fixture = new Fixture { OmitManifest = !invalid, InvalidManifest = invalid };
        using var session = fixture.Session();
        session.Command(new("check")); await session.CurrentOperation;
        Assert.Null(session.State.Error);
        Assert.True(session.State.LibraryLoaded);
        Assert.NotNull(session.State.CatalogError);
        var skill = Assert.Single(session.State.Skills!);
        Assert.False(skill.Curated);
        Assert.True(skill.InLibrary);
        Assert.True(skill.Installable);
        Assert.Null(skill.Issue);
        session.Command(new("review", "codex", SkillId: skill.Id)); await session.CurrentOperation;
        Assert.Null(session.State.Error);
        Assert.Equal(skill.Id, Assert.Single(Assert.Single(session.State.Review!.Tools).Artifacts).Id);
    }

    [Fact]
    public async Task CatalogIdsAreIndependentOfSkillFolderNamesForLocalMatchingAndReview()
    {
        using var fixture = new Fixture { SampleId = "curated-sample" };
        var file = Path.Combine(fixture.Adapter("codex").SkillsRoot, "sample", "SKILL.md");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, "---\nname: sample\ndescription: Personal copy\n---\n");
        using var session = fixture.Session();
        session.Command(new("check")); await session.CurrentOperation;
        var item = Assert.Single(session.State.Skills!);
        Assert.Equal("curated-sample", item.Id);
        Assert.Equal(2, item.Occurrences.Count);
        session.Command(new("review", "codex", SkillId: item.Id)); await session.CurrentOperation;
        Assert.Null(session.State.Error);
        var artifact = Assert.Single(Assert.Single(session.State.Review!.Tools).Artifacts);
        Assert.Equal(item.Id, artifact.Id);
        Assert.All(artifact.Files, file => Assert.Contains(Path.Combine("sample", "SKILL.md"), file.Path));
    }

    [Fact]
    public async Task CatalogFileExportUsesChosenDestinationAndPinnedSnapshotWithoutConfigInstallation()
    {
        using var fixture = new Fixture { IncludeFileItems = true };
        var destination = Path.Combine(fixture.Directory, "exported.json");
        Directory.CreateDirectory(fixture.Directory);
        using var session = fixture.Session(exportDestination: _ => destination);
        session.Command(new("check")); await session.CurrentOperation;
        fixture.Requests.Clear();
        session.Command(new("export-item", SkillId: "example-template")); await session.CurrentOperation;
        Assert.Null(session.State.Error);
        Assert.Equal("{\"sample\":true}", await File.ReadAllTextAsync(destination));
        Assert.DoesNotContain(fixture.Requests, request => request.EndsWith("/commits/main", StringComparison.Ordinal));
        Assert.False(Directory.Exists(fixture.Adapter("codex").SkillsRoot));
        Assert.Throws<ArgumentException>(() => session.Command(new("export-item", SkillId: "sample")));
    }

    [Fact]
    public async Task CancelledSaveDialogDoesNotDownloadOrWriteCatalogFiles()
    {
        using var fixture = new Fixture { IncludeFileItems = true };
        using var session = fixture.Session(exportDestination: _ => null);
        session.Command(new("check")); await session.CurrentOperation;
        fixture.Requests.Clear();
        session.Command(new("export-item", SkillId: "database-instructions"));
        Assert.False(session.State.Busy);
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task MissingToolsCannotBePreparedAndCancellationAllowsAnotherOperation()
    {
        using var fixture = new Fixture();
        using var session = fixture.Session();
        session.Command(new("review", "opencode")); await session.CurrentOperation;
        Assert.Null(session.State.Review);
        Assert.Contains("No selected tool", session.State.Error);
        fixture.BlockReads = true;
        session.Command(new("review", "codex"));
        Assert.True(session.State.Busy);
        Assert.Throws<InvalidOperationException>(() => session.Command(new("review", "claude")));
        session.Command(new("cancel")); await session.CurrentOperation;
        Assert.False(session.State.Busy);
        Assert.Null(session.State.Review);
        fixture.BlockReads = false;
        session.Command(new("review", "codex")); await session.CurrentOperation;
        Assert.NotNull(session.State.Review);
    }

    private sealed class Fixture : IDisposable
    {
        public const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        private const string Tree = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "estao-hub-session-" + Guid.NewGuid().ToString("N"));
        public List<string> Requests { get; } = [];
        public bool DenyAccess { get; set; }
        public bool BlockReads { get; set; }
        public Func<HttpRequestMessage, CancellationToken, Task>? BeforeRead { get; set; }
        public bool FailTree { get; set; }
        public bool IncludeExtraSkills { get; set; }
        public bool IncludeOpenCode { get; set; }
        public bool IncludeFileItems { get; set; }
        public bool LocalizedSkill { get; set; }
        public bool OmitManifest { get; set; }
        public bool InvalidManifest { get; set; }
        public string SampleId { get; set; } = "sample";
        public HarnessAdapter Adapter(string id) => new(id, Path.Combine(Directory, "home"), _ => null);
        public HarnessHubSession Session(string token = "fake-test-token", Func<CancellationToken, Task<IReadOnlyList<string>>>? candidates = null,
            Func<HubSkill, string?>? exportDestination = null) => new(Path.Combine(Directory, "config.json"),
            new HttpClient(new Handler(this)), id => IncludeOpenCode || id != "opencode", Adapter,
            candidates ?? (_ => Task.FromResult<IReadOnlyList<string>>([token])), exportDestination);

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.PathAndQuery;
            lock (Requests) Requests.Add(path);
            if (DenyAccess) return new(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
            if (FailTree && path.Contains("/git/trees/", StringComparison.Ordinal)) return new(HttpStatusCode.ServiceUnavailable) { Content = new StringContent("{}") };
            var skill = Encoding.UTF8.GetBytes("---\nname: sample\ndescription: Shared test skill\n" +
                (LocalizedSkill ? "descriptions:\n  es: Skill de prueba\n  fr: Compétence de test\n" : "") + "---\nFixture, never execute.\n");
            object CatalogItem(string id, string type, string source, string[]? dependencies = null) => new
            {
                id, type, path = source, categories = "core",
                dependencies = dependencies ?? []
            };
            var items = new List<object> { CatalogItem(SampleId, "skill", "skills/sample", IncludeExtraSkills ? ["base"] : []) };
            var extras = new Dictionary<string, byte[]>();
            if (IncludeExtraSkills)
                foreach (var id in new[] { "base", "unrelated" })
                {
                    extras["skills/" + id + "/SKILL.md"] = Encoding.UTF8.GetBytes($"---\nname: {id}\ndescription: Test skill\n---\n");
                    items.Add(CatalogItem(id, "skill", "skills/" + id));
                }
            if (IncludeFileItems)
            {
                extras["instructions/database.md"] = Encoding.UTF8.GetBytes("Project database guidance. Never execute.");
                extras["templates/example.json"] = Encoding.UTF8.GetBytes("{\"sample\":true}");
                items.Add(CatalogItem("database-instructions", "instruction", "instructions/database.md"));
                items.Add(CatalogItem("example-template", "file", "templates/example.json"));
            }
            extras["skills/sample/SKILL.md"] = skill;
            if (!OmitManifest)
                extras["hub-manifest.json"] = InvalidManifest ? Encoding.UTF8.GetBytes("{invalid-json") :
                    JsonSerializer.SerializeToUtf8Bytes(new
                    {
                        schemaVersion = 1, defaultLanguage = "en",
                        categories = new Dictionary<string, Dictionary<string, string>> { ["core"] = new() { ["en"] = "Core", ["es"] = "General" } },
                        items
                    });
            var entries = extras.Select(pair => new { path = pair.Key, sha = InstallerPaths.GitBlob(pair.Value), size = pair.Value.Length, mode = "100644", type = "blob" }).ToArray();
            var blob = extras.Values.FirstOrDefault(bytes => path.EndsWith("/" + InstallerPaths.GitBlob(bytes), StringComparison.Ordinal));
            object body = path.Contains("/contents/", StringComparison.Ordinal) ? Array.Empty<object>()
                : path.Contains("/commits/", StringComparison.Ordinal) ? new { sha = Commit, commit = new { tree = new { sha = Tree } } }
                : path.Contains("/git/trees/", StringComparison.Ordinal) ? new { truncated = false, tree = entries }
                : blob is not null ? new { encoding = "base64", content = Convert.ToBase64String(blob) }
                : new { full_name = GitHubRepositorySource.Repository };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) };
        }
        public void Dispose() { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true); }
        private sealed class Handler(Fixture fixture) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (fixture.BlockReads) await Task.Delay(Timeout.Infinite, ct);
                if (fixture.BeforeRead is { } beforeRead) await beforeRead(request, ct);
                ct.ThrowIfCancellationRequested();
                return fixture.Respond(request);
            }
        }
    }
}
