using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Hmy.Estao.Core.Configuration;

namespace Hmy.Estao.Core.Harnesses;

internal static partial class HarnessArtifactDiscovery
{
    private const long MaximumFileSize = 20L * 1024L * 1024L;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<IReadOnlyList<HarnessArtifactSource>> DiscoverAsync(
        HarnessProfileConfig profile, CancellationToken cancellationToken = default)
    {
        if (!HarnessCatalog.IsSupported(profile.Id))
            throw new InvalidOperationException($"Harness '{profile.Id}' is not supported.");
        var enabled = profile.Features.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new List<HarnessArtifactSource>();

        if (enabled.Contains(HarnessFeatureIds.Instructions))
            await DiscoverInstructionsAsync(profile, result, cancellationToken).ConfigureAwait(false);

        foreach (var feature in new[] { HarnessFeatureIds.Skills, HarnessFeatureIds.Agents, HarnessFeatureIds.Prompts })
        {
            if (enabled.Contains(feature))
                await AddDirectoryAsync(HarnessPaths.FeatureDirectory(profile, feature), feature, result, cancellationToken)
                    .ConfigureAwait(false);
        }

        if (enabled.Contains(HarnessFeatureIds.Skills) && profile.Id == "codex")
            await AddDirectoryAsync(HarnessPaths.CodexLegacySkills(profile), HarnessFeatureIds.Skills, result,
                cancellationToken, excludedRootDirectories: new HashSet<string>([".system"], StringComparer.OrdinalIgnoreCase))
                .ConfigureAwait(false);

        if (enabled.Contains(HarnessFeatureIds.Hooks))
            await DiscoverHooksAsync(profile, result, cancellationToken).ConfigureAwait(false);

        if (enabled.Contains(HarnessFeatureIds.Rules) && profile.Id == "codex")
            await AddDirectoryAsync(HarnessPaths.FeatureDirectory(profile, HarnessFeatureIds.Rules),
                HarnessFeatureIds.Rules, result, cancellationToken).ConfigureAwait(false);

        if (enabled.Contains(HarnessFeatureIds.Plugins) && profile.Id == "codex")
            await DiscoverCodexPluginsAsync(profile, result, cancellationToken).ConfigureAwait(false);

        if (enabled.Contains(HarnessFeatureIds.Settings))
        {
            foreach (var path in HarnessPaths.SettingsFiles(profile).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(path)) continue;
                var content = await ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
                var sanitized = SecretTextSanitizer.Sanitize(path, content);
                result.Add(new HarnessArtifactSource(HarnessFeatureIds.Settings, Path.GetFileName(path),
                    sanitized.Content, sanitized.Redacted));
            }
        }

        if (enabled.Contains(HarnessFeatureIds.Mcp))
        {
            var mcp = await HarnessMcpConfiguration.ReadAsync(profile, cancellationToken).ConfigureAwait(false);
            if (mcp is not null)
            {
                result.Add(new HarnessArtifactSource(HarnessFeatureIds.Mcp, "mcp.json",
                    JsonSerializer.SerializeToUtf8Bytes(mcp.Configuration, JsonOptions), mcp.Redacted));
            }
        }

        return result;
    }

    private static async Task DiscoverInstructionsAsync(
        HarnessProfileConfig profile, List<HarnessArtifactSource> result, CancellationToken cancellationToken)
    {
        var primary = HarnessPaths.PrimaryInstructions(profile);
        var selectedPrimary = primary;
        if (profile.Id == "codex")
        {
            var overridePath = Path.Combine(Path.GetDirectoryName(primary) ?? string.Empty, "AGENTS.override.md");
            if (File.Exists(overridePath) && new FileInfo(overridePath).Length > 0) selectedPrimary = overridePath;
        }
        if (File.Exists(selectedPrimary))
        {
            var content = await ReadFileAsync(selectedPrimary, cancellationToken).ConfigureAwait(false);
            if (content.Length > 0)
                result.Add(new HarnessArtifactSource(HarnessFeatureIds.Instructions, "primary.md", content));
        }

        var additional = HarnessPaths.AdditionalInstructions(profile);
        if (additional is not null)
            await AddDirectoryAsync(additional, HarnessFeatureIds.Instructions, result, cancellationToken, "additional")
                .ConfigureAwait(false);

    }

    private static async Task DiscoverCodexPluginsAsync(
        HarnessProfileConfig profile, List<HarnessArtifactSource> result, CancellationToken cancellationToken)
    {
        var configPath = HarnessPaths.McpConfiguration(profile);
        if (!File.Exists(configPath)) return;
        var sanitized = SecretTextSanitizer.Sanitize(configPath,
            await ReadFileAsync(configPath, cancellationToken).ConfigureAwait(false));
        var sections = CodexPluginConfiguration.Extract(Encoding.UTF8.GetString(sanitized.Content));
        if (string.IsNullOrWhiteSpace(sections)) return;
        result.Add(new HarnessArtifactSource(HarnessFeatureIds.Plugins, "plugins.toml",
            Encoding.UTF8.GetBytes(sections), sanitized.Redacted));
    }

    private static async Task DiscoverHooksAsync(
        HarnessProfileConfig profile, List<HarnessArtifactSource> result, CancellationToken cancellationToken)
    {
        var directory = HarnessPaths.FeatureDirectory(profile, HarnessFeatureIds.Hooks);
        if (profile.Id == "codex")
        {
            var hooks = Path.Combine(directory, "hooks.json");
            if (File.Exists(hooks)) result.Add(new HarnessArtifactSource(HarnessFeatureIds.Hooks, "hooks.json",
                await ReadFileAsync(hooks, cancellationToken).ConfigureAwait(false)));
            return;
        }

        if (profile.Id == "claude") directory = Path.Combine(directory, "hooks");
        if (profile.Id == "opencode") return;
        await AddDirectoryAsync(directory, HarnessFeatureIds.Hooks, result, cancellationToken).ConfigureAwait(false);
    }

    private static async Task AddDirectoryAsync(
        string directory,
        string feature,
        List<HarnessArtifactSource> result,
        CancellationToken cancellationToken,
        string? prefix = null,
        IReadOnlySet<string>? excludedRootDirectories = null)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var path in EnumerateSafeFiles(directory, excludedRootDirectories)
                     .OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
            if (relative.Split('/').Any(part => part is ".git" or "node_modules" or "bin" or "obj" or ".system")) continue;
            var logical = prefix is null ? relative : $"{prefix}/{relative}";
            if (result.Any(item => string.Equals(item.Feature, feature, StringComparison.OrdinalIgnoreCase) &&
                                   string.Equals(item.LogicalPath, logical, StringComparison.OrdinalIgnoreCase))) continue;
            result.Add(new HarnessArtifactSource(feature, logical,
                await ReadFileAsync(path, cancellationToken).ConfigureAwait(false)));
        }
    }

    private static IEnumerable<string> EnumerateSafeFiles(
        string directory, IReadOnlySet<string>? excludedRootDirectories = null)
    {
        var pending = new Stack<string>();
        pending.Push(directory);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(current))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0) yield return file;
            }
            foreach (var child in Directory.EnumerateDirectories(current))
            {
                if (string.Equals(current, directory, StringComparison.OrdinalIgnoreCase) &&
                    excludedRootDirectories?.Contains(Path.GetFileName(child)) == true) continue;
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
            }
        }
    }

    private static async Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($"Refusing to publish reparse-point file '{path}'.");
        if (info.Length > MaximumFileSize)
            throw new InvalidOperationException($"'{path}' is larger than the {MaximumFileSize / 1024 / 1024} MB package limit.");
        return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
    }

    private sealed record SanitizedContent(byte[] Content, bool Redacted);

    private static partial class SecretTextSanitizer
    {
        public static SanitizedContent Sanitize(string path, byte[] content)
        {
            if (!IsTextFile(path)) return new SanitizedContent(content, false);
            var text = Encoding.UTF8.GetString(content);
            if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase) ||
                Path.GetExtension(path).Equals(".jsonc", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var root = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip
                    });
                    var changed = RedactJson(root);
                    return changed
                        ? new SanitizedContent(Encoding.UTF8.GetBytes(root!.ToJsonString(JsonOptions) + Environment.NewLine), true)
                        : new SanitizedContent(content, false);
                }
                catch (JsonException)
                {
                    // Fall through to conservative line-based redaction for malformed JSONC.
                }
            }

            var redacted = false;
            var lines = text.Split(['\r', '\n'], StringSplitOptions.None);
            for (var index = 0; index < lines.Length; index++)
            {
                var match = AssignmentRegex().Match(lines[index]);
                if (!match.Success || !SecretNameRegex().IsMatch(match.Groups["key"].Value) ||
                    IsReference(match.Groups["value"].Value)) continue;
                lines[index] = match.Groups["prefix"].Value + "\"${ENV:REQUIRED}\"" + match.Groups["suffix"].Value;
                redacted = true;
            }
            return redacted
                ? new SanitizedContent(Encoding.UTF8.GetBytes(string.Join(Environment.NewLine, lines)), true)
                : new SanitizedContent(content, false);
        }

        private static bool RedactJson(JsonNode? node)
        {
            var changed = false;
            if (node is JsonObject item)
            {
                foreach (var pair in item.ToList())
                {
                    if (SecretNameRegex().IsMatch(pair.Key) && pair.Value is JsonValue value &&
                        value.TryGetValue<string>(out var text) && !IsReference(text))
                    {
                        item[pair.Key] = $"${{ENV:{NormalizeName(pair.Key)}}}";
                        changed = true;
                    }
                    else changed |= RedactJson(pair.Value);
                }
            }
            else if (node is JsonArray array)
            {
                foreach (var child in array) changed |= RedactJson(child);
            }
            return changed;
        }

        private static bool IsReference(string value) =>
            value.Contains("${", StringComparison.Ordinal) || value.Contains("{env:", StringComparison.OrdinalIgnoreCase);

        private static bool IsTextFile(string path) => Path.GetExtension(path).ToLowerInvariant() is
            ".json" or ".jsonc" or ".toml" or ".yaml" or ".yml" or ".md" or ".txt";

        private static string NormalizeName(string name) => Regex.Replace(name.ToUpperInvariant(), "[^A-Z0-9_]", "_");

        [GeneratedRegex("token|secret|password|passwd|api[-_]?key|authorization|credential|private[-_]?key|cookie", RegexOptions.IgnoreCase)]
        private static partial Regex SecretNameRegex();

        [GeneratedRegex("^(?<prefix>\\s*(?<key>[A-Za-z0-9_.-]+)\\s*[=:]\\s*)(?<value>[^#\\r\\n,]+)(?<suffix>.*)$")]
        private static partial Regex AssignmentRegex();
    }
}
