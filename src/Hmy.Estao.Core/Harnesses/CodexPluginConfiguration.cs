using System.Text;
using System.Text.RegularExpressions;
using Hmy.Estao.Core.Configuration;

namespace Hmy.Estao.Core.Harnesses;

internal static partial class CodexPluginConfiguration
{
    public static string Extract(string configText)
    {
        var blocks = ParseBlocks(configText)
            .Where(block => IsPluginSection(block.Name))
            .Select(block => block.Text.TrimEnd());
        var value = string.Join(Environment.NewLine + Environment.NewLine, blocks);
        return value.Length == 0 ? string.Empty : value + Environment.NewLine;
    }

    public static async Task MergeAsync(
        HarnessProfileConfig target, byte[] portableContent, CancellationToken cancellationToken = default)
    {
        if (target.Id != "codex")
            throw new InvalidOperationException("Codex plugin configuration can only be installed into Codex.");
        var targetPath = HarnessPaths.McpConfiguration(target);
        var incoming = ParseBlocks(Encoding.UTF8.GetString(portableContent))
            .Where(block => IsPluginSection(block.Name))
            .ToList();
        if (incoming.Count == 0) throw new InvalidDataException("Portable Codex plugin configuration is empty.");

        var existing = File.Exists(targetPath)
            ? await File.ReadAllTextAsync(targetPath, cancellationToken).ConfigureAwait(false)
            : string.Empty;
        foreach (var block in incoming)
        {
            var current = ParseBlocks(existing)
                .FirstOrDefault(item => string.Equals(item.Name, block.Name, StringComparison.OrdinalIgnoreCase));
            if (current is not null)
                existing = existing.Remove(current.Index, current.Length).Insert(current.Index, block.Text.TrimEnd() + Environment.NewLine);
            else
                existing = existing.TrimEnd() + Environment.NewLine + Environment.NewLine + block.Text.TrimEnd() + Environment.NewLine;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await File.WriteAllTextAsync(targetPath, existing.TrimStart(), cancellationToken).ConfigureAwait(false);
    }

    private static IReadOnlyList<TomlBlock> ParseBlocks(string text)
    {
        var matches = TableHeaderRegex().Matches(text);
        var result = new List<TomlBlock>(matches.Count);
        for (var index = 0; index < matches.Count; index++)
        {
            var match = matches[index];
            var end = index + 1 < matches.Count ? matches[index + 1].Index : text.Length;
            result.Add(new TomlBlock(match.Groups["name"].Value.Trim(), match.Index,
                end - match.Index, text[match.Index..end]));
        }
        return result;
    }

    private static bool IsPluginSection(string name) =>
        name.StartsWith("plugins.", StringComparison.OrdinalIgnoreCase) ||
        name.StartsWith("marketplaces.", StringComparison.OrdinalIgnoreCase);

    private sealed record TomlBlock(string Name, int Index, int Length, string Text);

    [GeneratedRegex("(?m)^\\s*\\[(?<name>[^]\\r\\n]+)\\]\\s*(?:#.*)?$")]
    private static partial Regex TableHeaderRegex();
}
