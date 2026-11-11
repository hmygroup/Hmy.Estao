using System.Security.Cryptography;
using System.Text;
using Hmy.Estao.Core.Harnesses.Installer;
using YamlDotNet.Core;

namespace Hmy.Estao.Desktop.HarnessHub;

internal static class HubSkillInventory
{
    private const int MaxSkillsPerRoot = 2000;
    private const int MaxDirectoriesPerRoot = 10000;
    private const int MaxSkillFileBytes = 4 * 1024 * 1024;

    public static IReadOnlyList<HubSkill> Scan(IEnumerable<string> toolIds, Func<string, HarnessAdapter> adapterFactory,
        Action<string, string>? reportError = null, CancellationToken cancellationToken = default,
        IReadOnlySet<string>? skillDirectories = null)
    {
        var adapters = new List<(string ToolId, HarnessAdapter Adapter)>();
        foreach (var toolId in toolIds.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { adapters.Add((toolId, adapterFactory(toolId))); }
            catch (Exception ex) when (ex is not OperationCanceledException) { reportError?.Invoke(toolId, SafeError(ex)); }
        }

        var rootObservers = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (toolId, adapter) in adapters)
        {
            foreach (var root in GetRoots(adapter))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullRoot = Path.GetFullPath(root.Path);
                if (!rootObservers.TryGetValue(fullRoot, out var observers))
                    rootObservers[fullRoot] = observers = new(StringComparer.OrdinalIgnoreCase);
                observers.Add(toolId);
            }
        }

        var occurrences = new List<(SkillMetadataInfo Metadata, HubSkillOccurrence Occurrence)>();
        foreach (var (fullRoot, observers) in rootObservers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var found = new List<(SkillMetadataInfo Metadata, HubSkillOccurrence Occurrence)>();
            void Report(string _, string error) { foreach (var observer in observers) reportError?.Invoke(observer, error); }
            var directories = skillDirectories is null ? [fullRoot] : skillDirectories.Where(directory =>
                directory.Equals(fullRoot, StringComparison.OrdinalIgnoreCase) ||
                directory.StartsWith(Path.TrimEndingDirectorySeparator(fullRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            foreach (var directory in directories)
                ScanRoot("", directory, false, found, Report, cancellationToken);
            foreach (var toolId in observers)
            {
                var adapter = adapters.First(item => item.ToolId == toolId).Adapter;
                var shared = observers.Count > 1 || !fullRoot.Equals(Path.GetFullPath(adapter.SkillsRoot), StringComparison.OrdinalIgnoreCase);
                occurrences.AddRange(found.Select(item => (item.Metadata, item.Occurrence with { ToolId = toolId, Shared = shared })));
            }
        }

        return occurrences.GroupBy(x => x.Metadata.Name, StringComparer.OrdinalIgnoreCase).Select(group =>
        {
            var valid = group.Where(x => x.Occurrence.Hash is not null).ToArray();
            var differing = valid.Select(x => x.Occurrence.Hash).Distinct(StringComparer.Ordinal).Count() > 1;
            var entries = group.ToArray();
            var errors = entries.Where(x => x.Occurrence.Error is not null)
                .Select(x => $"{x.Occurrence.ToolId}: {x.Occurrence.Error}").ToArray();
            var metadata = valid.Select(x => x.Metadata).FirstOrDefault();
            var issue = differing ? "Copies with this name have different SKILL.md metadata/content." :
                errors.Length > 0 ? string.Join(" ", errors) : null;
            return new HubSkill(group.Key, group.Key, metadata?.Description ?? "", false, false, [],
                entries.Select(x => x.Occurrence).ToArray(), null, issue, Descriptions: metadata?.Descriptions);
        }).OrderBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static IEnumerable<(string Path, bool Compatible)> GetRoots(HarnessAdapter adapter)
    {
        yield return (adapter.SkillsRoot, false);
        foreach (var root in adapter.CompatibleSkillsRoots.Distinct(StringComparer.OrdinalIgnoreCase))
            if (!Path.GetFullPath(root).Equals(Path.GetFullPath(adapter.SkillsRoot), StringComparison.OrdinalIgnoreCase))
                yield return (root, true);
    }

    private static void ScanRoot(string toolId, string root, bool shared,
        List<(SkillMetadataInfo Metadata, HubSkillOccurrence Occurrence)> occurrences,
        Action<string, string>? reportError, CancellationToken cancellationToken)
    {
        var directoriesScanned = 0;
        var skillsScanned = 0;
        var pending = new Stack<string>();
        try
        {
            InstallerPaths.CheckLocal(root);
            pending.Push(root);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                if (++directoriesScanned > MaxDirectoriesPerRoot)
                {
                    reportError?.Invoke(toolId, $"Skill root '{root}' exceeds the {MaxDirectoriesPerRoot} directory scan limit.");
                    return;
                }

                InstallerPaths.CheckLocal(directory);
                var skillFile = Path.Combine(directory, "SKILL.md");
                try
                {
                    InstallerPaths.CheckLocal(skillFile);
                    using var stream = new FileStream(skillFile, FileMode.Open, FileAccess.Read, FileShare.Read);
                    var bytes = ReadBounded(stream, MaxSkillFileBytes, cancellationToken);
                    var name = Path.GetFileName(directory);
                    if (++skillsScanned > MaxSkillsPerRoot)
                    {
                        reportError?.Invoke(toolId, $"Skill root '{root}' exceeds the {MaxSkillsPerRoot} skill scan limit.");
                        return;
                    }

                    try
                    {
                        var metadata = HarnessAdapter.ReadSkillMetadata(bytes);
                        var (metadataName, description) = (metadata.Name, metadata.Description);
                        if (string.IsNullOrWhiteSpace(metadataName))
                            throw new InvalidDataException("SKILL.md metadata must include a name.");
                        if (metadataName.Contains('/') || metadataName.Contains('\\'))
                            throw new InvalidDataException("SKILL.md metadata name must be a single safe folder name.");
                        InstallerPaths.Relative(metadataName);
                        if (!metadataName.Equals(name, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException($"SKILL.md metadata name '{metadataName}' does not match folder '{name}'.");

                        if (string.IsNullOrWhiteSpace(description) || description.Length > 1024)
                            throw new InvalidDataException("SKILL.md metadata requires a description of at most 1024 characters.");
                        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                        occurrences.Add((metadata, new(toolId, skillFile, shared, hash)));
                    }
                    catch (Exception ex) when (ex is InvalidDataException or DecoderFallbackException or YamlException or ArgumentException)
                    {
                        occurrences.Add((new(name, "", new Dictionary<string, string>()), new(toolId, skillFile, shared, null, SafeError(ex))));
                    }

                    // A folder containing SKILL.md is the skill boundary; assets are not recursively inspected.
                    continue;
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                catch (Exception ex) when (IsScanFailure(ex))
                {
                    reportError?.Invoke(toolId, $"Could not scan '{directory}': {SafeError(ex)}");
                    continue;
                }

                try
                {
                    foreach (var child in Directory.EnumerateDirectories(directory))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (directoriesScanned + pending.Count >= MaxDirectoriesPerRoot)
                        {
                            reportError?.Invoke(toolId, $"Skill root '{root}' exceeds the {MaxDirectoriesPerRoot} directory scan limit.");
                            break;
                        }
                        try
                        {
                            InstallerPaths.CheckLocal(child);
                            pending.Push(child);
                        }
                        catch (Exception ex) when (IsScanFailure(ex))
                        { reportError?.Invoke(toolId, $"Could not scan '{child}': {SafeError(ex)}"); }
                    }
                }
                catch (Exception ex) when (IsScanFailure(ex))
                {
                    // Missing roots are normal; missing subtrees during a scan and denied access are not.
                    if (directory.Equals(root, StringComparison.OrdinalIgnoreCase) && ex is DirectoryNotFoundException)
                    {
                        if (RootIsFile(root)) reportError?.Invoke(toolId, $"Skill root '{root}' is not a directory.");
                        return;
                    }
                    reportError?.Invoke(toolId, $"Could not scan '{directory}': {SafeError(ex)}");
                }
            }
        }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (IsScanFailure(ex)) { reportError?.Invoke(toolId, $"Could not scan skill root '{root}': {SafeError(ex)}"); }
    }

    private static byte[] ReadBounded(Stream stream, int limit, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = stream.Read(chunk, 0, chunk.Length);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > limit) throw new InvalidDataException("SKILL.md exceeds the 4 MB scan limit.");
            buffer.Write(chunk, 0, read);
        }
    }

    private static bool IsScanFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or
        ArgumentException or NotSupportedException or InvalidDataException or System.Security.SecurityException;

    private static bool RootIsFile(string root)
    {
        try
        {
            InstallerPaths.CheckLocal(root);
            return (File.GetAttributes(root) & FileAttributes.Directory) == 0;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static string SafeError(Exception exception) =>
        string.IsNullOrWhiteSpace(exception.Message) ? "Could not read this skill." : exception.Message;
}
