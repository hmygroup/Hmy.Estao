using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hmy.Estao.Core.Configuration;

namespace Hmy.Estao.Core.Harnesses;

public sealed class HarnessPackageInstaller
{
    public async Task<HarnessInstallResult> InstallAsync(
        string packagePath,
        HarnessProfileConfig target,
        CancellationToken cancellationToken = default)
    {
        if (!target.Enabled) throw new InvalidOperationException($"Harness '{target.Id}' is disabled in Settings.");
        if (!HarnessCatalog.IsSupported(target.Id)) throw new InvalidOperationException($"Harness '{target.Id}' is not supported.");
        if (new FileInfo(packagePath).Length > 200L * 1024L * 1024L)
            throw new InvalidDataException("Package file is larger than the 200 MB safety limit.");
        var basePath = HarnessPaths.ResolveBasePath(target);
        Directory.CreateDirectory(basePath);

        var installed = new List<string>();
        var skipped = new List<string>();
        var warnings = new List<string>();
        var backedUp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? backupDirectory = null;
        var enabledFeatures = target.Features.ToHashSet(StringComparer.OrdinalIgnoreCase);
        long extractedBytes = 0;

        await using var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var manifestEntry = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("Package has no manifest.json.");
        HarnessPackageManifest manifest;
        await using (var manifestStream = manifestEntry.Open())
        {
            manifest = await JsonSerializer.DeserializeAsync<HarnessPackageManifest>(manifestStream,
                HarnessHubService.JsonOptions, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("Package manifest is empty.");
        }
        HarnessHubService.ValidateManifest(manifest);
        var restoreManifest = new HarnessRestoreManifest
        {
            HarnessId = target.Id,
            PackageId = manifest.Id,
            PackageName = manifest.Name,
            PackageVersion = manifest.PackageVersion,
            CreatedUtc = DateTimeOffset.UtcNow
        };
        long declaredPayloadBytes = 0;
        foreach (var artifact in manifest.Artifacts)
        {
            var entry = archive.GetEntry(artifact.ArchivePath)
                ?? throw new InvalidDataException($"Package payload '{artifact.ArchivePath}' is missing.");
            if (entry.Length > 20L * 1024L * 1024L)
                throw new InvalidDataException($"Package payload '{artifact.LogicalPath}' is larger than 20 MB.");
            declaredPayloadBytes += entry.Length;
            if (declaredPayloadBytes > 100L * 1024L * 1024L)
                throw new InvalidDataException("Expanded package payload exceeds the 100 MB safety limit.");
        }

        async Task BackupAsync(string path)
        {
            EnsureWithinBase(basePath, path);
            if (!backedUp.Add(path)) return;
            backupDirectory ??= Path.Combine(basePath, ".estao", "backups",
                DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss-fff"), target.Id);
            var relative = Path.GetRelativePath(basePath, path);
            if (relative.StartsWith("..", StringComparison.Ordinal))
                throw new InvalidOperationException($"Cannot back up target outside configured base path: '{path}'.");
            var existed = File.Exists(path);
            if (existed)
            {
                var destination = SafeTarget(backupDirectory, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    81920, FileOptions.Asynchronous);
                await source.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
            }
            restoreManifest.Entries.Add(new HarnessRestoreEntry
            {
                TargetPath = relative.Replace('\\', '/'),
                Existed = existed
            });
            await HarnessRestoreService.WriteManifestAsync(backupDirectory, restoreManifest, cancellationToken)
                .ConfigureAwait(false);
        }

        async Task WriteAsync(string targetPath, byte[] content)
        {
            EnsureWithinBase(basePath, targetPath);
            await BackupAsync(targetPath).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await File.WriteAllBytesAsync(targetPath, content, cancellationToken).ConfigureAwait(false);
            installed.Add(targetPath);
        }

        var instructionArtifacts = manifest.Artifacts
            .Where(item => string.Equals(item.Feature, HarnessFeatureIds.Instructions, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (instructionArtifacts.Count > 0)
        {
            if (enabledFeatures.Contains(HarnessFeatureIds.Instructions))
                await InstallInstructionsAsync(archive, manifest, instructionArtifacts, target, basePath,
                    WriteAsync, BackupAsync, installed, warnings, cancellationToken).ConfigureAwait(false);
            else
                skipped.AddRange(instructionArtifacts.Select(item => $"{item.Feature}: {item.LogicalPath} (disabled)"));
        }

        foreach (var artifact in manifest.Artifacts.Where(item =>
                     !string.Equals(item.Feature, HarnessFeatureIds.Instructions, StringComparison.OrdinalIgnoreCase)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!enabledFeatures.Contains(artifact.Feature))
            {
                skipped.Add($"{artifact.Feature}: {artifact.LogicalPath} (disabled)");
                continue;
            }

            var content = await ReadArtifactAsync(archive, artifact, cancellationToken).ConfigureAwait(false);
            extractedBytes += content.Length;
            if (extractedBytes > 100L * 1024L * 1024L)
                throw new InvalidDataException("Expanded package payload exceeds the 100 MB safety limit.");
            if (artifact.Redacted)
                warnings.Add($"{artifact.Feature} '{artifact.LogicalPath}' contains secret placeholders that must be completed locally.");

            switch (artifact.Feature)
            {
                case HarnessFeatureIds.Skills:
                {
                    var targetPath = SafeTarget(HarnessPaths.FeatureDirectory(target, HarnessFeatureIds.Skills), artifact.LogicalPath);
                    await WriteAsync(targetPath, content).ConfigureAwait(false);
                    if (!string.Equals(manifest.SourceHarness, target.Id, StringComparison.OrdinalIgnoreCase) &&
                        artifact.LogicalPath.EndsWith("agents/openai.yaml", StringComparison.OrdinalIgnoreCase))
                        warnings.Add("The Codex agents/openai.yaml skill metadata was retained; other harnesses may ignore its UI and dependency fields.");
                    break;
                }
                case HarnessFeatureIds.Agents:
                {
                    var converted = HarnessAgentConverter.Convert(manifest.SourceHarness, target.Id, artifact.LogicalPath, content);
                    await WriteAsync(SafeTarget(HarnessPaths.FeatureDirectory(target, HarnessFeatureIds.Agents), converted.LogicalPath), converted.Content)
                        .ConfigureAwait(false);
                    if (converted.Warning is not null) warnings.Add(converted.Warning);
                    break;
                }
                case HarnessFeatureIds.Prompts:
                {
                    var logical = target.Id == "copilot"
                        ? EnsureCopilotPromptExtension(artifact.LogicalPath)
                        : EnsureMarkdownExtension(artifact.LogicalPath);
                    await WriteAsync(SafeTarget(HarnessPaths.FeatureDirectory(target, HarnessFeatureIds.Prompts), logical), content)
                        .ConfigureAwait(false);
                    if (!string.Equals(manifest.SourceHarness, target.Id, StringComparison.OrdinalIgnoreCase))
                        warnings.Add("Prompt/command content was preserved and renamed for the target; harness-specific frontmatter may need adjustment.");
                    break;
                }
                case HarnessFeatureIds.Mcp:
                {
                    var portable = JsonSerializer.Deserialize<PortableMcpConfiguration>(content, HarnessHubService.JsonOptions)
                        ?? throw new InvalidDataException("Portable MCP configuration is invalid.");
                    var mcpTarget = HarnessPaths.McpConfiguration(target);
                    EnsureWithinBase(basePath, mcpTarget);
                    await BackupAsync(mcpTarget).ConfigureAwait(false);
                    var paths = await HarnessMcpConfiguration.MergeAsync(target, portable, cancellationToken).ConfigureAwait(false);
                    installed.AddRange(paths);
                    if (!string.Equals(manifest.SourceHarness, target.Id, StringComparison.OrdinalIgnoreCase))
                        warnings.Add("MCP servers were translated to the target harness format; approval and tool-filter semantics remain target-specific.");
                    break;
                }
                case HarnessFeatureIds.Hooks:
                {
                    if (!string.Equals(manifest.SourceHarness, target.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        skipped.Add($"hooks: {artifact.LogicalPath} (no safe cross-harness mapping)");
                        warnings.Add("Hooks were not converted because lifecycle events and payloads differ between harnesses.");
                        break;
                    }
                    await WriteAsync(SafeTarget(HarnessPaths.FeatureDirectory(target, HarnessFeatureIds.Hooks), artifact.LogicalPath), content)
                        .ConfigureAwait(false);
                    break;
                }
                case HarnessFeatureIds.Rules:
                {
                    if (manifest.SourceHarness != "codex" || target.Id != "codex")
                    {
                        skipped.Add($"rules: {artifact.LogicalPath} (Codex-only)");
                        warnings.Add("Codex command rules were not copied to another harness because approval semantics differ.");
                        break;
                    }
                    await WriteAsync(SafeTarget(HarnessPaths.FeatureDirectory(target, HarnessFeatureIds.Rules),
                        artifact.LogicalPath), content).ConfigureAwait(false);
                    break;
                }
                case HarnessFeatureIds.Plugins:
                {
                    if (manifest.SourceHarness != "codex" || target.Id != "codex")
                    {
                        skipped.Add($"plugins: {artifact.LogicalPath} (Codex-only)");
                        warnings.Add("Codex plugin registrations were not copied to another harness; install equivalent extensions there explicitly.");
                        break;
                    }
                    var configTarget = HarnessPaths.McpConfiguration(target);
                    EnsureWithinBase(basePath, configTarget);
                    await BackupAsync(configTarget).ConfigureAwait(false);
                    await CodexPluginConfiguration.MergeAsync(target, content, cancellationToken).ConfigureAwait(false);
                    installed.Add(configTarget);
                    break;
                }
                case HarnessFeatureIds.Settings:
                {
                    if (!string.Equals(manifest.SourceHarness, target.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        skipped.Add($"settings: {artifact.LogicalPath} (source-only)");
                        warnings.Add("Raw settings were not copied across harnesses; only portable feature types were converted.");
                        break;
                    }
                    await WriteAsync(HarnessPaths.SettingsTarget(target, artifact.LogicalPath), content).ConfigureAwait(false);
                    break;
                }
                default:
                    skipped.Add($"{artifact.Feature}: {artifact.LogicalPath} (unsupported)");
                    break;
            }
        }

        return new HarnessInstallResult(
            installed.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            skipped.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            warnings.Distinct(StringComparer.Ordinal).ToList(),
            backupDirectory);
    }

    private static async Task InstallInstructionsAsync(
        ZipArchive archive,
        HarnessPackageManifest manifest,
        IReadOnlyList<HarnessPackageArtifact> artifacts,
        HarnessProfileConfig target,
        string basePath,
        Func<string, byte[], Task> writeAsync,
        Func<string, Task> backupAsync,
        List<string> installed,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        var primaryPath = HarnessPaths.PrimaryInstructions(target);
        EnsureWithinBase(basePath, primaryPath);
        var primary = artifacts.FirstOrDefault(item => string.Equals(item.LogicalPath, "primary.md", StringComparison.OrdinalIgnoreCase));
        if (primary is not null)
            await writeAsync(primaryPath, await ReadArtifactAsync(archive, primary, cancellationToken).ConfigureAwait(false))
                .ConfigureAwait(false);

        var additionalDirectory = HarnessPaths.AdditionalInstructions(target);
        var append = new List<string>();
        foreach (var artifact in artifacts.Where(item => !ReferenceEquals(item, primary)))
        {
            var content = await ReadArtifactAsync(archive, artifact, cancellationToken).ConfigureAwait(false);
            var logical = artifact.LogicalPath.StartsWith("additional/", StringComparison.OrdinalIgnoreCase)
                ? artifact.LogicalPath["additional/".Length..]
                : artifact.LogicalPath;
            if (additionalDirectory is not null)
            {
                logical = target.Id == "copilot" ? EnsureInstructionExtension(logical) : EnsureMarkdownExtension(logical);
                await writeAsync(SafeTarget(additionalDirectory, logical), content).ConfigureAwait(false);
            }
            else
            {
                append.Add($"<!-- Estao: {artifact.LogicalPath} -->{Environment.NewLine}{Encoding.UTF8.GetString(content).Trim()}");
            }
        }

        if (append.Count > 0)
        {
            await backupAsync(primaryPath).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(primaryPath)!);
            var existing = File.Exists(primaryPath)
                ? await File.ReadAllTextAsync(primaryPath, cancellationToken).ConfigureAwait(false)
                : string.Empty;
            var combined = string.Join(Environment.NewLine + Environment.NewLine,
                new[] { existing.Trim() }.Concat(append).Where(value => value.Length > 0));
            await File.WriteAllTextAsync(primaryPath, combined + Environment.NewLine, cancellationToken).ConfigureAwait(false);
            installed.Add(primaryPath);
            warnings.Add("Additional instruction files were merged into the target's single primary instruction file.");
        }

        if (!string.Equals(manifest.SourceHarness, target.Id, StringComparison.OrdinalIgnoreCase))
            warnings.Add($"Instructions were renamed for {HarnessCatalog.Get(target.Id).DisplayName}; imports and harness-specific directives should be reviewed.");
        if (artifacts.Any(item => item.Redacted))
            warnings.Add("Instructions contain secret placeholders that must be completed locally.");
    }

    private static async Task<byte[]> ReadArtifactAsync(
        ZipArchive archive, HarnessPackageArtifact artifact, CancellationToken cancellationToken)
    {
        var entry = archive.GetEntry(artifact.ArchivePath)
            ?? throw new InvalidDataException($"Package payload '{artifact.ArchivePath}' is missing.");
        if (entry.Length > 20L * 1024L * 1024L)
            throw new InvalidDataException($"Package payload '{artifact.LogicalPath}' is larger than 20 MB.");
        await using var input = entry.Open();
        using var output = new MemoryStream();
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        var content = output.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
        if (!string.Equals(hash, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Package payload '{artifact.LogicalPath}' failed its integrity check.");
        return content;
    }

    private static string EnsureMarkdownExtension(string path) => path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
        ? path
        : Path.ChangeExtension(path, ".md");

    private static string EnsureCopilotPromptExtension(string path) => path.EndsWith(".prompt.md", StringComparison.OrdinalIgnoreCase)
        ? path
        : Path.ChangeExtension(path, null) + ".prompt.md";

    private static string EnsureInstructionExtension(string path) => path.EndsWith(".instructions.md", StringComparison.OrdinalIgnoreCase)
        ? path
        : Path.ChangeExtension(path, null) + ".instructions.md";

    private static string SafeTarget(string root, string relativePath)
    {
        var validated = HarnessHubService.ValidateRelativePath(relativePath).Replace('/', Path.DirectorySeparatorChar);
        var rootPath = Path.GetFullPath(root);
        var target = Path.GetFullPath(Path.Combine(rootPath, validated));
        var prefix = rootPath.EndsWith(Path.DirectorySeparatorChar) ? rootPath : rootPath + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Unsafe target path '{relativePath}'.");
        return target;
    }

    private static void EnsureWithinBase(string basePath, string targetPath)
    {
        var root = Path.GetFullPath(basePath);
        var target = Path.GetFullPath(targetPath);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!target.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Harness target '{target}' is outside configured base path '{root}'.");
        var current = root;
        if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($"Configured base path '{root}' is a reparse point.");
        foreach (var part in Path.GetRelativePath(root, target).Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            if ((Directory.Exists(current) || File.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException($"Harness target traverses reparse point '{current}'.");
        }
    }
}
