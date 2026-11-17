using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hmy.Estao.Core.Harnesses.Installer;
using Tomlyn;
using Tomlyn.Model;

namespace Hmy.Estao.Desktop.HarnessHub;

// Edits a single native server, never round-tripping through the portable
// publishing model (which deliberately redacts credentials).
internal sealed class HubMcpConfiguration
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly HarnessAdapter _adapter;
    private readonly JsonObject? _json;
    private readonly TomlTable? _toml;
    public byte[] Before { get; }
    public string? BeforeHash { get; }
    public string Path => _adapter.McpPath;

    private HubMcpConfiguration(HarnessAdapter adapter, byte[] before, string? hash)
    {
        _adapter = adapter;
        Before = before;
        BeforeHash = hash;
        if (adapter.Id == "codex")
        {
            var text = Encoding.UTF8.GetString(before);
            if (Toml.Parse(text).HasErrors) throw new InvalidDataException("Invalid MCP TOML configuration.");
            _toml = Toml.ToModel(text);
        }
        else
        {
            var bytes = before.Length == 0 ? "{}"u8.ToArray() : before;
            StructuredConfiguration.Validate(bytes);
            _json = (JsonObject)JsonNode.Parse(bytes, documentOptions: new()
                { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })!;
        }
    }

    public static async Task<HubMcpConfiguration> ReadAsync(HarnessAdapter adapter, CancellationToken ct)
    {
        adapter.CheckMcpConfiguration();
        var hash = InstallerPaths.FileHash(adapter.McpPath);
        var bytes = hash is null ? [] : await File.ReadAllBytesAsync(adapter.McpPath, ct);
        if (hash is not null && InstallerPaths.Hash(bytes) != hash) throw new IOException("MCP configuration changed while reading.");
        return new(adapter, bytes, hash);
    }

    private JsonObject JsonServers(bool create)
    {
        JsonObject Child(JsonObject parent, string key)
        {
            if (parent[key] is JsonObject value) return value;
            if (parent.ContainsKey(key)) throw new InvalidDataException("Invalid MCP configuration object.");
            var result = new JsonObject();
            if (create) parent[key] = result;
            return result;
        }
        if (_adapter.Id == "opencode")
        {
            var mcp = Child(_json!, "mcp");
            // Respect an existing legacy map; new V2 configurations use servers.
            if (mcp.Count > 0 && !mcp.ContainsKey("servers") && mcp.Any(pair =>
                    pair.Value is JsonObject obj && obj.ContainsKey("command"))) return mcp;
            return Child(mcp, "servers");
        }
        var key = _json!.ContainsKey("servers") && !_json.ContainsKey("mcpServers") ? "servers" : "mcpServers";
        return Child(_json, key);
    }

    private TomlTable TomlServers(bool create)
    {
        if (_toml!.TryGetValue("mcp_servers", out var value))
            return value as TomlTable ?? throw new InvalidDataException("Invalid MCP TOML table.");
        var result = new TomlTable();
        if (create) _toml["mcp_servers"] = result;
        return result;
    }

    public string? Executable(string name)
    {
        name = ServerName(name);
        if (_toml is not null)
            return TomlServers(false).TryGetValue(name, out var value) && value is TomlTable server &&
                server.TryGetValue("command", out var command) ? command as string : null;
        var node = JsonServers(false)[name] as JsonObject;
        return node?["command"] is JsonArray array ? array.FirstOrDefault()?.GetValue<string>() : node?["command"]?.GetValue<string>();
    }

    public IEnumerable<string> Executables()
    {
        var names = _toml is not null ? TomlServers(false).Keys : JsonServers(false).Select(pair => pair.Key);
        return names.Select(name => PayloadExecutable(name, strict: false)).OfType<string>();
    }

    public string? PayloadExecutable(string name, bool strict = true)
    {
        name = ServerName(name);
        var command = Executable(name);
        if (command is null) return null; // Remote MCP: configuration only.
        var stem = System.IO.Path.GetFileNameWithoutExtension(command);
        if (!new[] { "dotnet", "node", "python", "python3", "cmd", "powershell", "pwsh", "npx", "npm", "uv", "uvx", "bash", "sh" }
            .Contains(stem, StringComparer.OrdinalIgnoreCase)) return command;
        IEnumerable<string> args;
        if (_toml is not null)
            args = TomlServers(false)[name] is TomlTable server && server.TryGetValue("args", out var value) && value is TomlArray array
                ? array.OfType<string>() : [];
        else
        {
            var server = (JsonObject)JsonServers(false)[name]!;
            args = server["command"] is JsonArray commandArray ? commandArray.Skip(1).Select(node => node!.GetValue<string>())
                : server["args"] is JsonArray array ? array.Select(node => node!.GetValue<string>()) : [];
        }
        var payload = args.FirstOrDefault(arg => System.IO.Path.IsPathFullyQualified(arg) &&
            new[] { ".exe", ".dll", ".js", ".py" }.Contains(System.IO.Path.GetExtension(arg), StringComparer.OrdinalIgnoreCase));
        if (payload is null && strict) throw new InvalidDataException("Cannot identify the MCP application files behind its runtime or shell command. Use an explicit application path in the MCP configuration.");
        return payload;
    }

    public void Install(string name, string executable)
    {
        name = ServerName(name);
        var arguments = ExistingArguments(name);
        if (_toml is not null)
        {
            var servers = TomlServers(true);
            var server = servers.TryGetValue(name, out var old) && old is TomlTable table ? table : new TomlTable();
            foreach (var key in new[] { "url", "http_headers", "bearer_token_env_var" }) server.Remove(key);
            server["command"] = executable;
            var args = new TomlArray();
            foreach (var argument in arguments) args.Add(argument);
            server["args"] = args;
            server["enabled"] = true;
            var environment = server.TryGetValue("env", out var env) && env is TomlTable map ? map : new TomlTable();
            environment["DOTNET_CONTENTROOT"] = System.IO.Path.GetDirectoryName(executable)!;
            server["env"] = environment;
            servers[name] = server;
        }
        else
        {
            var servers = JsonServers(true);
            var server = servers[name] as JsonObject ?? new JsonObject();
            var args = arguments.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray();
            foreach (var key in new[] { "url", "headers", "httpHeaders", "oauth", "args", "command", "disabled", "enabled" }) server.Remove(key);
            server["type"] = _adapter.Id is "opencode" or "copilot" ? "local" : "stdio";
            if (_adapter.Id == "opencode")
            {
                server["command"] = new JsonArray(new[] { JsonValue.Create(executable) }.Concat(args).ToArray());
                server["cwd"] = System.IO.Path.GetDirectoryName(executable)!;
            }
            else
            {
                server["command"] = executable;
                if (args.Length > 0) server["args"] = new JsonArray(args);
            }
            var environmentKey = _adapter.Id == "opencode" ? "environment" : "env";
            var environment = server[environmentKey] as JsonObject ?? new JsonObject();
            environment["DOTNET_CONTENTROOT"] = System.IO.Path.GetDirectoryName(executable)!;
            if (server[environmentKey] is null) server[environmentKey] = environment;
            // Keep existing environment, tool selection and timeouts on updates.
            if (servers[name] is null) servers[name] = server;
        }
    }

    private string[] ExistingArguments(string name)
    {
        var command = Executable(name);
        if (command is null) return [];
        string[] args;
        if (_toml is not null)
            args = TomlServers(false)[name] is TomlTable server && server.TryGetValue("args", out var value) && value is TomlArray array
                ? array.OfType<string>().ToArray() : [];
        else
        {
            var server = (JsonObject)JsonServers(false)[name]!;
            args = server["command"] is JsonArray commandArray ? commandArray.Skip(1).Select(node => node!.GetValue<string>()).ToArray()
                : server["args"] is JsonArray array ? array.Select(node => node!.GetValue<string>()).ToArray() : [];
        }
        var stem = System.IO.Path.GetFileNameWithoutExtension(command);
        if (!new[] { "dotnet", "node", "python", "python3", "cmd", "powershell", "pwsh", "npx", "npm", "uv", "uvx", "bash", "sh" }
            .Contains(stem, StringComparer.OrdinalIgnoreCase)) return args;
        var payload = PayloadExecutable(name, strict: false);
        var index = payload is null ? -1 : Array.IndexOf(args, payload);
        return index < 0 ? [] : args[(index + 1)..];
    }

    public void Remove(string name)
    {
        name = ServerName(name);
        if (_toml is not null) TomlServers(false).Remove(name);
        else JsonServers(false).Remove(name);
    }

    private string ServerName(string name)
    {
        var names = _toml is not null ? TomlServers(false).Keys : JsonServers(false).Select(pair => pair.Key);
        var matches = names.Where(key => key.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length > 1) throw new InvalidDataException("Multiple MCP server names differ only by case. Resolve the ambiguity before managing this MCP.");
        return matches.SingleOrDefault() ?? name;
    }

    public byte[] Serialize() => Encoding.UTF8.GetBytes(_toml is not null
        ? Toml.FromModel(_toml) : _json!.ToJsonString(JsonOptions) + Environment.NewLine);

    public async Task WriteAsync(string dataDirectory, CancellationToken ct)
    {
        var backup = System.IO.Path.Combine(dataDirectory, "network-backups", Guid.NewGuid().ToString("N"));
        InstallerPaths.EnsurePrivateDirectory(backup);
        if (BeforeHash is not null) await File.WriteAllBytesAsync(System.IO.Path.Combine(backup, System.IO.Path.GetFileName(_adapter.McpPath)), Before, ct);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_adapter.McpPath)!);
        await HubFileExporter.WriteAsync(_adapter.McpPath, Serialize(), BeforeHash, ct);
    }
}
