using System.Text.Json;
using Hmy.Estao.Core.Configuration;

namespace Hmy.Estao.Core.Harnesses;

public sealed class HarnessRestoreService
{
    private const string ManifestFileName = "restore.json";

    public async Task<IReadOnlyList<HarnessRestorePoint>> ListAsync(
        HarnessProfileConfig profile, CancellationToken cancellationToken = default)
    {
        var basePath = HarnessPaths.ResolveBasePath(profile);
        var backupRoot = Path.Combine(basePath, ".estao", "backups");
        if (!Directory.Exists(backupRoot)) return [];
        var result = new List<HarnessRestorePoint>();
        foreach (var path in EnumerateSafeFiles(backupRoot)
                     .Where(path => string.Equals(Path.GetFileName(path), ManifestFileName, StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var manifest = await ReadManifestAsync(path, cancellationToken).ConfigureAwait(false);
                if (string.Equals(manifest.HarnessId, profile.Id, StringComparison.OrdinalIgnoreCase))
                    result.Add(new HarnessRestorePoint(manifest, Path.GetDirectoryName(path)!));
            }
            catch (InvalidDataException)
            {
                // Ignore incomplete legacy or interrupted restore points.
            }
            catch (JsonException)
            {
                // Ignore malformed journals without affecting other restore points.
            }
        }
        foreach (var directory in EnumerateSafeDirectories(backupRoot)
                     .Where(path => string.Equals(Path.GetFileName(path), profile.Id, StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(Path.Combine(directory, ManifestFileName)) ||
                (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            var files = EnumerateSafeFiles(directory).ToList();
            if (files.Count == 0) continue;
            result.Add(new HarnessRestorePoint(new HarnessRestoreManifest
            {
                HarnessId = profile.Id,
                PackageId = "legacy",
                PackageName = "Legacy Estao installation",
                PackageVersion = "unknown",
                CreatedUtc = new DirectoryInfo(directory).LastWriteTimeUtc,
                Entries = files.Select(file => new HarnessRestoreEntry
                {
                    TargetPath = Path.GetRelativePath(directory, file).Replace('\\', '/'),
                    Existed = true
                }).ToList()
            }, directory, IsComplete: false));
        }
        return result.OrderByDescending(item => item.Manifest.CreatedUtc).ToList();
    }

    public async Task<HarnessRestoreResult> RestoreAsync(
        HarnessRestorePoint point,
        HarnessProfileConfig profile,
        CancellationToken cancellationToken = default)
    {
        if (!string.Equals(point.Manifest.HarnessId, profile.Id, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The restore point belongs to a different harness.");
        ValidateManifest(point.Manifest);
        var basePath = Path.GetFullPath(HarnessPaths.ResolveBasePath(profile));
        var backupRoot = Path.GetFullPath(Path.Combine(basePath, ".estao", "backups"));
        EnsureWithin(backupRoot, point.Directory);
        var restored = new List<string>();
        var removed = new List<string>();

        foreach (var entry in point.Manifest.Entries.AsEnumerable().Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = SafePath(basePath, entry.TargetPath);
            EnsureNoReparsePoints(basePath, target);
            if (entry.Existed)
            {
                var backup = SafePath(point.Directory, entry.TargetPath);
                if (!File.Exists(backup))
                    throw new InvalidDataException($"Restore payload '{entry.TargetPath}' is missing.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(backup, target, overwrite: true);
                restored.Add(target);
            }
            else if (File.Exists(target))
            {
                File.Delete(target);
                removed.Add(target);
            }
        }

        return new HarnessRestoreResult(restored, removed);
    }

    internal static async Task WriteManifestAsync(
        string directory, HarnessRestoreManifest manifest, CancellationToken cancellationToken)
    {
        ValidateManifest(manifest);
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, ManifestFileName);
        var temporary = target + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                             81920, FileOptions.Asynchronous))
                await JsonSerializer.SerializeAsync(stream, manifest, HarnessHubService.JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
            File.Move(temporary, target, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task<HarnessRestoreManifest> ReadManifestAsync(
        string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (info.Length > 1024 * 1024) throw new InvalidDataException("Restore manifest is larger than 1 MB.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var manifest = await JsonSerializer.DeserializeAsync<HarnessRestoreManifest>(stream,
            HarnessHubService.JsonOptions, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Restore manifest is empty.");
        ValidateManifest(manifest);
        return manifest;
    }

    private static void ValidateManifest(HarnessRestoreManifest manifest)
    {
        if (manifest.SchemaVersion != 1) throw new InvalidDataException("Unsupported restore manifest version.");
        if (!HarnessCatalog.IsSupported(manifest.HarnessId)) throw new InvalidDataException("Unknown restore harness.");
        manifest.Entries ??= [];
        if (manifest.Entries.Count > 2000) throw new InvalidDataException("Restore point contains too many entries.");
        foreach (var entry in manifest.Entries) HarnessHubService.ValidateRelativePath(entry.TargetPath);
    }

    private static string SafePath(string root, string relativePath)
    {
        var relative = HarnessHubService.ValidateRelativePath(relativePath).Replace('/', Path.DirectorySeparatorChar);
        var target = Path.GetFullPath(Path.Combine(root, relative));
        EnsureWithin(root, target);
        return target;
    }

    private static void EnsureWithin(string root, string target)
    {
        var rootPath = Path.GetFullPath(root);
        var targetPath = Path.GetFullPath(target);
        var prefix = rootPath.EndsWith(Path.DirectorySeparatorChar) ? rootPath : rootPath + Path.DirectorySeparatorChar;
        if (!targetPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Restore path '{targetPath}' is outside '{rootPath}'.");
    }

    private static void EnsureNoReparsePoints(string root, string target)
    {
        var current = Path.GetFullPath(root);
        foreach (var part in Path.GetRelativePath(current, target).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"Restore target traverses reparse point '{current}'.");
        }
    }

    private static IEnumerable<string> EnumerateSafeFiles(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(current))
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) yield return file;
            foreach (var child in Directory.EnumerateDirectories(current))
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
        }
    }

    private static IEnumerable<string> EnumerateSafeDirectories(string directory)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(current))
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                yield return child;
                pending.Push(child);
            }
        }
    }
}
