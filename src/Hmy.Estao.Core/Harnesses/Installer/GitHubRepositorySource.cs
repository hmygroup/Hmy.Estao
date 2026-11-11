using System.Net;
using System.Collections.Concurrent;
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

public sealed class GitHubRepositorySource(HttpClient client)
{
    // Source instances are operation-scoped. Only immutable Git objects are cached,
    // separately for each credential; access checks and mutable refs stay live.
    private readonly ConcurrentDictionary<(string Token, string Path), byte[]> _immutableResponses = new();
    public const string Repository = "hmygroup/hmy-prompt-engineer";
    public static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(45) };

    public async Task VerifyAccessAsync(string token, CancellationToken ct = default)
    {
        using var response = await GetAsync($"repos/{Repository}/contents/", token, ct).ConfigureAwait(false);
        if (response.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid repository content response.");
    }

    public async Task<RepositorySkillLibrary> DiscoverSkillsAsync(string token, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new GitHubAccessException("Conecta GitHub para leer la biblioteca privada.", HttpStatusCode.Unauthorized);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.CancelAfter(TimeSpan.FromMinutes(3));
        var ct = operation.Token;
        using (var access = await GetAsync($"repos/{Repository}", token, ct).ConfigureAwait(false))
            if (access.RootElement.GetProperty("full_name").GetString() != Repository)
                throw new InvalidDataException("Unexpected source repository identity.");
        using var revision = await GetAsync($"repos/{Repository}/commits/main", token, ct).ConfigureAwait(false);
        var commit = revision.RootElement.GetProperty("sha").GetString()!;
        var tree = revision.RootElement.GetProperty("commit").GetProperty("tree").GetProperty("sha").GetString()!;
        if (!SourceRevision.Sha().IsMatch(commit) || !SourceRevision.Sha().IsMatch(tree)) throw new InvalidDataException("Invalid source revision.");
        using var listing = await GetAsync($"repos/{Repository}/git/trees/{tree}?recursive=1", token, ct).ConfigureAwait(false);
        if (listing.RootElement.GetProperty("truncated").GetBoolean()) throw new InvalidDataException("GitHub returned an incomplete source tree.");
        var entries = ReadTreeEntries(listing.RootElement.GetProperty("tree"));
        var manifestEntry = entries.SingleOrDefault(e => e.Path == "hub-manifest.json");
        ValidatedCatalog? catalog = null;
        string? catalogError = null;
        try
        {
            if (manifestEntry is null) throw new InvalidDataException("hub-manifest.json is missing.");
            catalog = HubCatalogContract.Read(await BlobAsync(manifestEntry, token, ct).ConfigureAwait(false), entries);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException)
        { catalogError = ex.Message; }
        var curatedByPath = catalog?.Catalog.Items.Where(i => i.Type == "skill")
            .ToDictionary(i => i.Path, StringComparer.OrdinalIgnoreCase) ?? new Dictionary<string, HubCatalogItem>(StringComparer.OrdinalIgnoreCase);
        var skills = new List<RepositorySkill>();
        var candidates = entries.Where(IsSkillEntry).ToArray();
        var skillIds = SkillIds(candidates, catalog);
        var metadata = new List<(SourceEntry Entry, byte[] Bytes)>();
        foreach (var batch in candidates.Chunk(6))
            metadata.AddRange(await Task.WhenAll(batch.Select(async entry =>
                (Entry: entry, Bytes: await BlobAsync(entry, token, ct).ConfigureAwait(false)))).ConfigureAwait(false));
        foreach (var (entry, bytes) in metadata)
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
                var skillMetadata = HarnessAdapter.ReadSkillMetadata(bytes);
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
                skills.Add(new RepositorySkill(item.Id, item.Id, "", item.Path,
                    [], false, null, true, item.Type, null, catalog.ItemCategories[item.Id], catalog.Catalog.DefaultLanguage));
        return new RepositorySkillLibrary(commit, skills, catalog?.Categories, catalog?.Catalog.DefaultLanguage ?? "en", catalogError);
    }

    public async Task<PreparedSnapshot> PrepareAsync(string token, HarnessAdapter adapter,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default, string reference = "main", string? sourcePath = null)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new GitHubAccessException("Conecta GitHub para leer la biblioteca privada.", HttpStatusCode.Unauthorized);
        if (reference != "main" && !SourceRevision.Sha().IsMatch(reference)) throw new InvalidDataException("Invalid source reference.");
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.CancelAfter(TimeSpan.FromMinutes(8));
        var ct = operation.Token;
        progress?.ReportMessage(new("hub.check_access"), "Comprobando acceso a GitHub…");
        using (var access = await GetAsync($"repos/{Repository}", token, ct).ConfigureAwait(false))
            if (access.RootElement.GetProperty("full_name").GetString() != Repository)
                throw new InvalidDataException("Unexpected source repository identity.");
        using var revision = await GetAsync($"repos/{Repository}/commits/{reference}", token, ct).ConfigureAwait(false);
        var commit = revision.RootElement.GetProperty("sha").GetString()!;
        var tree = revision.RootElement.GetProperty("commit").GetProperty("tree").GetProperty("sha").GetString()!;
        if (!SourceRevision.Sha().IsMatch(commit) || !SourceRevision.Sha().IsMatch(tree)) throw new InvalidDataException("Invalid source revision.");
        if (reference != "main" && commit != reference) throw new InvalidDataException("The source revision no longer matches the reviewed snapshot.");
        if (reference != "main" && commit != reference) throw new InvalidDataException("GitHub returned a different source revision than requested.");
        using var listing = await GetAsync($"repos/{Repository}/git/trees/{tree}?recursive=1", token, ct).ConfigureAwait(false);
        if (listing.RootElement.GetProperty("truncated").GetBoolean()) throw new InvalidDataException("GitHub returned an incomplete source tree.");
        var entries = ReadTreeEntries(listing.RootElement.GetProperty("tree"));
        var manifestEntry = entries.SingleOrDefault(e => e.Path == "hub-manifest.json");
        ValidatedCatalog? catalog = null;
        if (manifestEntry is not null)
            try { catalog = HubCatalogContract.Read(await BlobAsync(manifestEntry, token, ct).ConfigureAwait(false), entries); }
            catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException) { }
        var coverage = new List<CoverageItem>();
        var files = new List<PreparedFile>();
        var artifacts = new List<PreparedArtifact>();
        var selected = new List<HubCatalogItem>();
        if (sourcePath is null) selected.AddRange(catalog?.Catalog.Items.Where(i => i.Type == "skill" && !catalog.UnavailableSkills.Contains(i.Id)) ?? []);
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
        var invalidSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in selected.Where(i => i.Type == "skill"))
        {
            var source = entries.Single(e => e.Path == item.Path + "/SKILL.md");
            try
            {
                var (name, description) = HarnessAdapter.SkillMetadata(await BlobAsync(source, token, ct).ConfigureAwait(false));
                if (name != item.Path[(item.Path.LastIndexOf('/') + 1)..] || string.IsNullOrWhiteSpace(description) || description.Length > 1024)
                    invalidSkills.Add(item.Id);
            }
            catch (Exception ex) when (ex is InvalidDataException or DecoderFallbackException or ArgumentException or YamlException)
            { invalidSkills.Add(item.Id); }
        }
        if (sourcePath is not null && invalidSkills.Count > 0)
            throw new InvalidDataException("Requested repository skill has invalid metadata.");
        var removedInvalidDependency = true;
        while (removedInvalidDependency)
        {
            removedInvalidDependency = selected.RemoveAll(item => invalidSkills.Contains(item.Id) ||
                item.Dependencies.Any(dependency => invalidSkills.Contains(dependency) ||
                    selected.All(candidate => candidate.Id != dependency))) > 0;
        }
        selected.RemoveAll(item => invalidSkills.Contains(item.Id));
        foreach (var item in selected)
        {
            if (item.Type != "skill") continue;
            var source = item.Path;
            var folder = source[(source.LastIndexOf('/') + 1)..];
            var artifact = new SourceArtifact { Id = folder, Source = source, Category = "skill" };
            var members = entries.Where(e => e.Path == source + "/SKILL.md" || e.Path.StartsWith(source + "/", StringComparison.Ordinal)).ToList();
            var metadata = await BlobAsync(members.Single(e => e.Path == source + "/SKILL.md"), token, ct).ConfigureAwait(false);
            var (name, description) = HarnessAdapter.SkillMetadata(metadata);
            if (name != folder || string.IsNullOrWhiteSpace(description) || description.Length > 1024)
                throw new InvalidDataException("Invalid skill identity or description in repository metadata.");
            progress?.ReportMessage(new("hub.downloading", item.Id), $"Descargando {item.Id}…");
            var payloads = new List<(SourceEntry Entry, byte[] Bytes)>();
            // Bounded parallelism avoids hundreds of serial requests without exhausting GitHub's concurrency limits.
            foreach (var batch in members.Chunk(6))
            {
                var downloaded = await Task.WhenAll(batch.Select(async e => (Entry: e, Bytes: await BlobAsync(e, token, ct).ConfigureAwait(false)))).ConfigureAwait(false);
                payloads.AddRange(downloaded);
            }
            files.AddRange(adapter.Prepare(artifact, payloads).Select(file => file with { ArtifactId = item.Id }));
            artifacts.Add(new(item.Id, "skill", source, description, []));
            coverage.Add(new(source, "installable", "Formato nativo; no se ejecutan scripts ni se instalan dependencias.", members.Count));
        }
        var dependencyMap = selected.ToDictionary(i => i.Id, i => (IReadOnlyList<string>)(catalog?.ById.GetValueOrDefault(i.Id)?.Dependencies ?? []), StringComparer.OrdinalIgnoreCase);
        return new PreparedSnapshot(commit, tree, "1", files, coverage, dependencyMap, artifacts);
    }

    public async Task<byte[]> ReadCatalogFileAsync(string token, string sourcePath, string reference, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new GitHubAccessException("Connect GitHub to read the private repository.", HttpStatusCode.Unauthorized);
        if (string.IsNullOrWhiteSpace(token)) throw new GitHubAccessException("Conecta GitHub para leer la biblioteca privada.", HttpStatusCode.Unauthorized);
        InstallerPaths.Relative(sourcePath);
        if (reference != "main" && !SourceRevision.Sha().IsMatch(reference)) throw new InvalidDataException("Invalid source reference.");
        using var revision = await GetAsync($"repos/{Repository}/commits/{reference}", token, cancellationToken).ConfigureAwait(false);
        var commit = revision.RootElement.GetProperty("sha").GetString()!;
        var tree = revision.RootElement.GetProperty("commit").GetProperty("tree").GetProperty("sha").GetString()!;
        if (!SourceRevision.Sha().IsMatch(commit) || !SourceRevision.Sha().IsMatch(tree)) throw new InvalidDataException("Invalid source revision.");
        if (reference != "main" && commit != reference) throw new InvalidDataException("The source revision no longer matches the reviewed snapshot.");
        if (reference != "main" && commit != reference) throw new InvalidDataException("GitHub returned a different source revision than requested.");
        using var listing = await GetAsync($"repos/{Repository}/git/trees/{tree}?recursive=1", token, cancellationToken).ConfigureAwait(false);
        if (listing.RootElement.GetProperty("truncated").GetBoolean()) throw new InvalidDataException("GitHub returned an incomplete source tree.");
        var entries = ReadTreeEntries(listing.RootElement.GetProperty("tree"));
        var manifest = entries.SingleOrDefault(e => e.Path == "hub-manifest.json") ?? throw new InvalidDataException("hub-manifest.json is missing.");
        var catalog = HubCatalogContract.Read(await BlobAsync(manifest, token, cancellationToken).ConfigureAwait(false), entries);
        var item = catalog.Catalog.Items.SingleOrDefault(i => i.Path == sourcePath && i.Type is "instruction" or "file")
            ?? throw new InvalidDataException("Only explicitly curated instruction and file items can be exported.");
        var source = entries.Single(e => e.Path == item.Path);
        return await BlobAsync(source, token, cancellationToken).ConfigureAwait(false);
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

    private async Task<byte[]> BlobAsync(SourceEntry entry, string token, CancellationToken ct)
    {
        if (entry.Size is < 0 or > 4 * 1024 * 1024 || !SourceRevision.Sha().IsMatch(entry.Sha)) throw new InvalidDataException("Unsafe source blob.");
        using var document = await GetAsync($"repos/{Repository}/git/blobs/{entry.Sha}", token, ct).ConfigureAwait(false);
        if (document.RootElement.GetProperty("encoding").GetString() != "base64") throw new InvalidDataException("Unsupported GitHub encoding.");
        var bytes = Convert.FromBase64String(document.RootElement.GetProperty("content").GetString()!);
        if (bytes.LongLength != entry.Size || InstallerPaths.GitBlob(bytes) != entry.Sha) throw new InvalidDataException("Source blob integrity check failed.");
        return bytes;
    }

    private async Task<JsonDocument> GetAsync(string path, string token, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var cacheable = path.StartsWith($"repos/{Repository}/git/", StringComparison.Ordinal) ||
            (path.StartsWith($"repos/{Repository}/commits/", StringComparison.Ordinal) && SourceRevision.Sha().IsMatch(path[(path.LastIndexOf('/') + 1)..]));
        if (cacheable && _immutableResponses.TryGetValue((token, path), out var cached)) return JsonDocument.Parse(cached);
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/" + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
            request.Headers.UserAgent.ParseAdd("Hmy.Estao-HarnessInstaller/1.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            HttpResponseMessage response;
            try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false); }
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
                var document = JsonDocument.Parse(bytes);
                if (cacheable) _immutableResponses.TryAdd((token, path), bytes);
                return document;
            }
        }
    }
}
