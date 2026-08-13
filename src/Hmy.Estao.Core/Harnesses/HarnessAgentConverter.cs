using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hmy.Estao.Core.Harnesses;

internal sealed record ConvertedHarnessAgent(string LogicalPath, byte[] Content, string? Warning);

internal static partial class HarnessAgentConverter
{
    public static ConvertedHarnessAgent Convert(
        string sourceHarness,
        string targetHarness,
        string logicalPath,
        byte[] content)
    {
        if (string.Equals(sourceHarness, targetHarness, StringComparison.OrdinalIgnoreCase))
            return new ConvertedHarnessAgent(logicalPath, content, null);
        var text = Encoding.UTF8.GetString(content);
        if (string.Equals(sourceHarness, "codex", StringComparison.OrdinalIgnoreCase))
        {
            var agent = ReadCodexAgent(logicalPath, text);
            return new ConvertedHarnessAgent(Path.ChangeExtension(logicalPath, ".md"),
                Encoding.UTF8.GetBytes(ToMarkdown(targetHarness, agent) + Environment.NewLine),
                "Codex agent instructions were converted from TOML to Markdown; model, sandbox and tool policy were omitted because their semantics differ.");
        }
        if (string.Equals(targetHarness, "codex", StringComparison.OrdinalIgnoreCase))
        {
            var agent = ReadMarkdownAgent(logicalPath, text);
            return new ConvertedHarnessAgent(Path.ChangeExtension(logicalPath, ".toml"),
                Encoding.UTF8.GetBytes(ToCodexToml(agent) + Environment.NewLine),
                "Markdown agent instructions were converted to Codex TOML; target-specific model, sandbox and MCP policy should be configured separately.");
        }
        return new ConvertedHarnessAgent(Path.ChangeExtension(logicalPath, ".md"), content,
            "Agent Markdown was preserved, but target-specific tool and permission frontmatter should be reviewed.");
    }

    private static PortableAgent ReadCodexAgent(string path, string text)
    {
        var name = TomlValue(text, "name") ?? DefaultName(path);
        var description = TomlValue(text, "description") ?? $"Imported agent {name}.";
        var instructions = TripleQuotedValue(text, "developer_instructions") ?? string.Empty;
        return new PortableAgent(name, description, instructions.Trim());
    }

    private static PortableAgent ReadMarkdownAgent(string path, string text)
    {
        var name = DefaultName(path);
        var description = $"Imported agent {name}.";
        var body = text;
        if (text.StartsWith("---", StringComparison.Ordinal))
        {
            var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end >= 0)
            {
                var frontmatter = text[3..end];
                name = YamlValue(frontmatter, "name") ?? name;
                description = YamlValue(frontmatter, "description") ?? description;
                body = text[(end + 4)..].TrimStart('\r', '\n');
            }
        }
        return new PortableAgent(name, description, body.Trim());
    }

    private static string ToMarkdown(string targetHarness, PortableAgent agent)
    {
        var frontmatter = new List<string>
        {
            "---",
            $"name: {YamlQuote(agent.Name)}",
            $"description: {YamlQuote(agent.Description)}"
        };
        if (string.Equals(targetHarness, "opencode", StringComparison.OrdinalIgnoreCase))
            frontmatter.Add("mode: subagent");
        frontmatter.Add("---");
        frontmatter.Add(string.Empty);
        frontmatter.Add(agent.Instructions);
        return string.Join(Environment.NewLine, frontmatter).TrimEnd();
    }

    private static string ToCodexToml(PortableAgent agent)
    {
        var safeInstructions = agent.Instructions.Replace("\"\"\"", "\\\"\\\"\\\"", StringComparison.Ordinal);
        return string.Join(Environment.NewLine,
            $"name = {TomlQuote(NormalizeCodexName(agent.Name))}",
            $"description = {TomlQuote(agent.Description)}",
            "developer_instructions = \"\"\"",
            safeInstructions,
            "\"\"\"");
    }

    private static string? TomlValue(string text, string key)
    {
        var match = Regex.Match(text, $"(?m)^\\s*{Regex.Escape(key)}\\s*=\\s*\"(?<value>(?:\\\\.|[^\"])*)\"\\s*$");
        return match.Success ? Regex.Unescape(match.Groups["value"].Value) : null;
    }

    private static string? TripleQuotedValue(string text, string key)
    {
        var match = Regex.Match(text, $"(?s){Regex.Escape(key)}\\s*=\\s*\"\"\"(?<value>.*?)\"\"\"");
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static string? YamlValue(string frontmatter, string key)
    {
        var match = Regex.Match(frontmatter, $"(?m)^\\s*{Regex.Escape(key)}\\s*:\\s*(?<value>.+?)\\s*$");
        if (!match.Success) return null;
        var value = match.Groups["value"].Value.Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
        {
            try { return JsonSerializer.Deserialize<string>(value); }
            catch (JsonException) { return value.Trim('"'); }
        }
        return value.Trim('\'', '"');
    }

    private static string DefaultName(string path) => Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
    private static string NormalizeCodexName(string value) => InvalidNameRegex().Replace(value.Trim().ToLowerInvariant(), "_").Trim('_');
    private static string YamlQuote(string value) => JsonSerializer.Serialize(value);
    private static string TomlQuote(string value) => $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    private sealed record PortableAgent(string Name, string Description, string Instructions);

    [GeneratedRegex("[^a-z0-9_]+")]
    private static partial Regex InvalidNameRegex();
}
