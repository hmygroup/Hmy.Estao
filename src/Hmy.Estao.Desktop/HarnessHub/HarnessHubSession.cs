using System.Diagnostics;
using System.Net;
using Hmy.Estao.App;
using Hmy.Estao.App.Services;
using Hmy.Estao.Core.Harnesses.Installer;
using Hmy.Estao.Core.Security;
using Hmy.Estao.Core.Models;

namespace Hmy.Estao.Desktop.HarnessHub;

// Owned by the Settings window, never by the idle tray. Tokens and downloaded
// payloads remain native; JS receives only a review and aggregate progress.
internal sealed class HarnessHubSession : IDisposable
{
    private readonly object _gate = new();
    private readonly HttpClient _client;
    private readonly HarnessGitHubAuthorization _authorization;
    private readonly HarnessInstallationService _installer;
    private readonly string _dataDirectory;
    private readonly Func<string, bool> _detected;
    private readonly Func<string, HarnessAdapter> _adapter;
    private readonly Func<CancellationToken, Task<IReadOnlyList<string>>> _candidates;
    private readonly Dictionary<string, (HarnessAdapter Adapter, PreparedSnapshot Snapshot, InstallPlan Plan)> _prepared = [];
    private CancellationTokenSource? _cancellation;
    private Task? _running;
    private string? _token;
    private IReadOnlyList<HubSkill> _localSkills = [];
    private IReadOnlyList<HubSkill> _librarySkills = [];
    private bool _disposed;
    private HubState _state = new(false, "idle", "Check your tools to get started.", false, "Not connected", [], null, [], null, null);

    public HarnessHubSession(string configPath, HttpClient? client = null, Func<string, bool>? detected = null,
        Func<string, HarnessAdapter>? adapter = null, Func<CancellationToken, Task<IReadOnlyList<string>>>? candidates = null)
    {
        _dataDirectory = Path.GetDirectoryName(Path.GetFullPath(configPath))!;
        _authorization = new(_dataDirectory);
        _installer = new(_dataDirectory);
        _client = client ?? GitHubRepositorySource.CreateClient();
        _detected = detected ?? HubTools.IsDetected;
        _adapter = adapter ?? (id => new HarnessAdapter(id));
        _candidates = candidates ?? _authorization.CandidatesAsync;
    }

    public HubState State { get { lock (_gate) return _state; } }
    internal Task CurrentOperation { get { lock (_gate) return _running ?? Task.CompletedTask; } }
    private void Update(Func<HubState, HubState> change) { lock (_gate) { if (!_disposed) _state = change(_state); } }
    private IProgress<string> Progress() => new InlineProgress(text => Update(s => s with { Progress = text, ProgressMessage = new("hub.working") }),
        message => Update(s => s with { ProgressMessage = message }));

    public void Command(HubCommand command)
    {
        if (command.Action == "cancel") { lock (_gate) _cancellation?.Cancel(); return; }
        if (command.Action == "open-repo") { Open("https://github.com/" + GitHubRepositorySource.Repository); return; }
        if (command.Action == "website") { Open(HubTools.Website(command.HarnessId ?? "")); return; }
        if (command.Action == "open-auth")
        {
            if (State.DeviceCode is null) throw new InvalidOperationException("Start GitHub authorization first.").WithUserMessage("signin.need_code");
            Open("https://github.com/login/device"); return;
        }
        if (command.Action == "copy-code")
        {
            if (State.DeviceCode is not { } code) throw new InvalidOperationException("No device code is available.").WithUserMessage("signin.need_code");
            Clipboard.SetText(code.Code); return;
        }

        if (State.Busy) throw new InvalidOperationException("A Hub operation is already in progress.").WithUserMessage("hub.busy");

        switch (command.Action)
        {
            case "check": Start("checking", CheckAsync); break;
            case "connect": Start("connecting", ConnectAsync); break;
            case "review":
                if (command.HarnessId is not null && !HarnessAdapter.Ids.Contains(command.HarnessId)) throw new ArgumentException("Unknown harness.").WithUserMessage("hub.unknown_tool");
                Start("preparing", ct => PrepareAsync(command.HarnessId, ct, command.SkillId)); break;
            case "install":
                var review = State.Review ?? throw new InvalidOperationException("Review the installation first.").WithUserMessage("hub.review_first");
                if (command.ReviewId != review.Id || command.Selection is null) throw new ArgumentException("The review has changed. Review again.").WithUserMessage("hub.review_changed");
                if (command.Selection.Values.Any(ids => ids is null)) throw new ArgumentException("Invalid skill selection.").WithUserMessage("hub.select_skill");
                if (command.Selection.Keys.Any(id => !review.Tools.Any(t => t.Id == id))) throw new ArgumentException("Unknown installation target.").WithUserMessage("hub.unknown_tool");
                var selection = command.Selection.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
                // Validate every selection before starting any writes.
                foreach (var (id, ids) in selection) HubReviewBuilder.Select(_prepared[id].Snapshot, ids);
                if (!selection.Values.Any(ids => ids.Length > 0)) throw new ArgumentException("Select at least one skill to install.").WithUserMessage("hub.select_skill");
                Start("installing", ct => InstallAsync(selection, ct)); break;
            case "recover":
                if (!HarnessAdapter.Ids.Contains(command.HarnessId ?? "")) throw new ArgumentException("Unknown harness.").WithUserMessage("hub.unknown_tool");
                Start("recovering", ct => RecoverAsync(command.HarnessId!, ct)); break;
            case "discard-review":
                if (State.Busy) throw new InvalidOperationException("Wait for the current operation or cancel it.").WithUserMessage("hub.busy");
                _prepared.Clear(); Update(s => s with { Review = null, Error = null }); break;
            default: throw new ArgumentException("Unknown Hub command.").WithUserMessage("settings.command_unknown");
        }
    }

    private void Start(string phase, Func<CancellationToken, Task> action)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_state.Busy) throw new InvalidOperationException("A Hub operation is already in progress.").WithUserMessage("hub.busy");
            _state = _state with { Busy = true, Phase = phase, CheckingAuthorization = phase is "checking" or "connecting" or "preparing",
                Progress = "Working…", Error = null, ErrorMessage = null, DeviceCode = null, ProgressMessage = new("hub.working") };
            _cancellation?.Dispose();
            _cancellation = new();
            var token = _cancellation.Token;
            _running = Task.Run(async () =>
            {
                try { await action(token).ConfigureAwait(false); }
                catch (OperationCanceledException)
                {
                    _prepared.Clear();
                    Update(s => s with { Review = null, Progress = "Cancelled. Any interrupted writes are recovered when safe.", ProgressMessage = new("hub.cancelled") });
                }
                catch (Exception exception)
                {
                    _prepared.Clear();
                    Update(s => s with { Review = null, Error = SafeError(exception), ErrorMessage = UserMessage.FromException(exception), Progress = "Could not complete the operation.", ProgressMessage = new("error.operation") });
                }
                finally { Update(s => s with { Busy = false, CheckingAuthorization = false, Phase = "idle", DeviceCode = null }); }
            });
        }
    }

    private async Task CheckAsync(CancellationToken ct)
    {
        _prepared.Clear();
        _librarySkills = [];
        Update(s => s with { Review = null, Results = [], LibraryCommit = null, LibraryError = null, LibraryLoaded = false,
            Progress = "Checking installed tools…", ProgressMessage = new("hub.check_tools") });
        await UpdateToolsAsync(ct);
        await UpdateLocalInventoryAsync(ct);
        if (await FindAuthorizationAsync(ct)) await UpdateRepositoryLibraryAsync(ct);
        Update(s => s with { Progress = "Tools checked. Review a tool or install all compatible skills.", ProgressMessage = new("hub.checked") });
    }

    private async Task UpdateToolsAsync(CancellationToken ct)
    {
        var tools = new List<HubTool>();
        foreach (var id in HarnessAdapter.Ids)
        {
            ct.ThrowIfCancellationRequested();
            var detected = _detected(id);
            try
            {
                var adapter = _adapter(id);
                var status = await _installer.GetInstallationStatusAsync(adapter);
                tools.Add(new(id, adapter.DisplayName, detected, status.ToString()));
            }
            catch (Exception exception) { tools.Add(new(id, Name(id), detected, "Error", SafeError(exception), UserMessage.FromException(exception))); }
        }
        Update(s => s with { Tools = tools });
    }

    private async Task<bool> FindAuthorizationAsync(CancellationToken ct)
    {
        Update(s => s with { CheckingAuthorization = true, Progress = "Checking access to the private GitHub repository…", ProgressMessage = new("hub.check_access") });
        var candidates = (await _candidates(ct)).ToList();
        if (_token is not null) candidates.Insert(0, _token);
        string? copilotToken = null;
        try { copilotToken = (await new SecureOAuthTokenStore(Path.Combine(_dataDirectory, "oauth-secrets.json")).ReadAsync("copilot", ct))?.AccessToken; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException or FormatException) { }
        foreach (var candidate in candidates.Distinct(StringComparer.Ordinal))
        {
            try
            {
                await new GitHubRepositorySource(_client).VerifyAccessAsync(candidate, ct);
                _token = candidate;
                Update(s => s with { Connected = true, AuthorizationChecked = true, CheckingAuthorization = false,
                    Connection = candidate == copilotToken ? "Connected using GitHub Copilot authorization" : "Connected to the private repository", ConnectionMessage = new("hub.connected") });
                return true;
            }
            catch (GitHubAccessException ex) when (ex.Status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound) { }
        }
        _token = null;
        Update(s => s with { Connected = false, AuthorizationChecked = true, CheckingAuthorization = false, Connection = copilotToken is not null
            ? "Copilot is signed in, but its token cannot read this repository. Connect GitHub separately."
            : "Connect GitHub with access to the private repository.", ConnectionMessage = new(copilotToken is not null ? "hub.copilot_no_access" : "hub.connect_required") });
        return false;
    }

    private async Task ConnectAsync(CancellationToken ct)
    {
        await UpdateToolsAsync(ct);
        await UpdateLocalInventoryAsync(ct);
        if (await FindAuthorizationAsync(ct)) { await UpdateRepositoryLibraryAsync(ct); return; }
        var authorization = await OAuthLoginService.StartHarnessDeviceFlowAsync(ct);
        Update(s => s with { DeviceCode = new(authorization.UserCode, authorization.VerificationUri),
            Progress = "Enter the code in GitHub to authorize access.", ProgressMessage = new("hub.authorize") });
        Open(authorization.VerificationUri);
        var token = await OAuthLoginService.CompleteHarnessDeviceFlowAsync(authorization, ct);
        await new GitHubRepositorySource(_client).VerifyAccessAsync(token, ct);
        await _authorization.SaveAsync(token, ct);
        _token = token;
        Update(s => s with { Connected = true, Connection = "Connected to the private repository", Progress = "GitHub connected.", ConnectionMessage = new("hub.connected"), ProgressMessage = new("hub.connected") });
        await UpdateRepositoryLibraryAsync(ct);
    }

    private async Task PrepareAsync(string? harnessId, CancellationToken ct, string? skillId = null)
    {
        _prepared.Clear();
        Update(s => s with { Review = null, Results = [] });
        await UpdateToolsAsync(ct);
        await UpdateLocalInventoryAsync(ct);
        if (!State.Tools.Any(t => t.Detected && (harnessId is null || t.Id == harnessId)))
            throw new InvalidOperationException("No selected tool is installed. Use Go to website, then Check again.").WithUserMessage("hub.no_tools");
        if (!await FindAuthorizationAsync(ct)) throw new InvalidOperationException(State.Connection).WithUserMessage(State.ConnectionMessage?.Code ?? "hub.connect_required");
        if (!State.LibraryLoaded) await UpdateRepositoryLibraryAsync(ct);
        var librarySkill = skillId is null ? null : State.Skills?.FirstOrDefault(s => s.Id == skillId && s.InLibrary);
        if (skillId is not null && librarySkill is not { Installable: true })
            throw new ArgumentException("Select a repository skill approved for installation.").WithUserMessage("hub.select_skill");
        var targets = State.Tools.Where(t => t.Detected && (harnessId is null || t.Id == harnessId) &&
            (librarySkill is null || librarySkill.CompatibleTools.Contains(t.Id, StringComparer.Ordinal))).ToArray();
        if (targets.Length == 0) throw new InvalidOperationException("No selected tool is installed. Use Go to website, then Check again.").WithUserMessage("hub.no_tools");
        var reviews = new List<HubToolReview>();
        var results = new List<HubResult>();
        string? commit = null;
        foreach (var target in targets)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var adapter = _adapter(target.Id);
                if (await _installer.GetInstallationStatusAsync(adapter) == HubOperationState.RecoveryRequired)
                    throw new InvalidOperationException("Recover the interrupted installation before reviewing this tool.").WithUserMessage("hub.recovery_required");
                var snapshot = await new GitHubRepositorySource(_client).PrepareAsync(_token!, adapter, Progress(), ct, commit ?? "main");
                commit ??= snapshot.Commit; // Every subsequent tool reads the same immutable commit.
                if (skillId is not null)
                {
                    var dependencies = snapshot.Dependencies ?? new Dictionary<string, IReadOnlyList<string>>();
                    if (!dependencies.ContainsKey(skillId)) throw new ArgumentException($"Skill '{skillId}' is not available for this tool.");
                    var closure = new HashSet<string>(StringComparer.Ordinal) { skillId };
                    void IncludeDependencies(string id)
                    {
                        foreach (var dependency in dependencies.GetValueOrDefault(id) ?? [])
                            if (closure.Add(dependency)) IncludeDependencies(dependency);
                    }
                    IncludeDependencies(skillId);
                    snapshot = HubReviewBuilder.Select(snapshot, closure);
                }
                var plan = await _installer.PlanAsync(adapter, snapshot, ct);
                _prepared[target.Id] = (adapter, snapshot, plan);
                reviews.Add(HubReviewBuilder.Build(adapter, snapshot, plan));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) { results.Add(new(target.Id, target.Name, "failed", 0, 0, 0, 0, SafeError(exception), UserMessage.FromException(exception))); }
        }
        if (harnessId is null)
            results.AddRange(State.Tools.Where(t => !t.Detected).Select(t => new HubResult(t.Id, t.Name, "skipped", 0, 0, 0, 0, "Tool not installed.", new("hub.not_installed"))));
        Update(s => s with { Review = reviews.Count == 0 ? null : new(Guid.NewGuid().ToString("N"), commit!, reviews),
            Results = results, Progress = reviews.Count == 0 ? "No installation could be prepared. Review the results below." : "Review the skills before installing. Nothing has been written yet.", ProgressMessage = new(reviews.Count == 0 ? "hub.prepare_failed" : "hub.review_ready") });
    }

    private async Task InstallAsync(Dictionary<string, string[]> selection, CancellationToken ct)
    {
        var results = State.Results.ToList();
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in State.Review!.Tools)
        {
            ct.ThrowIfCancellationRequested();
            if (!selection.TryGetValue(tool.Id, out var ids) || ids.Length == 0) continue;
            var prepared = _prepared[tool.Id];
            try
            {
                if (!_detected(tool.Id)) throw new InvalidOperationException("Tool is no longer installed. Check again.").WithUserMessage("hub.no_tools");
                var snapshot = HubReviewBuilder.Select(prepared.Snapshot, ids);
                // Refresh plans between tools because Codex/Copilot/OpenCode can
                // observe the same global skills. Only preceding successful writes
                // may invalidate an approved review; other changes need a new review.
                foreach (var file in prepared.Plan.Files.Where(f => ids.Contains(f.ArtifactId)))
                    if (!written.Contains(Path.GetFullPath(file.Path)) && InstallerPaths.FileHash(file.Path) != file.BeforeHash)
                        throw new IOException("Local files changed after review. Review this tool again.").WithUserMessage("hub.review_changed");
                var plan = await _installer.PlanAsync(prepared.Adapter, snapshot, ct);
                var result = await _installer.ApplyAsync(prepared.Adapter, plan, preserveConflicts: true, Progress(), ct);
                foreach (var file in plan.Files.Where(f => f.Action is InstallAction.Create or InstallAction.Update or InstallAction.Merge))
                    written.Add(Path.GetFullPath(file.Path));
                results.Add(new(tool.Id, tool.Name, result.Conflicts > 0 ? "partial" : "installed",
                    result.Created, result.Updated, result.Unchanged, result.Conflicts));
            }
            catch (OperationCanceledException) { results.Add(new(tool.Id, tool.Name, "cancelled", 0, 0, 0, 0, "Installation cancelled; safe recovery attempted.", new("hub.cancelled"))); Update(s => s with { Results = results.ToArray() }); throw; }
            catch (Exception exception) { results.Add(new(tool.Id, tool.Name, "failed", 0, 0, 0, 0, SafeError(exception), UserMessage.FromException(exception))); }
            Update(s => s with { Results = results.ToArray() });
        }
        _prepared.Clear();
        Update(s => s with { Review = null, Progress = "Finished. Local conflicts were preserved; downloaded scripts were not executed.", ProgressMessage = new("hub.finished") });
        await UpdateToolsAsync(ct);
        await UpdateLocalInventoryAsync(ct);
    }

    private Task UpdateLocalInventoryAsync(CancellationToken ct)
    {
        var errors = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        _localSkills = HubSkillInventory.Scan(State.Tools.Where(t => t.Detected).Select(t => t.Id), _adapter,
            (id, error) =>
            {
                if (!errors.TryGetValue(id, out var messages)) errors[id] = messages = [];
                if (messages.Count < 10) messages.Add(error);
                else if (messages.Count == 10) messages.Add("Additional discovery errors omitted.");
            }, ct);
        var skills = MergeSkills(_localSkills, _librarySkills);
        Update(s => s with { Skills = skills, Tools = s.Tools.Select(tool => errors.TryGetValue(tool.Id, out var messages)
            ? tool with { Status = tool.Status == "RecoveryRequired" ? tool.Status : "Error",
                Error = SafeError(new IOException(string.Join(" ", messages))), ErrorMessage = new("error.io") } : tool).ToArray() });
        return Task.CompletedTask;
    }

    private async Task UpdateRepositoryLibraryAsync(CancellationToken ct)
    {
        Update(s => s with { Progress = "Loading the HMY skill library…", ProgressMessage = new("hub.library_loading") });
        try
        {
            var library = await new GitHubRepositorySource(_client).DiscoverSkillsAsync(_token!, ct);
            _librarySkills = library.Skills.GroupBy(skill => skill.Id, StringComparer.OrdinalIgnoreCase).Select(group =>
            {
                var preferred = group.FirstOrDefault(skill => skill.Installable) ?? group.First();
                var sources = group.Select(skill => skill.Source).Distinct(StringComparer.Ordinal).ToArray();
                var issues = new[] { preferred.Issue, sources.Length > 1
                        ? "Multiple repository entries share this name. The approved mapping, if any, is used for installation." : null,
                    preferred.Installable ? null : "Not approved for automatic installation." }.Where(issue => issue is not null).ToArray();
                return new HubSkill(preferred.Id, preferred.Name, preferred.Description, true, preferred.Installable,
                    preferred.CompatibleHarnesses, [], string.Join("; ", sources), issues.Length == 0 ? null : string.Join(" ", issues));
            }).ToArray();
            Update(s => s with { LibraryCommit = library.Commit, LibraryLoaded = true, LibraryError = null,
                Skills = MergeSkills(_localSkills, _librarySkills) });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            Update(s => s with { LibraryCommit = null, LibraryLoaded = false, LibraryError = SafeError(exception) });
        }
    }

    private static IReadOnlyList<HubSkill> MergeSkills(IEnumerable<HubSkill> local, IEnumerable<HubSkill> library)
    {
        var merged = local.ToDictionary(skill => skill.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var item in library)
        {
            if (merged.TryGetValue(item.Id, out var existing))
            {
                var issues = new[] { existing.Issue, item.Issue }.Where(issue => !string.IsNullOrWhiteSpace(issue)).Distinct().ToArray();
                merged[item.Id] = item with { Occurrences = existing.Occurrences,
                    Issue = issues.Length == 0 ? null : string.Join(" ", issues) };
            }
            else merged[item.Id] = item;
        }
        return merged.Values.OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task RecoverAsync(string id, CancellationToken ct)
    {
        _prepared.Clear(); Update(s => s with { Review = null });
        var issues = await _installer.RecoverAsync(_adapter(id), ct);
        Update(s => s with { Progress = issues.Count == 0 ? "Recovery completed. You can review an installation now." : "Later local edits were preserved. Manual recovery is still required.", ProgressMessage = new(issues.Count == 0 ? "hub.recovered" : "hub.recovery_required"),
            Results = [new(id, Name(id), issues.Count == 0 ? "recovered" : "failed", 0, 0, 0, issues.Count,
                issues.Count == 0 ? null : string.Join(Environment.NewLine, issues), issues.Count == 0 ? null : new("hub.recovery_required"))] });
        await UpdateToolsAsync(ct);
        await UpdateLocalInventoryAsync(ct);
    }

    private string SafeError(Exception exception)
    {
        var message = exception is GitHubAccessException or InvalidDataException or IOException or InvalidOperationException or ArgumentException
            ? exception.Message : "The operation failed. Check network access and local profile permissions.";
        return _token is not null ? message.Replace(_token, "[redacted]", StringComparison.Ordinal) : message;
    }
    private static string Name(string id) => id switch { "codex" => "OpenAI Codex", "claude" => "Claude Code", "copilot" => "GitHub Copilot CLI", _ => "OpenCode" };
    private static void Open(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    private sealed class InlineProgress(Action<string> report, Action<UserMessage> message) : IUserMessageProgress
    {
        public void Report(string value) => report(value);
        public void Report(UserMessage value) => message(value);
    }

    public void Dispose()
    {
        Task? running;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _cancellation?.Cancel();
            running = _running;
        }
        // Do not dispose the HTTP client or payloads beneath an active rollback.
        if (running is not null) _ = running.ContinueWith(_ => Cleanup(), TaskScheduler.Default);
        else Cleanup();
    }
    private void Cleanup()
    {
        _prepared.Clear(); _token = null; _client.Dispose(); _cancellation?.Dispose();
        lock (_gate) _state = _state with { Review = null, Results = [], DeviceCode = null };
    }
}
