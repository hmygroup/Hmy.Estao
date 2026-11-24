using System.Net;
using Hmy.Estao.Core.Models;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using YamlDotNet.Core;

namespace Hmy.Estao.Core.Harnesses.Installer;

public sealed class GitHubAccessException(string message, HttpStatusCode status) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

public sealed class GitHubRepositorySource
{
    private readonly HttpClient _client;
    private readonly string _localManifestPath;

    public GitHubRepositorySource(HttpClient client) : this(client, Path.Combine(AppContext.BaseDirectory, "hub-manifest.json")) { }

    internal GitHubRepositorySource(HttpClient client, string localManifestPath)
    {
        _client = client;
        _localManifestPath = localManifestPath;
    }

    private const int DownloadConcurrency = 6;
    private const long BlobCacheLimit = 32 * 1024 * 1024;
    private readonly SemaphoreSlim _revisionGate = new(1);
    private readonly SemaphoreSlim _downloads = new(DownloadConcurrency);
    private readonly Dictionary<(string Token, string Sha), Revision> _revisions = new();
    private readonly Dictionary<(string Token, string Sha), byte[]> _blobs = new();
    private long _blobCacheSize;

    private sealed record Revision(string Commit, string Tree, IReadOnlyList<SourceEntry> Entries);

    public const string Repository = "hmygroup/hmy-prompt-engineer";
    public static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(45) };

    public async Task VerifyAccessAsync(string token, CancellationToken ct = default)
    {
        using var response = await GetAsync($"repos/{Repository}/contents/", token, ct).ConfigureAwait(false);
        if (response.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid repository content response.");
    }

    public Task<RepositorySkillLibrary> DiscoverSkillsAsync(string token, CancellationToken cancellationToken = default) =>
        DiscoverSkillsAsync(token, cancellationToken, accessVerified: false);

    internal async Task<RepositorySkillLibrary> DiscoverSkillsAsync(string token, CancellationToken cancellationToken, bool accessVerified)
    {
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.CancelAfter(TimeSpan.FromMinutes(3));
        var ct = operation.Token;
        if (!accessVerified) await VerifyIdentityAsync(token, ct).ConfigureAwait(false);
        var (commit, _, entries) = await ReadRevisionAsync(token, "main", ct).ConfigureAwait(false);
        ValidatedCatalog? catalog = null;
        string? catalogError = null;
        try
        {
            catalog = await ReadSkillCatalogAsync(entries, token, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException)
        { catalogError = ex.Message; }
        var curatedByPath = catalog?.Catalog.Items.Where(i => i.Type == "skill")
            .ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, HubCatalogItem>(StringComparer.OrdinalIgnoreCase);
        var skills = new List<RepositorySkill>();
        var candidates = entries.Where(IsSkillEntry).ToArray();
        var skillIds = SkillIds(candidates, catalog);
        var instructionPaths = (catalog?.Catalog.Items ?? []).Where(item => item.Type == "instruction")
            .Select(item => item.Path).ToHashSet(StringComparer.Ordinal);
        var metadata = await DownloadAsync(candidates.Concat(entries.Where(entry => instructionPaths.Contains(entry.Path)))
            .DistinctBy(entry => entry.Path).ToArray(), token, ct, (entry, error) => !IsSkillEntry(entry) && IsMetadataError(error)).ConfigureAwait(false);
        var instructions = metadata.Where(pair => instructionPaths.Contains(pair.Entry.Path))
            .ToDictionary(pair => pair.Entry.Path, pair => pair.Bytes, StringComparer.Ordinal);
        foreach (var (entry, bytes, _) in metadata.Where(pair => IsSkillEntry(pair.Entry)))
        {
            ct.ThrowIfCancellationRequested();
            var source = SkillSource(entry);
            var folder = SkillFolder(source);
            var id = skillIds[source];
            string name = id, description;
            IReadOnlyDictionary<string, string> descriptions = new Dictionary<string, string>();
            var metadataValid = false;
            string? issue = null;
            try
            {
                var skillMetadata = HarnessAdapter.ReadSkillMetadata(bytes!);
                (name, description) = (skillMetadata.Name, skillMetadata.Description);
                descriptions = skillMetadata.Descriptions;
                metadataValid = source != "." && name == folder && !string.IsNullOrWhiteSpace(description) && description.Length <= 1024;
                if (!metadataValid) issue = source == "." ? "Root SKILL.md has no installable skill folder. Curate it as a file to export it."
                    : "Invalid skill identity or description in repository metadata.";
            }
            catch (Exception ex) when (ex is InvalidDataException or DecoderFallbackException or ArgumentException or YamlException)
            { description = ""; issue = "Could not parse repository skill metadata."; }
            if (string.IsNullOrWhiteSpace(name)) name = id;
            HubCatalogItem? item = null;
            var curated = catalog is not null && curatedByPath.TryGetValue(source, out item);
            if (!curated) item = null;
            var dependencyMissing = curated && catalog!.UnavailableSkills.Contains(item!.Id);
            if (dependencyMissing) issue = "A required catalog skill or transitive dependency is missing from the repository.";
            skills.Add(new RepositorySkill(curated ? item!.Id : id, name, description, source,
                metadataValid ? HarnessAdapter.Ids : [], metadataValid && !dependencyMissing, issue, curated, "skill",
                descriptions, item is null ? [] : catalog!.ItemCategories[item.Id], catalog?.Catalog.DefaultLanguage ?? "en"));
        }
        if (catalog is not null)
            foreach (var item in catalog.Catalog.Items.Where(i => i.Type != "skill"))
            {
                SkillMetadataInfo? documentMetadata = null;
                string? issue = null;
                if (item.Type == "instruction")
                {
                    try
                    {
                        var document = InstructionDocument.Read(instructions[item.Path] ?? throw new InvalidDataException("Invalid instruction blob."));
                        documentMetadata = document.Metadata;
                        if (string.IsNullOrWhiteSpace(document.Body)) issue = "The instruction file has no instruction body.";
                    }
                    catch (Exception ex) when (ex is InvalidDataException or DecoderFallbackException or ArgumentException or YamlException)
                    { issue = "Could not parse instruction metadata or content."; }
                }
                skills.Add(new RepositorySkill(item.Id, string.IsNullOrWhiteSpace(documentMetadata?.Name) ? item.Id : documentMetadata.Name,
                    item.Type is "mcp" or "cli" ? item.Description : documentMetadata?.Description ?? "", item.Path,
                    item.Type is "instruction" or "mcp" ? HarnessAdapter.Ids : [], item.Type is "instruction" or "mcp" or "cli" && issue is null,
                    issue, true, item.Type, documentMetadata?.Descriptions, catalog.ItemCategories[item.Id], catalog.Catalog.DefaultLanguage));
            }
        return new RepositorySkillLibrary(commit, skills, catalog?.Categories, catalog?.Catalog.DefaultLanguage ?? "en", catalogError);
    }

    public async Task<PreparedSnapshot> PrepareAsync(string token, HarnessAdapter adapter,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default, string reference = "main", string? sourcePath = null,
        IReadOnlyCollection<string>? catalogSkillIds = null) =>
        (await PrepareSkillsAsync(token, progress, cancellationToken, reference, sourcePath, catalogSkillIds, accessVerified: false)
            .ConfigureAwait(false)).ForAdapter(adapter, cancellationToken);

    internal sealed record DownloadedSkill(string Id, SourceArtifact Artifact, (SourceEntry Entry, byte[] Bytes)[] Payloads);

    internal sealed record DownloadedSkills(string Commit, string Tree, IReadOnlyList<DownloadedSkill> Skills,
        IReadOnlyList<CoverageItem> Coverage, IReadOnlyDictionary<string, IReadOnlyList<string>> Dependencies,
        IReadOnlyList<PreparedArtifact> Artifacts, IReadOnlyDictionary<string, Exception> Failures)
    {
        public PreparedSnapshot ForAdapter(HarnessAdapter adapter, CancellationToken ct)
        {
            var files = new List<PreparedFile>();
            foreach (var skill in Skills)
            {
                ct.ThrowIfCancellationRequested();
                var payloads = skill.Payloads.Select(pair => (pair.Entry, pair.Bytes.ToArray())).ToArray();
                files.AddRange(adapter.Prepare(skill.Artifact, payloads).Select(file => file with { ArtifactId = skill.Id }));
            }
            return new(Commit, Tree, "2", files, Coverage, Dependencies, Artifacts);
        }
    }

    internal async Task<DownloadedSkills> PrepareSkillsAsync(string token, IProgress<string>? progress,
        CancellationToken cancellationToken, string reference, string? sourcePath, IReadOnlyCollection<string>? catalogSkillIds, bool accessVerified,
        bool continueOnSkillFailure = false)
    {
        ValidateReference(reference);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.CancelAfter(TimeSpan.FromMinutes(8));
        var ct = operation.Token;
        if (!accessVerified)
        {
            progress?.ReportMessage(new("hub.check_access"), "Comprobando acceso a GitHub…");
            await VerifyIdentityAsync(token, ct).ConfigureAwait(false);
        }
        var (commit, tree, entries) = await ReadRevisionAsync(token, reference, ct).ConfigureAwait(false);
        ValidatedCatalog? catalog = null;
        try { catalog = await ReadSkillCatalogAsync(entries, token, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException) { }
        var coverage = new List<CoverageItem>();
        var downloaded = new List<DownloadedSkill>();
        var artifacts = new List<PreparedArtifact>();
        var selected = new List<HubCatalogItem>();
        var failures = new Dictionary<string, Exception>(StringComparer.OrdinalIgnoreCase);
        if (catalogSkillIds is not null)
        {
            foreach (var id in catalogSkillIds)
            {
                if (catalog is null || !catalog.ById.TryGetValue(id, out var item) || item.Type != "skill" || catalog.UnavailableSkills.Contains(id))
                {
                    var error = new InvalidDataException("A selected skill or its required dependencies are no longer available.");
                    if (!continueOnSkillFailure) throw error;
                    failures[id] = error;
                    continue;
                }
                AddDependencies(item, catalog, selected);
            }
        }
        else if (sourcePath is null) selected.AddRange(catalog?.Catalog.Items.Where(i => i.Type == "skill" && !catalog.UnavailableSkills.Contains(i.Id)) ?? []);
        else
        {
            InstallerPaths.Relative(sourcePath);
            var entry = entries.SingleOrDefault(e => e.Path == sourcePath + "/SKILL.md");
            if (entry is null) throw new InvalidDataException("Requested source is not a repository skill.");
            var declared = catalog?.Catalog.Items.SingleOrDefault(i => i.Type == "skill" && i.Path == sourcePath);
            if (declared is not null && catalog!.UnavailableSkills.Contains(declared.Id))
                throw new InvalidDataException("A required catalog skill or transitive dependency is missing from the repository.");
            if (declared is not null) AddDependencies(declared, catalog!, selected);
            else selected.Add(new HubCatalogItem { Id = SkillIds(entries.Where(IsSkillEntry), catalog)[sourcePath], Type = "skill", Path = sourcePath });
        }
        var skillMetadata = await DownloadAsync(selected.Select(item =>
            entries.Single(entry => entry.Path == item.Path + "/SKILL.md")).DistinctBy(entry => entry.Path).ToArray(),
            token, ct, (_, error) => IsMetadataError(error) || (continueOnSkillFailure && IsItemReadError(error))).ConfigureAwait(false);
        var metadataByPath = skillMetadata.ToDictionary(pair => pair.Entry.Path, StringComparer.Ordinal);
        var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in selected)
        {
            try
            {
                var (_, bytes, error) = metadataByPath[item.Path + "/SKILL.md"];
                if (error is not null)
                {
                    failures[item.Id] = error;
                    continue;
                }
                var (name, description) = HarnessAdapter.SkillMetadata(bytes!);
                if (name != SkillFolder(item.Path) || string.IsNullOrWhiteSpace(description) || description.Length > 1024)
                    throw new InvalidDataException("Invalid skill identity or description in repository metadata.");
                descriptions[item.Id] = description;
            }
            catch (Exception ex) when (IsMetadataError(ex))
            { failures[item.Id] = ex; }
        }
        if (sourcePath is not null && failures.Count > 0)
            throw new InvalidDataException("Requested repository skill has invalid metadata.");
        RemoveFailedSkills();
        if (!continueOnSkillFailure && catalogSkillIds?.Any(id => failures.ContainsKey(id)) == true)
            throw new InvalidDataException("A selected skill or its required dependencies have invalid metadata.");
        var membersById = selected.ToDictionary(item => item.Id,
            item => entries.Where(entry => entry.Path.StartsWith(item.Path + "/", StringComparison.Ordinal)).ToArray());
        var missingPayloads = membersById.Values.SelectMany(members => members)
            .Where(entry => !metadataByPath.ContainsKey(entry.Path)).DistinctBy(entry => entry.Path).ToArray();
        foreach (var item in selected)
            progress?.ReportMessage(new("hub.downloading", item.Id), $"Descargando {item.Id}…");
        foreach (var payload in await DownloadAsync(missingPayloads, token, ct,
            (_, error) => continueOnSkillFailure && IsItemReadError(error)).ConfigureAwait(false))
            metadataByPath.Add(payload.Entry.Path, payload);
        foreach (var item in selected)
        {
            var error = membersById[item.Id].Select(entry => metadataByPath[entry.Path].Error).FirstOrDefault(error => error is not null);
            if (error is not null) failures[item.Id] = error;
        }
        RemoveFailedSkills();
        if (continueOnSkillFailure && catalog is not null && catalogSkillIds is not null && failures.Count > 0)
        {
            // Keep only the closure of selections that can still be installed.
            selected.Clear();
            foreach (var id in catalogSkillIds.Where(id => !failures.ContainsKey(id)))
                AddDependencies(catalog.ById[id], catalog, selected);
        }
        foreach (var item in selected)
        {
            ct.ThrowIfCancellationRequested();
            var source = item.Path;
            var artifact = new SourceArtifact { Id = SkillFolder(source), Source = source, Category = "skill" };
            var members = membersById[item.Id];
            var payloads = members.Select(entry => (entry, metadataByPath[entry.Path].Bytes!)).ToArray();
            downloaded.Add(new(item.Id, artifact, payloads));
            artifacts.Add(new(item.Id, "skill", source, descriptions[item.Id], []));
            coverage.Add(new(source, "installable", "Formato nativo; no se ejecutan scripts ni se instalan dependencias.", members.Length));
        }
        var dependencyMap = selected.ToDictionary(i => i.Id, i => (IReadOnlyList<string>)(catalog?.ById.GetValueOrDefault(i.Id)?.Dependencies ?? []), StringComparer.OrdinalIgnoreCase);
        return new(commit, tree, downloaded, coverage, dependencyMap, artifacts, failures);

        void RemoveFailedSkills()
        {
            while (selected.RemoveAll(item =>
            {
                if (failures.ContainsKey(item.Id)) return true;
                if (!item.Dependencies.Any(failures.ContainsKey)) return false;
                failures[item.Id] = new InvalidDataException("A required skill could not be prepared.");
                return true;
            }) > 0) { }
        }
    }

    public Task<byte[]> ReadCatalogFileAsync(string token, string sourcePath, string reference, CancellationToken cancellationToken = default) =>
        ReadCatalogFileAsync(token, sourcePath, reference, cancellationToken, accessVerified: false);

    internal async Task<byte[]> ReadCatalogFileAsync(string token, string sourcePath, string reference, CancellationToken cancellationToken, bool accessVerified)
    {
        InstallerPaths.Relative(sourcePath);
        ValidateReference(reference);
        if (!accessVerified) await VerifyIdentityAsync(token, cancellationToken).ConfigureAwait(false);
        var (_, _, entries) = await ReadRevisionAsync(token, reference, cancellationToken).ConfigureAwait(false);
        var manifest = entries.SingleOrDefault(e => e.Path == "hub-manifest.json") ?? throw new InvalidDataException("hub-manifest.json is missing.");
        var catalog = HubCatalogContract.Read(await BlobAsync(manifest, token, cancellationToken).ConfigureAwait(false), entries);
        var item = catalog.Catalog.Items.SingleOrDefault(i => i.Path == sourcePath && i.Type is "instruction" or "file")
            ?? throw new InvalidDataException("Only explicitly curated instruction and file items can be exported.");
        var source = entries.Single(e => e.Path == item.Path);
        return (await BlobAsync(source, token, cancellationToken).ConfigureAwait(false)).ToArray();
    }

    private async Task<ValidatedCatalog> ReadSkillCatalogAsync(IReadOnlyList<SourceEntry> entries, string token, CancellationToken ct)
    {
        if (File.Exists(_localManifestPath))
            return HubCatalogContract.Read(await File.ReadAllBytesAsync(_localManifestPath, ct).ConfigureAwait(false), entries);

        var manifest = entries.SingleOrDefault(entry => entry.Path == "hub-manifest.json")
            ?? throw new InvalidDataException("hub-manifest.json is missing.");
        return HubCatalogContract.Read(await BlobAsync(manifest, token, ct).ConfigureAwait(false), entries);
    }

    private static void AddDependencies(HubCatalogItem item, ValidatedCatalog catalog, List<HubCatalogItem> selected)
    {
        if (selected.Any(existing => existing.Id == item.Id)) return;
        foreach (var id in item.Dependencies) AddDependencies(catalog.ById[id], catalog, selected);
        selected.Add(item);
    }

    private static bool IsSkillEntry(SourceEntry entry) => entry.Path == "SKILL.md" || entry.Path.EndsWith("/SKILL.md", StringComparison.Ordinal);
    private static string SkillSource(SourceEntry entry) => entry.Path == "SKILL.md" ? "." : entry.Path[..^"/SKILL.md".Length];
    private static string SkillFolder(string source) => source == "." ? "repository-skill" : source[(source.LastIndexOf('/') + 1)..];

    // Discovery and preparation must agree on identity for duplicate names and catalog-ID collisions.
    private static IReadOnlyDictionary<string, string> SkillIds(IEnumerable<SourceEntry> entries, ValidatedCatalog? catalog)
    {
        var sources = entries.Select(SkillSource).Order(StringComparer.Ordinal).ToArray();
        var counts = sources.GroupBy(SkillFolder, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        var used = (catalog?.ById.Keys ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var curated = catalog?.Catalog.Items.Where(item => item.Type == "skill")
            .ToDictionary(item => item.Path, item => item.Id, StringComparer.Ordinal) ?? new Dictionary<string, string>();
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var source in sources)
        {
            if (curated.TryGetValue(source, out var declared)) { ids[source] = declared; continue; }
            var folder = SkillFolder(source);
            var id = counts[folder] == 1 && !used.Contains(folder) ? folder
                : folder + "-" + InstallerPaths.GitBlob(Encoding.UTF8.GetBytes(source));
            while (!used.Add(id)) id += "-other";
            ids.Add(source, id);
        }
        return ids;
    }

    private static List<SourceEntry> ReadTreeEntries(JsonElement tree)
    {
        var entries = new List<SourceEntry>();
        foreach (var item in tree.EnumerateArray())
        {
            if (item.GetProperty("type").GetString() == "tree") continue;
            var mode = item.GetProperty("mode").GetString()!;
            if (mode is not ("100644" or "100755")) throw new InvalidDataException("Source contains a symbolic link or submodule.");
            entries.Add(new SourceEntry(item.GetProperty("path").GetString()!, item.GetProperty("sha").GetString()!,
                item.TryGetProperty("size", out var size) ? size.GetInt64() : -1, mode));
        }
        if (entries.Count > 5000 || entries.Any(e => e.Size is < 0 or > 4 * 1024 * 1024) || entries.Sum(e => e.Size) > 50L * 1024 * 1024)
            throw new InvalidDataException("Source snapshot exceeds safety limits.");
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            InstallerPaths.Relative(entry.Path);
            if (!paths.Add(entry.Path) || !SourceRevision.Sha().IsMatch(entry.Sha))
                throw new InvalidDataException("Unsafe or duplicate source entry.");
        }
        return entries;
    }

    private async Task VerifyIdentityAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new GitHubAccessException("Conecta GitHub para leer la biblioteca privada.", HttpStatusCode.Unauthorized);
        using var access = await GetAsync($"repos/{Repository}", token, ct).ConfigureAwait(false);
        if (access.RootElement.GetProperty("full_name").GetString() != Repository)
            throw new InvalidDataException("Unexpected source repository identity.");
    }

    private static void ValidateReference(string reference)
    {
        if (reference != "main" && !SourceRevision.Sha().IsMatch(reference))
            throw new InvalidDataException("Invalid source reference.");
    }

    private async Task<Revision> ReadRevisionAsync(string token, string reference, CancellationToken ct)
    {
        await _revisionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (reference != "main" && _revisions.TryGetValue((token, reference), out var cached)) return cached;
            using var revision = await GetAsync($"repos/{Repository}/commits/{reference}", token, ct).ConfigureAwait(false);
            var commit = revision.RootElement.GetProperty("sha").GetString()!;
            var tree = revision.RootElement.GetProperty("commit").GetProperty("tree").GetProperty("sha").GetString()!;
            if (!SourceRevision.Sha().IsMatch(commit) || !SourceRevision.Sha().IsMatch(tree))
                throw new InvalidDataException("Invalid source revision.");
            if (reference != "main" && commit != reference)
                throw new InvalidDataException("The source revision no longer matches the reviewed snapshot.");
            if (_revisions.TryGetValue((token, commit), out cached))
            {
                if (cached.Tree != tree) throw new InvalidDataException("GitHub returned a different tree for the same commit.");
                return cached;
            }
            using var listing = await GetAsync($"repos/{Repository}/git/trees/{tree}?recursive=1", token, ct).ConfigureAwait(false);
            if (listing.RootElement.GetProperty("truncated").GetBoolean())
                throw new InvalidDataException("GitHub returned an incomplete source tree.");
            var result = new Revision(commit, tree, ReadTreeEntries(listing.RootElement.GetProperty("tree")));
            if (_revisions.Count >= 4) _revisions.Remove(_revisions.Keys.First());
            _revisions[(token, commit)] = result;
            return result;
        }
        finally { _revisionGate.Release(); }
    }

    private static bool IsMetadataError(Exception error) =>
        error is InvalidDataException or ArgumentException or JsonException or YamlException;

    private static bool IsItemReadError(Exception error) =>
        IsMetadataError(error) || error is FormatException or HttpRequestException or IOException or TimeoutException ||
        error is GitHubAccessException access &&
            (access.Status == HttpStatusCode.NotFound ||
                ((int)access.Status is >= 500 and < 600 && UserMessage.FromException(access).Code != "hub.rate_limited"));

    private async Task<(SourceEntry Entry, byte[]? Bytes, Exception? Error)[]> DownloadAsync(SourceEntry[] entries, string token,
        CancellationToken ct, Func<SourceEntry, Exception, bool> tolerateError)
    {
        var results = new (SourceEntry Entry, byte[]? Bytes, Exception? Error)[entries.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, entries.Length),
            new ParallelOptions { MaxDegreeOfParallelism = DownloadConcurrency, CancellationToken = ct }, async (index, cancellation) =>
            {
                var entry = entries[index];
                try { results[index] = (entry, await BlobAsync(entry, token, cancellation).ConfigureAwait(false), null); }
                catch (Exception ex) when (tolerateError(entry, ex))
                { results[index] = (entry, null, ex); }
            }).ConfigureAwait(false);
        return results;
    }

    private async Task<byte[]> BlobAsync(SourceEntry entry, string token, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (entry.Size is < 0 or > 4 * 1024 * 1024 || !SourceRevision.Sha().IsMatch(entry.Sha)) throw new InvalidDataException("Unsafe source blob.");
        var key = (token, entry.Sha);
        lock (_blobs)
            if (_blobs.TryGetValue(key, out var cached) && cached.LongLength == entry.Size) return cached;
        await _downloads.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_blobs)
                if (_blobs.TryGetValue(key, out var cached) && cached.LongLength == entry.Size) return cached;
            using var document = await GetAsync($"repos/{Repository}/git/blobs/{entry.Sha}", token, ct).ConfigureAwait(false);
            if (document.RootElement.GetProperty("encoding").GetString() != "base64") throw new InvalidDataException("Unsupported GitHub encoding.");
            var bytes = Convert.FromBase64String(document.RootElement.GetProperty("content").GetString()!);
            if (bytes.LongLength != entry.Size || InstallerPaths.GitBlob(bytes) != entry.Sha) throw new InvalidDataException("Source blob integrity check failed.");
            lock (_blobs)
            {
                if (_blobCacheSize + bytes.Length > BlobCacheLimit || _blobs.Count >= 2048)
                {
                    _blobs.Clear();
                    _blobCacheSize = 0;
                }
                if (_blobs.TryAdd(key, bytes)) _blobCacheSize += bytes.Length;
            }
            return bytes;
        }
        finally { _downloads.Release(); }
    }

    private async Task<JsonDocument> GetAsync(string path, string token, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/" + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
            request.Headers.UserAgent.ParseAdd("Hmy.Estao-HarnessInstaller/1.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            HttpResponseMessage response;
            try { response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            { throw new TimeoutException("GitHub no respondió dentro del tiempo permitido. Reintenta la operación.").WithUserMessage("error.network"); }
            catch (HttpRequestException) when (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt + 1), ct).ConfigureAwait(false);
                continue;
            }
            using (response)
            {
                var rateLimited = response.StatusCode == HttpStatusCode.TooManyRequests ||
                    (response.StatusCode == HttpStatusCode.Forbidden && response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.Contains("0"));
                if (attempt < 2 && ((int)response.StatusCode >= 500 || rateLimited))
                {
                    var seconds = response.Headers.RetryAfter?.Delta?.TotalSeconds ??
                        (response.Headers.RetryAfter?.Date is { } retryAt ? (retryAt - DateTimeOffset.UtcNow).TotalSeconds : attempt + 1);
                    if (rateLimited && response.Headers.RetryAfter is null && response.Headers.TryGetValues("X-RateLimit-Reset", out var reset) &&
                        long.TryParse(reset.FirstOrDefault(), out var epoch)) seconds = Math.Max(seconds, epoch - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    if (seconds > 15) throw new GitHubAccessException("Límite de GitHub alcanzado. Reintenta más tarde.", response.StatusCode).WithUserMessage("hub.rate_limited");
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, seconds)), ct).ConfigureAwait(false);
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw new GitHubAccessException(response.StatusCode switch
                    {
                        HttpStatusCode.Unauthorized => "La autorización GitHub ha caducado o no es válida. Vuelve a conectar.",
                        HttpStatusCode.Forbidden => "GitHub deniega el acceso. Revisa permisos, SSO y límites de la organización.",
                        HttpStatusCode.NotFound => "Biblioteca privada no accesible: comprueba cuenta y autorización del repositorio.",
                        _ => $"GitHub no pudo completar la lectura (HTTP {(int)response.StatusCode})."
                    }, response.StatusCode).WithUserMessage(response.StatusCode switch
                    {
                        HttpStatusCode.Unauthorized => "hub.auth_expired",
                        HttpStatusCode.Forbidden => "hub.access_denied",
                        HttpStatusCode.NotFound => "hub.repo_missing",
                        _ => "hub.read_failed"
                    }, (int)response.StatusCode);
                if (response.Content.Headers.ContentLength > 8 * 1024 * 1024) throw new InvalidDataException("GitHub response exceeds the safe limit.");
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                var chunk = new byte[16384];
                int count;
                while ((count = await stream.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + count > 8 * 1024 * 1024) throw new InvalidDataException("GitHub response exceeds the safe limit.");
                    buffer.Write(chunk, 0, count);
                }
                var bytes = buffer.ToArray();
                return JsonDocument.Parse(bytes);
            }
        }
    }
}
