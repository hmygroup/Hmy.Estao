using System.Net;
using System.Text;
using System.Text.Json;
using Hmy.Estao.Core.Harnesses.Installer;
using Hmy.Estao.Core.Security;
using Hmy.Estao.Desktop.HarnessHub;

namespace Hmy.Estao.Desktop.Tests;

public sealed class HarnessHubSessionTests
{
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
        session.Command(new("review")); await session.CurrentOperation;
        var review = Assert.IsType<HubReview>(session.State.Review);
        Assert.Equal(3, review.Tools.Count);
        Assert.Equal("conflict", Assert.Single(review.Tools.Single(t => t.Id == "claude").Artifacts).Status);
        Assert.Equal("new", Assert.Single(review.Tools.Single(t => t.Id == "codex").Artifacts).Status);
        Assert.Contains(session.State.Results, r => r.Id == "opencode" && r.Outcome == "skipped");
        Assert.Single(fixture.Requests, r => r.EndsWith("/commits/main", StringComparison.Ordinal));
        Assert.Equal(2, fixture.Requests.Count(r => r.EndsWith("/commits/" + Fixture.Commit, StringComparison.Ordinal)));
        Assert.False(File.Exists(Path.Combine(fixture.Adapter("codex").SkillsRoot, "sample", "SKILL.md")));

        session.Command(new("install", ReviewId: review.Id, Selection: review.Tools.ToDictionary(t => t.Id, _ => new[] { "sample" })));
        await session.CurrentOperation;
        Assert.Null(session.State.Review);
        Assert.Contains(session.State.Results, r => r.Id == "codex" && r.Outcome == "installed" && r.Created == 1);
        Assert.Contains(session.State.Results, r => r.Id == "claude" && r.Outcome == "partial" && r.Conflicts == 1);
        Assert.Contains(session.State.Results, r => r.Id == "copilot" && r.Outcome == "installed" && r.Created == 0 && r.Unchanged == 1);
        Assert.Equal("Personal skill. Must not be overwritten.", await File.ReadAllTextAsync(existing));
        Assert.False(File.Exists(Path.Combine(fixture.Adapter("copilot").SkillsRoot, "sample", "SKILL.md")));
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
        public HarnessAdapter Adapter(string id) => new(id, Path.Combine(Directory, "home"), _ => null);
        public HarnessHubSession Session(string token = "fake-test-token", Func<CancellationToken, Task<IReadOnlyList<string>>>? candidates = null) => new(Path.Combine(Directory, "config.json"),
            new HttpClient(new Handler(this)), id => id != "opencode", Adapter,
            candidates ?? (_ => Task.FromResult<IReadOnlyList<string>>([token])));

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            var path = request.RequestUri!.PathAndQuery;
            Requests.Add(path);
            if (DenyAccess) return new(HttpStatusCode.Forbidden) { Content = new StringContent("{}") };
            var skill = Encoding.UTF8.GetBytes("---\nname: sample\ndescription: Shared test skill\n---\nFixture, never execute.\n");
            var manifest = JsonSerializer.SerializeToUtf8Bytes(new SourceManifest
            {
                Artifacts = [new() { Id = "sample", Source = "skills/sample", Harnesses = ["codex", "claude", "copilot", "opencode"] }]
            }, InstallerJson.Options);
            var hash = InstallerPaths.GitBlob(skill);
            var manifestHash = InstallerPaths.GitBlob(manifest);
            object body = path.Contains("/contents/", StringComparison.Ordinal) ? Array.Empty<object>()
                : path.Contains("/commits/", StringComparison.Ordinal) ? new { sha = Commit, commit = new { tree = new { sha = Tree } } }
                : path.Contains("/git/trees/", StringComparison.Ordinal) ? new { truncated = false, tree = new[] {
                    new { path = "skills/sample/SKILL.md", sha = hash, size = skill.Length, mode = "100644", type = "blob" },
                    new { path = "estao-harness.json", sha = manifestHash, size = manifest.Length, mode = "100644", type = "blob" } } }
                : path.EndsWith("/" + hash, StringComparison.Ordinal) ? new { encoding = "base64", content = Convert.ToBase64String(skill) }
                : path.EndsWith("/" + manifestHash, StringComparison.Ordinal) ? new { encoding = "base64", content = Convert.ToBase64String(manifest) }
                : new { full_name = GitHubRepositorySource.Repository };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body)) };
        }
        public void Dispose() { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true); }
        private sealed class Handler(Fixture fixture) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (fixture.BlockReads) await Task.Delay(Timeout.Infinite, ct);
                ct.ThrowIfCancellationRequested();
                return fixture.Respond(request);
            }
        }
    }
}
