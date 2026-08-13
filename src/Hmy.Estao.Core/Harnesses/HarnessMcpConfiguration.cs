using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Hmy.Estao.Core.Configuration;

namespace Hmy.Estao.Core.Harnesses;

internal sealed record PortableMcpReadResult(PortableMcpConfiguration Configuration, bool Redacted);

internal static partial class HarnessMcpConfiguration
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static async Task<PortableMcpReadResult?> ReadAsync(
        HarnessProfileConfig profile, CancellationToken cancellationToken = default)
    {
        var path = HarnessPaths.McpConfiguration(profile);
        if (!File.Exists(path)) return null;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException($"Refusing to publish reparse-point MCP file '{path}'.");
        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var configuration = string.Equals(profile.Id, "codex", StringComparison.Ordinal)
            ? ParseCodexToml(text)
            : ParseJson(profile.Id, text);
        if (configuration.Servers.Count == 0) return null;
        var redacted = RedactSecrets(configuration);
        return new PortableMcpReadResult(configuration, redacted);
    }

    public static async Task<IReadOnlyList<string>> MergeAsync(
        HarnessProfileConfig target,
        PortableMcpConfiguration portable,
        CancellationToken cancellationToken = default)
    {
        var path = HarnessPaths.McpConfiguration(target);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        if (string.Equals(target.Id, "codex", StringComparison.Ordinal))
        {
            var existing = File.Exists(path)
                ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)
                : string.Empty;
            await File.WriteAllTextAsync(path, MergeCodexToml(existing, portable), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            var root = await ReadJsonObjectAsync(path, cancellationToken).ConfigureAwait(false);
            MergeJson(root, target.Id, portable);
            await File.WriteAllTextAsync(path, root.ToJsonString(JsonOptions) + Environment.NewLine, cancellationToken)
                .ConfigureAwait(false);
        }

        return [path];
    }

    private static PortableMcpConfiguration ParseJson(string harnessId, string text)
    {
        var root = JsonNode.Parse(text, documentOptions: DocumentOptions) as JsonObject ?? [];
        JsonObject? servers = null;
        if (string.Equals(harnessId, "opencode", StringComparison.Ordinal))
        {
            var mcp = root["mcp"] as JsonObject;
            servers = mcp?["servers"] as JsonObject ?? mcp;
        }
        else
        {
            servers = root["mcpServers"] as JsonObject ?? root["servers"] as JsonObject;
        }

        var result = new PortableMcpConfiguration();
        if (servers is null) return result;
        foreach (var pair in servers)
        {
            if (pair.Value is not JsonObject source) continue;
            var server = new PortableMcpServer
            {
                Type = Text(source, "type") ?? (source["url"] is null ? "stdio" : "http"),
                Url = Text(source, "url"),
                Enabled = Bool(source, "enabled") ?? !(Bool(source, "disabled") ?? false)
            };
            if (source["command"] is JsonArray commandArray)
            {
                var command = commandArray.Select(ValueText).Where(value => value is not null).Cast<string>().ToList();
                server.Command = command.FirstOrDefault();
                server.Args.AddRange(command.Skip(1));
            }
            else
            {
                server.Command = Text(source, "command");
            }

            server.Args.AddRange(StringArray(source["args"]));
            ReadMap(source["env"] as JsonObject ?? source["environment"] as JsonObject, server.Environment);
            ReadMap(source["headers"] as JsonObject ?? source["httpHeaders"] as JsonObject, server.Headers);
            server.Tools.AddRange(StringArray(source["tools"] ?? source["enabledTools"]));
            result.Servers[pair.Key] = server;
        }
        return result;
    }

    private static PortableMcpConfiguration ParseCodexToml(string text)
    {
        var result = new PortableMcpConfiguration();
        string? serverName = null;
        string? subsection = null;
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("[", StringComparison.Ordinal))
            {
                var match = McpSectionRegex().Match(line);
                if (!match.Success)
                {
                    serverName = null;
                    subsection = null;
                    continue;
                }
                serverName = match.Groups["name"].Value.Trim('"', '\'');
                subsection = match.Groups["sub"].Value.TrimStart('.');
                result.Servers.TryAdd(serverName, new PortableMcpServer());
                continue;
            }
            if (serverName is null || !result.Servers.TryGetValue(serverName, out var server)) continue;
            var equals = line.IndexOf('=');
            if (equals <= 0) continue;
            var key = line[..equals].Trim().Trim('"');
            var value = line[(equals + 1)..].Trim();
            if (subsection is "env") server.Environment[key] = TomlString(value);
            else if (subsection is "http_headers" or "env_http_headers") server.Headers[key] = TomlString(value);
            else switch (key)
            {
                case "command": server.Command = TomlString(value); break;
                case "url": server.Url = TomlString(value); server.Type = "http"; break;
                case "args": server.Args.AddRange(TomlArray(value)); break;
                case "enabled": server.Enabled = !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase); break;
                case "enabled_tools": server.Tools.AddRange(TomlArray(value)); break;
                case "env": ReadInlineTomlMap(value, server.Environment); break;
                case "http_headers": ReadInlineTomlMap(value, server.Headers); break;
            }
        }
        return result;
    }

    private static void MergeJson(JsonObject root, string harnessId, PortableMcpConfiguration portable)
    {
        JsonObject servers;
        if (string.Equals(harnessId, "opencode", StringComparison.Ordinal))
        {
            var mcp = root["mcp"] as JsonObject ?? [];
            root["mcp"] = mcp;
            servers = mcp["servers"] as JsonObject ?? [];
            mcp["servers"] = servers;
        }
        else
        {
            servers = root["mcpServers"] as JsonObject ?? [];
            root["mcpServers"] = servers;
        }

        foreach (var pair in portable.Servers)
            servers[pair.Key] = ServerJson(harnessId, pair.Value);
    }

    private static JsonObject ServerJson(string harnessId, PortableMcpServer server)
    {
        var result = new JsonObject();
        var remote = !string.IsNullOrWhiteSpace(server.Url);
        result["type"] = harnessId switch
        {
            "copilot" when !remote => "local",
            _ when remote => "http",
            _ => "stdio"
        };
        if (remote)
        {
            result["url"] = server.Url;
        }
        else if (string.Equals(harnessId, "opencode", StringComparison.Ordinal))
        {
            result["command"] = new JsonArray(new[] { server.Command }.Concat(server.Args)
                .Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => JsonValue.Create(value)).ToArray());
        }
        else
        {
            result["command"] = server.Command;
            if (server.Args.Count > 0) result["args"] = JsonSerializer.SerializeToNode(server.Args, JsonOptions);
        }
        if (server.Environment.Count > 0)
            result[string.Equals(harnessId, "opencode", StringComparison.Ordinal) ? "environment" : "env"] =
                JsonSerializer.SerializeToNode(server.Environment, JsonOptions);
        if (server.Headers.Count > 0) result["headers"] = JsonSerializer.SerializeToNode(server.Headers, JsonOptions);
        if (server.Tools.Count > 0 && !string.Equals(harnessId, "opencode", StringComparison.Ordinal))
            result["tools"] = JsonSerializer.SerializeToNode(server.Tools, JsonOptions);
        if (string.Equals(harnessId, "opencode", StringComparison.Ordinal)) result["disabled"] = !server.Enabled;
        else if (!string.Equals(harnessId, "copilot", StringComparison.Ordinal)) result["enabled"] = server.Enabled;
        return result;
    }

    private static string MergeCodexToml(string existing, PortableMcpConfiguration portable)
    {
        var names = portable.Servers.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var output = new List<string>();
        var skip = false;
        foreach (var line in existing.Split(['\r', '\n'], StringSplitOptions.None))
        {
            if (line.TrimStart().StartsWith("[", StringComparison.Ordinal))
            {
                var match = McpSectionRegex().Match(line.Trim());
                skip = match.Success && names.Contains(match.Groups["name"].Value.Trim('"', '\''));
            }
            if (!skip) output.Add(line);
        }
        while (output.Count > 0 && string.IsNullOrWhiteSpace(output[^1])) output.RemoveAt(output.Count - 1);
        foreach (var pair in portable.Servers)
        {
            if (output.Count > 0) output.Add(string.Empty);
            output.Add($"[mcp_servers.{TomlKey(pair.Key)}]");
            var server = pair.Value;
            if (!string.IsNullOrWhiteSpace(server.Url)) output.Add($"url = {TomlQuote(server.Url)}");
            else
            {
                output.Add($"command = {TomlQuote(server.Command ?? string.Empty)}");
                if (server.Args.Count > 0) output.Add($"args = [{string.Join(", ", server.Args.Select(TomlQuote))}]");
            }
            output.Add($"enabled = {server.Enabled.ToString().ToLowerInvariant()}");
            if (server.Tools.Count > 0) output.Add($"enabled_tools = [{string.Join(", ", server.Tools.Select(TomlQuote))}]");
            if (server.Environment.Count > 0)
            {
                output.Add(string.Empty);
                output.Add($"[mcp_servers.{TomlKey(pair.Key)}.env]");
                output.AddRange(server.Environment.Select(value => $"{TomlKey(value.Key)} = {TomlQuote(value.Value)}"));
            }
            if (server.Headers.Count > 0)
            {
                output.Add(string.Empty);
                output.Add($"[mcp_servers.{TomlKey(pair.Key)}.http_headers]");
                output.AddRange(server.Headers.Select(value => $"{TomlKey(value.Key)} = {TomlQuote(value.Value)}"));
            }
        }
        return string.Join(Environment.NewLine, output) + Environment.NewLine;
    }

    private static bool RedactSecrets(PortableMcpConfiguration configuration)
    {
        var changed = false;
        foreach (var server in configuration.Servers.Values)
        {
            changed |= RedactMap(server.Environment);
            changed |= RedactMap(server.Headers);
        }
        return changed;
    }

    private static bool RedactMap(Dictionary<string, string> values)
    {
        var changed = false;
        foreach (var key in values.Keys.ToList())
        {
            if (!SecretNameRegex().IsMatch(key) || IsEnvironmentReference(values[key])) continue;
            values[key] = $"${{ENV:{NormalizeEnvironmentName(key)}}}";
            changed = true;
        }
        return changed;
    }

    private static bool IsEnvironmentReference(string value) =>
        value.Contains("${", StringComparison.Ordinal) || value.Contains("{env:", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeEnvironmentName(string key) =>
        Regex.Replace(key.ToUpperInvariant(), "[^A-Z0-9_]", "_");

    private static async Task<JsonObject> ReadJsonObjectAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return [];
        var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        return JsonNode.Parse(text, documentOptions: DocumentOptions) as JsonObject ?? [];
    }

    private static void ReadMap(JsonObject? source, Dictionary<string, string> target)
    {
        if (source is null) return;
        foreach (var pair in source)
        {
            var value = ValueText(pair.Value);
            if (value is not null) target[pair.Key] = value;
        }
    }

    private static void ReadInlineTomlMap(string value, Dictionary<string, string> target)
    {
        var body = value.Trim().TrimStart('{').TrimEnd('}');
        foreach (var pair in body.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals > 0) target[pair[..equals].Trim().Trim('"')] = TomlString(pair[(equals + 1)..].Trim());
        }
    }

    private static string? Text(JsonObject source, string name) => ValueText(source[name]);
    private static bool? Bool(JsonObject source, string name) => source[name]?.GetValueKind() is JsonValueKind.True or JsonValueKind.False
        ? source[name]!.GetValue<bool>()
        : null;
    private static string? ValueText(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static IEnumerable<string> StringArray(JsonNode? node) => node is JsonArray array
        ? array.Select(ValueText).Where(value => value is not null).Cast<string>()
        : [];
    private static string TomlString(string value) => value.Trim().Trim('"', '\'');
    private static IEnumerable<string> TomlArray(string value) => value.Trim().TrimStart('[').TrimEnd(']')
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(TomlString);
    private static string TomlQuote(string value) => $"\"{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    private static string TomlKey(string value) => Regex.IsMatch(value, "^[A-Za-z0-9_-]+$") ? value : TomlQuote(value);

    [GeneratedRegex("^\\[{1,2}mcp_servers\\.(?<name>\\\"[^\\\"]+\\\"|'[^']+'|[^.\\]]+)(?<sub>\\.[^\\]]+)?\\]{1,2}$", RegexOptions.IgnoreCase)]
    private static partial Regex McpSectionRegex();

    [GeneratedRegex("token|secret|password|passwd|api[-_]?key|authorization|credential|private[-_]?key|cookie", RegexOptions.IgnoreCase)]
    private static partial Regex SecretNameRegex();
}
