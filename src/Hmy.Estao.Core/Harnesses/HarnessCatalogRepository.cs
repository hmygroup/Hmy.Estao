using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hmy.Estao.Core.Configuration;

namespace Hmy.Estao.Core.Harnesses;

public sealed partial class HarnessCatalogRepository
{
    private const string CatalogEntryName = "catalog.json";

    public async Task<IReadOnlyList<HarnessPublishCandidate>> DiscoverCandidatesAsync(
        HarnessProfileConfig source, CancellationToken cancellationToken = default)
    {
        var artifacts = ExpandIndividualArtifacts(
            await HarnessArtifactDiscovery.DiscoverAsync(source, cancellationToken).ConfigureAwait(false));
        return artifacts
            .GroupBy(artifact => CandidateKey(artifact.Feature, artifact.LogicalPath), StringComparer.OrdinalIgnoreCase)
            .Select(group => new HarnessPublishCandidate(
                group.Key,
                CandidateName(group.Key),
                group.First().Feature,
                group.Select(artifact => new HarnessArtifactPreview(
                    artifact.Feature, artifact.LogicalPath, artifact.Content.LongLength, artifact.Redacted)).ToList()))
            .OrderBy(candidate => candidate.Feature)
            .ThenBy(candidate => candidate.Name)
            .ToList();
    }

    public async Task<HarnessCatalogEntry> PublishLocalAsync(
        HarnessRepositoryConfig repository,
        HarnessProfileConfig source,
        string candidateKey,
        HarnessCatalogDraft draft,
        string ownerName,
        CancellationToken cancellationToken = default)
    {
        if (!repository.Enabled) throw new InvalidOperationException($"Repository '{repository.Name}' is disabled.");
        if (!source.Enabled) throw new InvalidOperationException($"Harness '{source.Id}' is disabled.");
        var artifacts = ExpandIndividualArtifacts(
            await HarnessArtifactDiscovery.DiscoverAsync(source, cancellationToken).ConfigureAwait(false));
        var selected = artifacts.Where(artifact => string.Equals(
            CandidateKey(artifact.Feature, artifact.LogicalPath), candidateKey, StringComparison.OrdinalIgnoreCase)).ToList();
        if (selected.Count == 0) throw new InvalidOperationException("The selected local artifact no longer exists.");

        var feature = selected[0].Feature;
        if (selected.Any(artifact => !string.Equals(artifact.Feature, feature, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("A catalog artifact cannot contain multiple primary types.");
        var manifest = BuildCatalogManifest(source, feature, draft, ownerName);
        if (string.Equals(feature, HarnessFeatureIds.Skills, StringComparison.OrdinalIgnoreCase))
            manifest.CapabilityDescription = SkillDescription(selected);
        var packageManifest = new HarnessPackageManifest
        {
            Id = manifest.Id,
            Name = manifest.Name,
            Description = manifest.Description,
            PackageVersion = manifest.Version,
            SourceHarness = manifest.SourceHarness,
            SourceScope = source.Scope,
            Author = manifest.OwnerName,
            PublishedUtc = manifest.PublishedUtc
        };
        var root = PrepareRepository(repository.Path);
        var targetDirectory = Path.Combine(root, "artifacts", SafeSegment(manifest.Team), SafeSegment(manifest.Type),
            SafeSegment(manifest.Id), SafeSegment(manifest.Version));
        EnsureArtifactDirectory(root, targetDirectory);
        var targetPath = Path.Combine(targetDirectory, $"{SafeSegment(manifest.Id)}-{SafeSegment(manifest.Version)}.estao");
        if (File.Exists(targetPath))
            throw new IOException($"Artifact {manifest.Id} {manifest.Version} already exists and published versions are immutable.");
        var temporaryPath = targetPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite,
                             FileShare.None, 81920, FileOptions.Asynchronous))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                for (var index = 0; index < selected.Count; index++)
                {
                    var artifact = selected[index];
                    var extension = Path.GetExtension(artifact.LogicalPath);
                    var archivePath = $"payload/{artifact.Feature}/{index:D4}{extension}";
                    var entry = archive.CreateEntry(archivePath, CompressionLevel.Optimal);
                    await using (var output = entry.Open())
                        await output.WriteAsync(artifact.Content, cancellationToken).ConfigureAwait(false);
                    packageManifest.Artifacts.Add(new HarnessPackageArtifact
                    {
                        Feature = artifact.Feature,
                        LogicalPath = artifact.LogicalPath.Replace('\\', '/'),
                        ArchivePath = archivePath,
                        Sha256 = Convert.ToHexString(SHA256.HashData(artifact.Content)).ToLowerInvariant(),
                        Redacted = artifact.Redacted
                    });
                }
                await WriteJsonEntryAsync(archive, "manifest.json", packageManifest, cancellationToken).ConfigureAwait(false);
                await WriteJsonEntryAsync(archive, CatalogEntryName, manifest, cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, targetPath);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
        var info = new FileInfo(targetPath);
        return new HarnessCatalogEntry(repository.Id, repository.Name, manifest, targetPath, info.Length);
    }

    public async Task<HarnessCatalogEntry> PublishCollectionAsync(
        HarnessRepositoryConfig repository,
        HarnessCatalogDraft draft,
        string ownerName,
        IReadOnlyList<HarnessArtifactReference> references,
        bool snapshot,
        CancellationToken cancellationToken = default)
    {
        if (references.Count == 0) throw new InvalidOperationException("A collection must contain at least one artifact.");
        var type = snapshot ? HarnessCatalogItemTypes.Snapshot : HarnessCatalogItemTypes.Collection;
        var source = HarnessCatalog.CreateDefaultProfile("codex");
        var manifest = BuildCatalogManifest(source, type, draft, ownerName);
        manifest.SourceHarness = string.Empty;
        manifest.Compatibility = HarnessCatalog.All.ToDictionary(item => item.Id,
            _ => HarnessCompatibilityStates.Native, StringComparer.OrdinalIgnoreCase);
        manifest.References = references.Select(reference => new HarnessArtifactReference
        {
            RepositoryId = reference.RepositoryId,
            ArtifactId = reference.ArtifactId,
            Version = reference.Version,
            Enabled = reference.Enabled
        }).ToList();
        var root = PrepareRepository(repository.Path);
        var targetDirectory = Path.Combine(root, "artifacts", SafeSegment(manifest.Team), type,
            SafeSegment(manifest.Id), SafeSegment(manifest.Version));
        EnsureArtifactDirectory(root, targetDirectory);
        var targetPath = Path.Combine(targetDirectory, $"{SafeSegment(manifest.Id)}-{SafeSegment(manifest.Version)}.estao");
        if (File.Exists(targetPath)) throw new IOException($"{manifest.Name} {manifest.Version} already exists.");
        await using (var stream = new FileStream(targetPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                         81920, FileOptions.Asynchronous))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            await WriteJsonEntryAsync(archive, CatalogEntryName, manifest, cancellationToken).ConfigureAwait(false);
        var info = new FileInfo(targetPath);
        return new HarnessCatalogEntry(repository.Id, repository.Name, manifest, targetPath, info.Length);
    }

    public async Task<IReadOnlyList<HarnessCatalogEntry>> ListAsync(
        IEnumerable<HarnessRepositoryConfig> repositories, CancellationToken cancellationToken = default)
    {
        var result = new List<HarnessCatalogEntry>();
        foreach (var repository in repositories.Where(item => item.Enabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = ResolveRepository(repository.Path);
            var artifactsPath = Path.Combine(root, "artifacts");
            if (!Directory.Exists(artifactsPath)) continue;
            foreach (var path in Directory.EnumerateFiles(artifactsPath, "*.estao", SearchOption.AllDirectories))
            {
                try
                {
                    var manifest = await ReadCatalogManifestAsync(path, cancellationToken).ConfigureAwait(false);
                    if (string.Equals(manifest.State, HarnessArtifactStates.Archived, StringComparison.OrdinalIgnoreCase))
                        continue;
                    result.Add(new HarnessCatalogEntry(repository.Id, repository.Name, manifest, path,
                        new FileInfo(path).Length));
                }
                catch (InvalidDataException)
                {
                    // Ignore incomplete files while another user is publishing to the share.
                }
                catch (JsonException)
                {
                    // Ignore invalid catalog entries without hiding the rest of the repository.
                }
                catch (IOException)
                {
                    // A temporarily locked network file will be retried on refresh.
                }
            }
        }
        return result.OrderByDescending(entry => entry.Manifest.Recommended)
            .ThenByDescending(entry => entry.Manifest.PublishedUtc)
            .ThenBy(entry => entry.Manifest.Name).ToList();
    }

    public async Task<HarnessCatalogEntry> ImportAsync(HarnessRepositoryConfig repository, string sourcePath,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Artifact package was not found.", sourcePath);
        var manifest = await ReadCatalogManifestAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        // Payload artifacts must also pass the existing structural manifest validation.
        if (manifest.Type is not (HarnessCatalogItemTypes.Collection or HarnessCatalogItemTypes.Snapshot))
            _ = await HarnessHubService.ReadManifestAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var root = PrepareRepository(repository.Path);
        var targetDirectory = Path.Combine(root, "artifacts", SafeSegment(manifest.Team), SafeSegment(manifest.Type),
            SafeSegment(manifest.Id), SafeSegment(manifest.Version));
        EnsureArtifactDirectory(root, targetDirectory);
        var targetPath = Path.Combine(targetDirectory, $"{SafeSegment(manifest.Id)}-{SafeSegment(manifest.Version)}.estao");
        if (File.Exists(targetPath)) throw new IOException($"Artifact {manifest.Id} {manifest.Version} already exists.");
        var temporaryPath = targetPath + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                             81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var target = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             81920, FileOptions.Asynchronous))
            {
                await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, targetPath);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
        var info = new FileInfo(targetPath);
        return new HarnessCatalogEntry(repository.Id, repository.Name, manifest, targetPath, info.Length);
    }

    public static async Task<HarnessCatalogManifest> ReadCatalogManifestAsync(
        string packagePath, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = archive.GetEntry(CatalogEntryName) ?? throw new InvalidDataException("Artifact has no catalog metadata.");
        if (entry.Length > 1024 * 1024) throw new InvalidDataException("Catalog metadata is larger than 1 MB.");
        HarnessCatalogManifest manifest;
        await using (var input = entry.Open())
            manifest = await JsonSerializer.DeserializeAsync<HarnessCatalogManifest>(input,
                HarnessHubService.JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Catalog metadata is empty.");
        Validate(manifest);
        if (string.Equals(manifest.Type, HarnessFeatureIds.Skills, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(manifest.CapabilityDescription))
            manifest.CapabilityDescription = await ReadSkillDescriptionAsync(archive, cancellationToken).ConfigureAwait(false);
        return manifest;
    }

    public static string SuggestNextVersion(IEnumerable<string> existingVersions, string change = "minor")
    {
        var latest = existingVersions.Select(value => Version.TryParse(value, out var version) ? version : null)
            .Where(version => version is not null).Cast<Version>().OrderByDescending(version => version).FirstOrDefault()
            ?? new Version(0, 0, 0);
        return change.Trim().ToLowerInvariant() switch
        {
            "major" => $"{latest.Major + 1}.0.0",
            "patch" => $"{latest.Major}.{latest.Minor}.{Math.Max(0, latest.Build) + 1}",
            _ => $"{latest.Major}.{latest.Minor + 1}.0"
        };
    }

    private static HarnessCatalogManifest BuildCatalogManifest(HarnessProfileConfig source, string feature,
        HarnessCatalogDraft draft, string ownerName)
    {
        var id = Slug(draft.Id.Length == 0 ? draft.Name : draft.Id);
        if (id.Length == 0) throw new InvalidOperationException("Artifact ID must contain a letter or number.");
        if (string.IsNullOrWhiteSpace(draft.Name)) throw new InvalidOperationException("Artifact name is required.");
        var manifest = new HarnessCatalogManifest
        {
            Id = id,
            Name = draft.Name.Trim(),
            Summary = draft.Summary.Trim(),
            Description = draft.Description.Trim(),
            Type = feature,
            Version = NormalizeVersion(draft.Version),
            OwnerId = $"{Environment.UserDomainName}\\{Environment.UserName}",
            OwnerName = string.IsNullOrWhiteSpace(ownerName) ? Environment.UserName : ownerName.Trim(),
            Team = string.IsNullOrWhiteSpace(draft.Team) ? "company" : draft.Team.Trim(),
            SourceHarness = HarnessCatalog.NormalizeId(source.Id),
            Tags = draft.Tags.Select(tag => tag.Trim()).Where(tag => tag.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            ChangeNotes = draft.ChangeNotes.Trim(),
            AllowedScopes = draft.AllowedScopes.Select(scope => scope.Trim().ToLowerInvariant())
                .Where(scope => scope is "personal" or "project").Distinct(StringComparer.Ordinal).ToList(),
            PublishedUtc = DateTimeOffset.UtcNow
        };
        if (manifest.AllowedScopes.Count == 0) manifest.AllowedScopes = ["personal", "project"];
        foreach (var harness in HarnessCatalog.All)
        {
            manifest.Compatibility[harness.Id] = !HarnessCatalog.Supports(harness.Id, feature)
                ? HarnessCompatibilityStates.Unsupported
                : string.Equals(harness.Id, manifest.SourceHarness, StringComparison.Ordinal)
                    ? HarnessCompatibilityStates.Native
                    : HarnessFeatureIds.Portable.Contains(feature, StringComparer.OrdinalIgnoreCase)
                        ? HarnessCompatibilityStates.Converted
                        : HarnessCompatibilityStates.Unsupported;
        }
        return manifest;
    }

    private static void Validate(HarnessCatalogManifest manifest)
    {
        if (manifest.SchemaVersion != 1) throw new InvalidDataException($"Unsupported catalog schema {manifest.SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name))
            throw new InvalidDataException("Catalog metadata requires id and name.");
        if (!HarnessCatalogItemTypes.All.Contains(manifest.Type, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unknown artifact type '{manifest.Type}'.");
        if (!HarnessArtifactStates.All.Contains(manifest.State, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unknown artifact state '{manifest.State}'.");
        try
        {
            manifest.Version = NormalizeVersion(manifest.Version);
        }
        catch (InvalidOperationException exception)
        {
            throw new InvalidDataException("Artifact version is invalid.", exception);
        }
        manifest.Compatibility ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        manifest.References ??= [];
        if (manifest.Type == HarnessCatalogItemTypes.Collection && manifest.References.Any(reference =>
                string.Equals(reference.ArtifactId, manifest.Id, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("A collection cannot contain itself.");
    }

    private static async Task WriteJsonEntryAsync<T>(ZipArchive archive, string name, T value,
        CancellationToken cancellationToken)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        await using var output = entry.Open();
        await JsonSerializer.SerializeAsync(output, value, HarnessHubService.JsonOptions, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string CandidateKey(string feature, string logicalPath)
    {
        var normalized = logicalPath.Replace('\\', '/');
        var leaf = feature == HarnessFeatureIds.Mcp
            ? Path.GetFileNameWithoutExtension(normalized)
            : feature == HarnessFeatureIds.Skills || feature == HarnessFeatureIds.Agents || feature == HarnessFeatureIds.Prompts
            ? normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? normalized
            : feature;
        return $"{feature}:{leaf}";
    }

    private static IReadOnlyList<HarnessArtifactSource> ExpandIndividualArtifacts(
        IReadOnlyList<HarnessArtifactSource> artifacts)
    {
        var result = new List<HarnessArtifactSource>();
        foreach (var artifact in artifacts)
        {
            if (!string.Equals(artifact.Feature, HarnessFeatureIds.Mcp, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(artifact);
                continue;
            }
            var configuration = JsonSerializer.Deserialize<PortableMcpConfiguration>(artifact.Content,
                HarnessHubService.JsonOptions) ?? new PortableMcpConfiguration();
            foreach (var server in configuration.Servers)
            {
                var single = new PortableMcpConfiguration();
                single.Servers[server.Key] = server.Value;
                result.Add(new HarnessArtifactSource(HarnessFeatureIds.Mcp, $"{server.Key}.json",
                    JsonSerializer.SerializeToUtf8Bytes(single, HarnessHubService.JsonOptions), artifact.Redacted));
            }
        }
        return result;
    }

    private static string SkillDescription(IEnumerable<HarnessArtifactSource> artifacts)
    {
        var manifest = artifacts.FirstOrDefault(artifact =>
            string.Equals(Path.GetFileName(artifact.LogicalPath), "SKILL.md", StringComparison.OrdinalIgnoreCase));
        return manifest is null ? string.Empty : ParseSkillDescription(System.Text.Encoding.UTF8.GetString(manifest.Content));
    }

    private static async Task<string> ReadSkillDescriptionAsync(ZipArchive archive, CancellationToken cancellationToken)
    {
        var packageEntry = archive.GetEntry("manifest.json");
        if (packageEntry is null || packageEntry.Length > 1024 * 1024) return string.Empty;
        HarnessPackageManifest? package;
        await using (var input = packageEntry.Open())
            package = await JsonSerializer.DeserializeAsync<HarnessPackageManifest>(input,
                HarnessHubService.JsonOptions, cancellationToken).ConfigureAwait(false);
        var artifact = package?.Artifacts.FirstOrDefault(item =>
            string.Equals(Path.GetFileName(item.LogicalPath), "SKILL.md", StringComparison.OrdinalIgnoreCase));
        if (artifact is null) return string.Empty;
        var payload = archive.GetEntry(artifact.ArchivePath);
        if (payload is null || payload.Length > 256 * 1024) return string.Empty;
        using var reader = new StreamReader(payload.Open(), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 4096, leaveOpen: false);
        return ParseSkillDescription(await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false));
    }

    private static string ParseSkillDescription(string content)
    {
        var frontMatter = content.StartsWith("---", StringComparison.Ordinal)
            ? content.Split("---", StringSplitOptions.None).Skip(1).FirstOrDefault() ?? string.Empty
            : content;
        var match = SkillDescriptionRegex().Match(frontMatter);
        return match.Success ? match.Groups["value"].Value.Trim().Trim('"', '\'') : string.Empty;
    }

    private static string CandidateName(string key)
    {
        var separator = key.IndexOf(':');
        return separator < 0 ? key : key[(separator + 1)..];
    }

    private static string ResolveRepository(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Repository path is required.");
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim()));
    }

    private static string PrepareRepository(string value)
    {
        var repository = ResolveRepository(value);
        try
        {
            // Do not preflight the drive root. Mapped/network drives can deny root
            // enumeration while a concrete subdirectory remains fully accessible.
            Directory.CreateDirectory(repository);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            throw new InvalidOperationException(
                $"Harness Hub repository cannot be created or accessed: '{repository}'. {exception.Message}", exception);
        }
        return repository;
    }

    private static void EnsureArtifactDirectory(string repository, string targetDirectory)
    {
        var relative = Path.GetRelativePath(repository, targetDirectory);
        if (Path.IsPathRooted(relative) || relative == ".." ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidDataException($"Artifact directory escapes repository: '{targetDirectory}'.");
        var current = repository;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (Directory.Exists(current)) continue;
            try
            {
                Directory.CreateDirectory(current);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
            {
                throw new InvalidOperationException(
                    $"Cannot create artifact folder '{current}'. The repository itself is reachable, but Estao could not create this segment. " +
                    $"Verify create-folder permission and that no file has the same name. {exception.Message}", exception);
            }
        }
    }

    private static string NormalizeVersion(string value)
    {
        var version = value.Trim();
        var match = SemanticVersionRegex().Match(version);
        if (!match.Success)
            throw new InvalidOperationException("Version must be semantic, for example 2, 2.1, or 2.1.0-beta.1.");
        var major = match.Groups["major"].Value;
        var minor = match.Groups["minor"].Success ? match.Groups["minor"].Value : "0";
        var patch = match.Groups["patch"].Success ? match.Groups["patch"].Value : "0";
        return $"{major}.{minor}.{patch}{match.Groups["suffix"].Value}";
    }

    private static string SafeSegment(string value)
    {
        var segment = Slug(value);
        if (segment.Length == 0) throw new InvalidDataException($"Unsafe repository segment '{value}'.");
        return segment;
    }

    private static string Slug(string value) => SlugInvalidRegex().Replace(value.Trim().ToLowerInvariant(), "-").Trim('-');

    [GeneratedRegex("[^a-z0-9._-]+")]
    private static partial Regex SlugInvalidRegex();

    [GeneratedRegex("^(?<major>0|[1-9]\\d*)(?:\\.(?<minor>0|[1-9]\\d*))?(?:\\.(?<patch>0|[1-9]\\d*))?(?<suffix>(?:-[0-9A-Za-z.-]+)?(?:\\+[0-9A-Za-z.-]+)?)$")]
    private static partial Regex SemanticVersionRegex();

    [GeneratedRegex("^\\s*description\\s*:\\s*(?<value>.+?)\\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex SkillDescriptionRegex();
}
