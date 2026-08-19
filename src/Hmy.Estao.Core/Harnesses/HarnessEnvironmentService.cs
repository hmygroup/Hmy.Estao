using System.Security.Cryptography;
using System.Text.Json;
using Hmy.Estao.Core.Configuration;

namespace Hmy.Estao.Core.Harnesses;

public sealed class HarnessEnvironmentStore
{
    private readonly string _configPath;

    public HarnessEnvironmentStore(string configPath)
    {
        _configPath = Path.GetFullPath(configPath);
    }

    public string ResolvePath(HarnessEnvironmentDocument environment)
    {
        if (string.Equals(environment.Scope, "project", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(Path.GetFullPath(Environment.ExpandEnvironmentVariables(environment.RootPath)),
                ".estao", "environment.json");
        var directory = Path.GetDirectoryName(_configPath) ?? throw new InvalidOperationException("Config path has no directory.");
        return Path.Combine(directory, "harness-environments", SafeId(environment.Id) + ".json");
    }

    public async Task<HarnessEnvironmentDocument?> LoadAsync(HarnessEnvironmentConfig environment,
        CancellationToken cancellationToken = default)
    {
        var document = FromConfig(environment);
        var path = ResolvePath(document);
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var loaded = await JsonSerializer.DeserializeAsync<HarnessEnvironmentDocument>(stream,
            HarnessHubService.JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Environment document is empty.");
        Validate(loaded);
        // Root paths are machine-specific. The local registration remains authoritative.
        loaded.RootPath = environment.RootPath;
        return loaded;
    }

    public async Task SaveAsync(HarnessEnvironmentDocument environment, CancellationToken cancellationToken = default)
    {
        Validate(environment);
        environment.UpdatedUtc = DateTimeOffset.UtcNow;
        var path = ResolvePath(environment);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             81920, FileOptions.Asynchronous))
                await JsonSerializer.SerializeAsync(stream, environment, HarnessHubService.JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    public static HarnessEnvironmentDocument FromConfig(HarnessEnvironmentConfig environment) => new()
    {
        Id = environment.Id,
        Name = environment.Name,
        HarnessId = environment.HarnessId,
        Scope = environment.Scope,
        RootPath = environment.RootPath
    };

    private static void Validate(HarnessEnvironmentDocument environment)
    {
        if (environment.SchemaVersion != 1) throw new InvalidDataException($"Unsupported environment schema {environment.SchemaVersion}.");
        if (!HarnessCatalog.IsSupported(environment.HarnessId))
            throw new InvalidDataException($"Unsupported harness '{environment.HarnessId}'.");
        if (string.IsNullOrWhiteSpace(environment.Id) || string.IsNullOrWhiteSpace(environment.Name))
            throw new InvalidDataException("Environment requires id and name.");
        if (environment.Scope is not ("personal" or "project"))
            throw new InvalidDataException($"Unsupported environment scope '{environment.Scope}'.");
        environment.Artifacts ??= [];
        foreach (var artifact in environment.Artifacts)
        {
            if (string.IsNullOrWhiteSpace(artifact.RepositoryId) || string.IsNullOrWhiteSpace(artifact.ArtifactId))
                throw new InvalidDataException("Environment artifact references require repository and artifact IDs.");
            artifact.ManagedFiles ??= [];
        }
    }

    private static string SafeId(string value)
    {
        var result = new string(value.Trim().ToLowerInvariant()
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-')
            .ToArray()).Trim('-');
        return result.Length == 0 ? throw new InvalidDataException("Environment ID is invalid.") : result;
    }
}

public sealed class HarnessEnvironmentSyncService
{
    private readonly HarnessPackageInstaller _installer = new();
    private readonly HarnessRestoreService _restore = new();

    public async Task<IReadOnlyList<HarnessDriftItem>> DetectDriftAsync(
        HarnessEnvironmentDocument environment, CancellationToken cancellationToken = default)
    {
        var result = new List<HarnessDriftItem>();
        var root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(environment.RootPath));
        foreach (var managed in environment.Artifacts.SelectMany(artifact => artifact.ManagedFiles))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Path.Combine(root, managed.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsWithin(root, path)) throw new InvalidDataException($"Managed path escapes the environment: '{managed.Path}'.");
            if (!File.Exists(path))
            {
                result.Add(new HarnessDriftItem(managed.Path, "missing", managed.Sha256, string.Empty));
                continue;
            }
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actual = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
                .ToLowerInvariant();
            if (!string.Equals(actual, managed.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                var semantic = await ComputeSemanticHashAsync(environment, path, cancellationToken).ConfigureAwait(false);
                var state = managed.SemanticSha256.Length > 0 && semantic.Length > 0
                    ? string.Equals(semantic, managed.SemanticSha256, StringComparison.OrdinalIgnoreCase)
                        ? "format-only"
                        : "semantic-modified"
                    : "modified";
                result.Add(new HarnessDriftItem(managed.Path, state, managed.Sha256, actual));
            }
        }
        return result;
    }

    public async Task<HarnessSyncResult> ApplyAtomicAsync(
        HarnessEnvironmentDocument environment,
        IReadOnlyList<HarnessSyncPlanItem> plan,
        CancellationToken cancellationToken = default)
    {
        var profile = HarnessCatalog.CreateDefaultProfile(environment.HarnessId, environment.RootPath);
        profile.Scope = environment.Scope;
        profile.Enabled = true;
        var installed = new List<string>();
        var warnings = new List<string>();
        var skipped = new List<string>();
        var restoreDirectories = new List<string>();
        var completedRestorePoints = new List<HarnessRestorePoint>();
        var originalArtifacts = environment.Artifacts.Select(CloneArtifact).ToList();
        try
        {
            foreach (var item in plan.Where(item => item.Enabled))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pointsBefore = (await _restore.ListAsync(profile, cancellationToken).ConfigureAwait(false))
                    .Select(point => Path.GetFullPath(point.Directory)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                HarnessInstallResult result;
                try
                {
                    result = await _installer.InstallAsync(item.Entry.PackagePath, profile, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    var partialPoints = (await _restore.ListAsync(profile, CancellationToken.None).ConfigureAwait(false))
                        .Where(point => !pointsBefore.Contains(Path.GetFullPath(point.Directory))).ToList();
                    foreach (var partialPoint in partialPoints)
                        await _restore.RestoreAsync(partialPoint, profile, CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
                installed.AddRange(result.InstalledFiles);
                warnings.AddRange(result.Warnings);
                skipped.AddRange(result.SkippedArtifacts);
                if (result.BackupDirectory is not null)
                {
                    restoreDirectories.Add(result.BackupDirectory);
                    var points = await _restore.ListAsync(profile, cancellationToken).ConfigureAwait(false);
                    var point = points.FirstOrDefault(candidate => string.Equals(
                        Path.GetFullPath(candidate.Directory), Path.GetFullPath(result.BackupDirectory),
                        StringComparison.OrdinalIgnoreCase));
                    if (point is not null) completedRestorePoints.Add(point);
                }
                var reference = environment.Artifacts.FirstOrDefault(reference =>
                    string.Equals(reference.RepositoryId, item.Entry.RepositoryId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(reference.ArtifactId, item.Entry.Manifest.Id, StringComparison.OrdinalIgnoreCase));
                if (reference is null)
                {
                    reference = new HarnessEnvironmentArtifact
                    {
                        RepositoryId = item.Entry.RepositoryId,
                        ArtifactId = item.Entry.Manifest.Id
                    };
                    environment.Artifacts.Add(reference);
                }
                reference.Version = item.Entry.Manifest.Version;
                reference.Enabled = true;
                reference.AppliedSha256 = await HashFileAsync(item.Entry.PackagePath, cancellationToken).ConfigureAwait(false);
                reference.ManagedFiles = await BuildManagedFilesAsync(environment, result.InstalledFiles,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            foreach (var point in completedRestorePoints.AsEnumerable().Reverse())
                await _restore.RestoreAsync(point, profile, CancellationToken.None).ConfigureAwait(false);
            environment.Artifacts = originalArtifacts;
            throw;
        }
        return new HarnessSyncResult(installed, warnings, skipped, restoreDirectories);
    }

    public async Task<string?> RemoveAsync(HarnessEnvironmentDocument environment,
        HarnessEnvironmentArtifact artifact, bool deleteFiles, CancellationToken cancellationToken = default)
    {
        if (!deleteFiles)
        {
            environment.Artifacts.Remove(artifact);
            return null;
        }
        var root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(environment.RootPath));
        var backupDirectory = Path.Combine(root, ".estao", "backups",
            DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff"), environment.HarnessId);
        var manifest = new HarnessRestoreManifest
        {
            HarnessId = environment.HarnessId,
            PackageId = artifact.ArtifactId,
            PackageName = artifact.ArtifactId,
            PackageVersion = artifact.Version,
            CreatedUtc = DateTimeOffset.UtcNow
        };
        foreach (var managed in artifact.ManagedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var source = Path.GetFullPath(Path.Combine(root, managed.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsWithin(root, source)) throw new InvalidDataException($"Managed path escapes the environment: '{managed.Path}'.");
            if (!File.Exists(source)) continue;
            var target = Path.GetFullPath(Path.Combine(backupDirectory,
                managed.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsWithin(backupDirectory, target)) throw new InvalidDataException($"Unsafe backup path '{managed.Path}'.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(source, target, overwrite: false);
            manifest.Entries.Add(new HarnessRestoreEntry { TargetPath = managed.Path, Existed = true });
        }
        if (manifest.Entries.Count > 0)
        {
            await HarnessRestoreService.WriteManifestAsync(backupDirectory, manifest, cancellationToken)
                .ConfigureAwait(false);
            foreach (var managed in artifact.ManagedFiles)
            {
                var path = Path.GetFullPath(Path.Combine(root, managed.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (IsWithin(root, path) && File.Exists(path)) File.Delete(path);
            }
        }
        environment.Artifacts.Remove(artifact);
        return manifest.Entries.Count == 0 ? null : backupDirectory;
    }

    public async Task TrimRestorePointsAsync(HarnessEnvironmentDocument environment, int keep = 10,
        CancellationToken cancellationToken = default)
    {
        var profile = HarnessCatalog.CreateDefaultProfile(environment.HarnessId, environment.RootPath);
        profile.Scope = environment.Scope;
        var points = await _restore.ListAsync(profile, cancellationToken).ConfigureAwait(false);
        foreach (var point in points.Where(point => point.IsComplete).Skip(Math.Max(1, keep)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Directory.Exists(point.Directory)) Directory.Delete(point.Directory, recursive: true);
        }
    }

    private static async Task<List<HarnessManagedFileState>> BuildManagedFilesAsync(HarnessEnvironmentDocument environment,
        IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(environment.RootPath));
        var result = new List<HarnessManagedFileState>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path) || !IsWithin(root, path)) continue;
            result.Add(new HarnessManagedFileState
            {
                Path = Path.GetRelativePath(root, path).Replace('\\', '/'),
                Sha256 = await HashFileAsync(path, cancellationToken).ConfigureAwait(false),
                SemanticSha256 = await ComputeSemanticHashAsync(environment, path, cancellationToken).ConfigureAwait(false)
            });
        }
        return result;
    }

    private static HarnessEnvironmentArtifact CloneArtifact(HarnessEnvironmentArtifact source) => new()
    {
        RepositoryId = source.RepositoryId,
        ArtifactId = source.ArtifactId,
        Version = source.Version,
        Enabled = source.Enabled,
        UpdateChannel = source.UpdateChannel,
        AppliedSha256 = source.AppliedSha256,
        ManagedFiles = source.ManagedFiles.Select(file => new HarnessManagedFileState
        {
            Path = file.Path,
            Sha256 = file.Sha256,
            SemanticSha256 = file.SemanticSha256
        }).ToList()
    };

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
    }

    private static async Task<string> ComputeSemanticHashAsync(HarnessEnvironmentDocument environment, string path,
        CancellationToken cancellationToken)
    {
        try
        {
            var extension = Path.GetExtension(path);
            if (string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase))
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                }, cancellationToken).ConfigureAwait(false);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(document.RootElement, HarnessHubService.JsonOptions);
                return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            }
            if (string.Equals(extension, ".toml", StringComparison.OrdinalIgnoreCase))
            {
                var profile = HarnessCatalog.CreateDefaultProfile(environment.HarnessId, environment.RootPath);
                profile.Scope = environment.Scope;
                var mcp = await HarnessMcpConfiguration.ReadAsync(profile, cancellationToken).ConfigureAwait(false);
                if (mcp is not null)
                {
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(mcp.Configuration, HarnessHubService.JsonOptions);
                    return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                }
            }
        }
        catch (JsonException)
        {
            // Malformed structured content remains ordinary byte-level drift.
        }
        catch (InvalidOperationException)
        {
            // Unsupported or incomplete structured content remains byte-level drift.
        }
        return string.Empty;
    }

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
               !Path.IsPathRooted(relative);
    }
}
