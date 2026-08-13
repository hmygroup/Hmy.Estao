using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Platform;

namespace Hmy.Estao.Core.Harnesses;

public sealed partial class HarnessHubService
{
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public async Task<IReadOnlyList<HarnessArtifactPreview>> PreviewAsync(
        HarnessProfileConfig source, CancellationToken cancellationToken = default)
    {
        if (!source.Enabled) throw new InvalidOperationException($"Harness '{source.Id}' is disabled in Settings.");
        var artifacts = await HarnessArtifactDiscovery.DiscoverAsync(source, cancellationToken).ConfigureAwait(false);
        return artifacts.Select(item => new HarnessArtifactPreview(
            item.Feature, item.LogicalPath, item.Content.LongLength, item.Redacted)).ToList();
    }

    public async Task<HarnessHubPackage> PublishAsync(
        string hubPath,
        HarnessProfileConfig source,
        HarnessPackageDraft draft,
        CancellationToken cancellationToken = default)
    {
        if (!source.Enabled) throw new InvalidOperationException($"Harness '{source.Id}' is disabled in Settings.");
        var repository = ResolveHubPath(hubPath);
        var artifacts = await HarnessArtifactDiscovery.DiscoverAsync(source, cancellationToken).ConfigureAwait(false);
        if (artifacts.Count == 0)
            throw new InvalidOperationException("No enabled harness artifacts were found to publish.");
        if (artifacts.Sum(artifact => (long)artifact.Content.Length) > 100L * 1024L * 1024L)
            throw new InvalidOperationException("Package payload exceeds the 100 MB safety limit.");

        var manifest = new HarnessPackageManifest
        {
            Id = Slug(draft.Id.Length == 0 ? draft.Name : draft.Id),
            Name = Required(draft.Name, "Package name"),
            Description = draft.Description.Trim(),
            PackageVersion = NormalizeVersion(draft.PackageVersion),
            SourceHarness = HarnessCatalog.NormalizeId(source.Id),
            SourceScope = source.Scope,
            Author = Required(draft.Author, "Author"),
            PublishedUtc = DateTimeOffset.UtcNow
        };
        if (manifest.Id.Length == 0) throw new InvalidOperationException("Package ID must contain a letter or number.");

        var targetDirectory = Path.Combine(repository, "packages", manifest.Id, manifest.PackageVersion);
        Directory.CreateDirectory(targetDirectory);
        var targetPath = Path.Combine(targetDirectory, $"{manifest.Id}-{manifest.PackageVersion}.estao");
        if (File.Exists(targetPath))
            throw new IOException($"Package {manifest.Id} {manifest.PackageVersion} already exists in the hub.");
        var temporaryPath = targetPath + $".{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                             81920, FileOptions.Asynchronous))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
            {
                for (var index = 0; index < artifacts.Count; index++)
                {
                    var sourceArtifact = artifacts[index];
                    var extension = Path.GetExtension(sourceArtifact.LogicalPath);
                    var archivePath = $"payload/{sourceArtifact.Feature}/{index:D4}{extension}";
                    var entry = archive.CreateEntry(archivePath, CompressionLevel.Optimal);
                    await using (var output = entry.Open())
                        await output.WriteAsync(sourceArtifact.Content, cancellationToken).ConfigureAwait(false);
                    manifest.Artifacts.Add(new HarnessPackageArtifact
                    {
                        Feature = sourceArtifact.Feature,
                        LogicalPath = sourceArtifact.LogicalPath.Replace('\\', '/'),
                        ArchivePath = archivePath,
                        Sha256 = Convert.ToHexString(SHA256.HashData(sourceArtifact.Content)).ToLowerInvariant(),
                        Redacted = sourceArtifact.Redacted
                    });
                }

                var manifestEntry = archive.CreateEntry("manifest.json", CompressionLevel.Optimal);
                await using var manifestStream = manifestEntry.Open();
                await JsonSerializer.SerializeAsync(manifestStream, manifest, JsonOptions, cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporaryPath, targetPath);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }

        var info = new FileInfo(targetPath);
        return new HarnessHubPackage(manifest, targetPath, info.Length, info.LastWriteTimeUtc);
    }

    public async Task<IReadOnlyList<HarnessHubPackage>> ListAsync(
        string hubPath, CancellationToken cancellationToken = default)
    {
        var repository = ResolveHubPath(hubPath);
        var packages = Path.Combine(repository, "packages");
        if (!Directory.Exists(packages)) return [];
        var result = new List<HarnessHubPackage>();
        foreach (var path in Directory.EnumerateFiles(packages, "*.estao", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var manifest = await ReadManifestAsync(path, cancellationToken).ConfigureAwait(false);
                var info = new FileInfo(path);
                result.Add(new HarnessHubPackage(manifest, path, info.Length, info.LastWriteTimeUtc));
            }
            catch (InvalidDataException)
            {
                // A partial/corrupt file on a network share is ignored until a valid package replaces it.
            }
            catch (JsonException)
            {
                // Same treatment for an invalid package manifest.
            }
        }
        return result.OrderByDescending(item => item.Manifest.PublishedUtc).ThenBy(item => item.Manifest.Name).ToList();
    }

    public static async Task<string> DownloadAsync(
        HarnessHubPackage package,
        string? destinationPath = null,
        CancellationToken cancellationToken = default)
    {
        destinationPath ??= Path.Combine(EstaoPaths.ResolveDataDirectory(), "packages", "downloads",
            Path.GetFileName(package.Path));
        if (string.Equals(Path.GetFullPath(package.Path), Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Source package and download destination must be different files.");
        var directory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        await using var source = new FileStream(package.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None,
            81920, FileOptions.Asynchronous);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        return destinationPath;
    }

    public static async Task<HarnessPackageManifest> ReadManifestAsync(
        string packagePath, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var entry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("Package has no manifest.json.");
        if (entry.Length > 1024 * 1024) throw new InvalidDataException("Package manifest is larger than 1 MB.");
        await using var input = entry.Open();
        var manifest = await JsonSerializer.DeserializeAsync<HarnessPackageManifest>(input, JsonOptions, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("Package manifest is empty.");
        ValidateManifest(manifest);
        return manifest;
    }

    internal static void ValidateManifest(HarnessPackageManifest manifest)
    {
        if (manifest.SchemaVersion != 1) throw new InvalidDataException($"Unsupported package schema {manifest.SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(manifest.Id) || string.IsNullOrWhiteSpace(manifest.Name))
            throw new InvalidDataException("Package manifest requires id and name.");
        if (!HarnessCatalog.IsSupported(manifest.SourceHarness))
            throw new InvalidDataException($"Package source harness '{manifest.SourceHarness}' is not supported.");
        manifest.Artifacts ??= [];
        if (manifest.Artifacts.Count > 2000) throw new InvalidDataException("Package contains too many artifacts.");
        foreach (var artifact in manifest.Artifacts)
        {
            if (!HarnessFeatureIds.All.Contains(artifact.Feature, StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException($"Unknown package feature '{artifact.Feature}'.");
            ValidateRelativePath(artifact.LogicalPath);
            ValidateRelativePath(artifact.ArchivePath);
        }
    }

    internal static string ValidateRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value))
            throw new InvalidDataException($"Unsafe package path '{value}'.");
        var parts = value.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Any(part => part is "." or "..")) throw new InvalidDataException($"Unsafe package path '{value}'.");
        return string.Join('/', parts);
    }

    private static string ResolveHubPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException("Configure the department hub path first.");
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim());
        return Path.GetFullPath(expanded);
    }

    private static string NormalizeVersion(string value)
    {
        var version = string.IsNullOrWhiteSpace(value) ? "1.0.0" : value.Trim();
        if (!VersionRegex().IsMatch(version))
            throw new InvalidOperationException("Version may contain only letters, numbers, dots, dashes and plus signs.");
        return version;
    }

    private static string Required(string value, string name) =>
        string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException($"{name} is required.") : value.Trim();

    private static string Slug(string value) => SlugInvalidRegex().Replace(value.Trim().ToLowerInvariant(), "-").Trim('-');

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugInvalidRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9.+-]*$")]
    private static partial Regex VersionRegex();
}
