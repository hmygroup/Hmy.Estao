using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Hmy.Estao.Core.Harnesses.Installer;

public sealed class GitHubAccessException(string message, HttpStatusCode status) : Exception(message)
{
    public HttpStatusCode Status { get; } = status;
}

public sealed class GitHubRepositorySource(HttpClient client)
{
    public const string Repository = "hmygroup/hmy-prompt-engineer";
    public static HttpClient CreateClient() => new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = TimeSpan.FromSeconds(45) };

    public async Task VerifyAccessAsync(string token, CancellationToken ct = default)
    {
        using var response = await GetAsync($"repos/{Repository}/contents/", token, ct).ConfigureAwait(false);
        if (response.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("Invalid repository content response.");
    }

    public async Task<PreparedSnapshot> PrepareAsync(string token, HarnessAdapter adapter,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default, string reference = "main")
    {
        if (string.IsNullOrWhiteSpace(token)) throw new GitHubAccessException("Conecta GitHub para leer la biblioteca privada.", HttpStatusCode.Unauthorized);
        if (reference != "main" && !ManifestContract.Sha().IsMatch(reference)) throw new InvalidDataException("Invalid source reference.");
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.CancelAfter(TimeSpan.FromMinutes(8));
        var ct = operation.Token;
        progress?.Report("Comprobando acceso a GitHub…");
        using (var access = await GetAsync($"repos/{Repository}", token, ct).ConfigureAwait(false))
            if (access.RootElement.GetProperty("full_name").GetString() != Repository)
                throw new InvalidDataException("Unexpected source repository identity.");
        using var revision = await GetAsync($"repos/{Repository}/commits/{reference}", token, ct).ConfigureAwait(false);
        var commit = revision.RootElement.GetProperty("sha").GetString()!;
        var tree = revision.RootElement.GetProperty("commit").GetProperty("tree").GetProperty("sha").GetString()!;
        if (!ManifestContract.Sha().IsMatch(commit) || !ManifestContract.Sha().IsMatch(tree)) throw new InvalidDataException("Invalid source revision.");
        using var listing = await GetAsync($"repos/{Repository}/git/trees/{tree}?recursive=1", token, ct).ConfigureAwait(false);
        if (listing.RootElement.GetProperty("truncated").GetBoolean()) throw new InvalidDataException("GitHub returned an incomplete source tree.");
        var entries = new List<SourceEntry>();
        foreach (var item in listing.RootElement.GetProperty("tree").EnumerateArray())
        {
            var mode = item.GetProperty("mode").GetString()!;
            if (item.GetProperty("type").GetString() == "tree") continue;
            if (mode is not ("100644" or "100755")) throw new InvalidDataException("Source contains a symbolic link or submodule.");
            entries.Add(new SourceEntry(item.GetProperty("path").GetString()!, item.GetProperty("sha").GetString()!,
                item.TryGetProperty("size", out var size) ? size.GetInt64() : -1, mode));
        }
        var manifestEntry = entries.SingleOrDefault(e => e.Path == "estao-harness.json");
        var manifest = manifestEntry is null ? ManifestContract.Bootstrap() :
            InstallerJson.Read<SourceManifest>(await BlobAsync(manifestEntry, token, ct).ConfigureAwait(false));
        ManifestContract.Validate(manifest, commit, tree, entries);
        var coverage = new List<CoverageItem>();
        var files = new List<PreparedFile>();
        var selected = manifest.Artifacts.Where(a => a.Harnesses.Contains(adapter.Id)).ToList();
        foreach (var artifact in manifest.Artifacts)
        {
            var members = entries.Where(e => ManifestContract.Matches(artifact.Source, e.Path)).ToList();
            if (!selected.Contains(artifact))
            {
                coverage.Add(new(artifact.Source, "unsupported", "No declarado compatible con este harness.", members.Count));
                continue;
            }
            if (artifact.Dependencies.Any(d => selected.All(a => a.Id != d))) throw new InvalidDataException("A required dependency is unavailable for the selected harness.");
            adapter.CheckCapabilities(artifact);
            progress?.Report($"Descargando {artifact.Id}…");
            var payloads = new List<(SourceEntry Entry, byte[] Bytes)>();
            // Bounded parallelism avoids hundreds of serial requests without exhausting GitHub's concurrency limits.
            foreach (var batch in members.Chunk(6))
            {
                var downloaded = await Task.WhenAll(batch.Select(async e => (Entry: e, Bytes: await BlobAsync(e, token, ct).ConfigureAwait(false)))).ConfigureAwait(false);
                payloads.AddRange(downloaded);
            }
            files.AddRange(adapter.Prepare(artifact, payloads));
            coverage.Add(new(artifact.Source, "installable", string.Join("; ", artifact.Requirements.Prepend("Formato nativo; no se ejecutan scripts ni se instalan dependencias.")), members.Count));
        }
        coverage.AddRange(manifest.Exclusions.Select(e => new CoverageItem(e.Source, e.Classification, e.Reason,
            entries.Count(f => ManifestContract.Matches(e.Source, f.Path)))));
        return new PreparedSnapshot(commit, tree, manifest.Version, files, coverage,
            selected.ToDictionary(a => a.Id, a => (IReadOnlyList<string>)a.Dependencies));
    }

    private async Task<byte[]> BlobAsync(SourceEntry entry, string token, CancellationToken ct)
    {
        if (entry.Size is < 0 or > 4 * 1024 * 1024 || !ManifestContract.Sha().IsMatch(entry.Sha)) throw new InvalidDataException("Unsafe source blob.");
        using var document = await GetAsync($"repos/{Repository}/git/blobs/{entry.Sha}", token, ct).ConfigureAwait(false);
        if (document.RootElement.GetProperty("encoding").GetString() != "base64") throw new InvalidDataException("Unsupported GitHub encoding.");
        var bytes = Convert.FromBase64String(document.RootElement.GetProperty("content").GetString()!);
        if (bytes.LongLength != entry.Size || InstallerPaths.GitBlob(bytes) != entry.Sha) throw new InvalidDataException("Source blob integrity check failed.");
        return bytes;
    }

    private async Task<JsonDocument> GetAsync(string path, string token, CancellationToken ct)
    {
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
            { throw new TimeoutException("GitHub no respondió dentro del tiempo permitido. Reintenta la operación."); }
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
                    if (seconds > 15) throw new GitHubAccessException("Límite de GitHub alcanzado. Reintenta más tarde.", response.StatusCode);
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
                    }, response.StatusCode);
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
                return JsonDocument.Parse(buffer.ToArray());
            }
        }
    }
}
