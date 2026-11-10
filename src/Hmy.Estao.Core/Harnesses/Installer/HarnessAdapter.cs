using System.Text;
using System.Text.Json;
using Tomlyn;
using YamlDotNet.RepresentationModel;
using Parser = YamlDotNet.Core.Parser;
using YamlDotNet.Core.Events;

namespace Hmy.Estao.Core.Harnesses.Installer;

public sealed class HarnessAdapter
{
    public const string AdapterVersion = "1";
    public static readonly string[] Ids = ["codex", "claude", "copilot", "opencode"];
    public string Id { get; }
    public string DisplayName => Id switch { "codex" => "OpenAI Codex", "claude" => "Claude Code", "copilot" => "GitHub Copilot CLI", _ => "OpenCode" };
    public string Profile { get; }
    public string SkillsRoot { get; }
    public string AgentsRoot { get; }
    public string McpPath { get; }
    public IReadOnlyList<string> CompatibleSkillsRoots { get; }
    public Version? InstalledVersion { get; }
    private readonly bool _configurationConflict;

    public HarnessAdapter(string id, string? home = null, Func<string, string?>? environment = null, Version? installedVersion = null)
    {
        if (!Ids.Contains(id)) throw new ArgumentException("Unsupported harness.", nameof(id));
        Id = id;
        InstalledVersion = installedVersion;
        home ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        environment ??= Environment.GetEnvironmentVariable;
        string Resolve(string? value, string fallback)
        {
            var path = string.IsNullOrWhiteSpace(value) ? fallback : value;
            if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Profile overrides must be absolute local paths.");
            InstallerPaths.CheckLocal(path);
            return Path.GetFullPath(path);
        }
        Profile = id switch
        {
            "codex" => Resolve(environment("CODEX_HOME"), Path.Combine(home, ".codex")),
            "claude" => Resolve(environment("CLAUDE_CONFIG_DIR"), Path.Combine(home, ".claude")),
            "copilot" => Resolve(environment("COPILOT_HOME"), Path.Combine(home, ".copilot")),
            _ => Resolve(environment("OPENCODE_CONFIG_DIR"), Path.Combine(Resolve(environment("XDG_CONFIG_HOME"), Path.Combine(home, ".config")), "opencode"))
        };
        _configurationConflict = id == "opencode" && !string.IsNullOrWhiteSpace(environment("OPENCODE_CONFIG_CONTENT"));
        SkillsRoot = id == "codex" ? Path.Combine(home, ".agents", "skills") : Path.Combine(Profile, "skills");
        CompatibleSkillsRoots = id switch
        {
            "copilot" => [Path.Combine(home, ".agents", "skills")],
            "opencode" => [Path.Combine(home, ".agents", "skills"), Path.Combine(home, ".claude", "skills")],
            "codex" => [Path.Combine(Profile, "skills")],
            _ => []
        };
        AgentsRoot = Path.Combine(Profile, "agents");
        McpPath = id switch
        {
            "codex" => Path.Combine(Profile, "config.toml"),
            "claude" => Path.Combine(Path.GetDirectoryName(Profile)!, Path.GetFileName(Profile) + ".json"),
            "copilot" => Path.Combine(Profile, "mcp-config.json"),
            _ => Resolve(environment("OPENCODE_CONFIG"), Path.Combine(Profile, "opencode.json"))
        };
        if (id == "opencode" && string.IsNullOrWhiteSpace(environment("OPENCODE_CONFIG")))
        {
            var jsonc = Path.Combine(Profile, "opencode.jsonc");
            if (File.Exists(jsonc))
            {
                if (File.Exists(McpPath)) _configurationConflict = true;
                McpPath = jsonc;
            }
        }
        foreach (var path in new[] { Profile, SkillsRoot, AgentsRoot, McpPath }) InstallerPaths.CheckLocal(path);
    }

    public void CheckCapabilities(SourceArtifact artifact)
    {
        if (artifact.Category == "mcp")
            throw new InvalidDataException("MCP endpoints require individual approval. The reviewed source contains no user-global MCP definitions; none are enabled automatically.");
        if (artifact.Category == "mcp" && _configurationConflict)
            throw new InvalidDataException("Inline or ambiguous OpenCode JSON/JSONC configuration requires review before MCP installation.");
        if (artifact.MinimumVersions.TryGetValue(Id, out var minimum) &&
            (InstalledVersion is null || InstalledVersion < Version.Parse(minimum)))
            throw new InvalidDataException($"{DisplayName} requires verified version {minimum} or newer for {artifact.Id}.");
        if (Id == "opencode" && artifact.Category is "agent" or "mcp" && (InstalledVersion is null || InstalledVersion.Major < 2))
            throw new InvalidDataException("OpenCode agent/MCP installation requires a verified V2 version.");
        if (Id == "copilot" && artifact.Category == "command")
            throw new InvalidDataException("Copilot CLI does not use VS Code prompt files as native CLI commands.");
        if (Id == "codex" && artifact.Category == "command")
            throw new InvalidDataException("Codex commands require an explicitly validated skill conversion.");
    }

    public IReadOnlyList<PreparedFile> Prepare(SourceArtifact artifact, IReadOnlyList<(SourceEntry Entry, byte[] Bytes)> payloads)
    {
        CheckCapabilities(artifact);
        if (artifact.Category == "skill")
        {
            var entry = payloads.SingleOrDefault(p => p.Entry.Path == artifact.Source + "/SKILL.md");
            if (entry.Entry is null) throw new InvalidDataException("Skill has no SKILL.md.");
            var yaml = ReadFrontmatter(entry.Bytes);
            if (!yaml.Children.TryGetValue(new YamlScalarNode("name"), out var name) || name is not YamlScalarNode nameValue ||
                nameValue.Value != artifact.Id || !yaml.Children.TryGetValue(new YamlScalarNode("description"), out var description) ||
                description is not YamlScalarNode descriptionValue || string.IsNullOrWhiteSpace(descriptionValue.Value) || descriptionValue.Value.Length > 1024)
                throw new InvalidDataException($"Invalid skill identity: {artifact.Id}.");
            return payloads.Select(p => new PreparedFile(artifact.Id,
                Path.Combine(SkillsRoot, InstallerPaths.Relative(artifact.Id + "/" + p.Entry.Path[(artifact.Source.Length + 1)..])), p.Bytes)).ToArray();
        }
        if (payloads.Count != 1 || payloads[0].Entry.Path != artifact.Source) throw new InvalidDataException("Native configurations must declare a single file.");
        var bytes = payloads[0].Bytes;
        if (artifact.Category == "mcp")
        {
            if (Id == "codex")
            {
                var text = Encoding.UTF8.GetString(bytes);
                if (Toml.Parse(text).HasErrors) throw new InvalidDataException("Invalid native TOML.");
                if (Toml.ToModel(text).Keys.Any(k => k != "mcp_servers")) throw new InvalidDataException("MCP artifacts cannot change unrelated TOML settings.");
            }
            else
            {
                StructuredConfiguration.Validate(bytes);
                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                var configuration = document.RootElement;
                var key = Id == "opencode" ? "mcp" : "mcpServers";
                if (configuration.EnumerateObject().Any(p => p.Name != key) || !configuration.TryGetProperty(key, out var servers) || servers.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("MCP artifacts cannot change unrelated JSON settings.");
                if (Id == "opencode")
                {
                    if (servers.EnumerateObject().Any(p => p.Name != "servers") || !servers.TryGetProperty("servers", out servers))
                        throw new InvalidDataException("OpenCode V2 requires mcp.servers.");
                }
                foreach (var server in servers.EnumerateObject())
                {
                    if (server.Value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid MCP server.");
                    var type = server.Value.TryGetProperty("type", out var transport) ? transport.GetString() : "stdio";
                    var local = Id == "opencode" ? type == "local" : type is "stdio" or "local";
                    var remote = Id == "opencode" ? type == "remote" : type is "http" or "sse";
                    if (!local && !remote || local && (!server.Value.TryGetProperty("command", out var command) ||
                        command.ValueKind != (Id == "opencode" ? JsonValueKind.Array : JsonValueKind.String)) ||
                        remote && (!server.Value.TryGetProperty("url", out var url) || !Uri.TryCreate(url.GetString(), UriKind.Absolute, out _)))
                        throw new InvalidDataException("Unsupported native MCP transport.");
                }
            }
            return [new(artifact.Id, McpPath, bytes, Id == "codex" ? "toml" : "jsonc")];
        }
        if (artifact.Category == "agent" && Id == "codex")
        {
            var text = Encoding.UTF8.GetString(bytes);
            var syntax = Toml.Parse(text);
            if (syntax.HasErrors) throw new InvalidDataException("Invalid native Codex agent TOML.");
            var model = Toml.ToModel(text);
            if (new[] { "name", "description", "developer_instructions" }.Any(k => !model.ContainsKey(k)))
                throw new InvalidDataException("Codex agent requires name, description and developer_instructions.");
            if (model.Keys.Any(k => k is not ("name" or "description" or "developer_instructions")))
                throw new InvalidDataException("Agent policy/model overrides require a separately reviewed adapter.");
        }
        else
        {
            var yaml = ReadFrontmatter(bytes);
            if (artifact.Category == "agent")
            {
                var allowed = new[] { "name", "description", "tools", "model", "mode", "permissions", "strict-tools-list" };
                if (yaml.Children.Keys.Any(k => !allowed.Contains(k.ToString()))) throw new InvalidDataException("Unsupported agent policy field.");
                if (yaml.Children.TryGetValue(new YamlScalarNode("model"), out var model) && model.ToString() != "inherit")
                    throw new InvalidDataException("Automatic model overrides are not supported.");
                if (Id == "opencode")
                {
                    if (!yaml.Children.TryGetValue(new YamlScalarNode("permissions"), out var permissions) || permissions is not YamlSequenceNode rules || rules.Children.Count == 0)
                        throw new InvalidDataException("OpenCode agents require explicit restricted permissions.");
                    var first = rules.Children[0] as YamlMappingNode;
                    if (first is null || first[new YamlScalarNode("action")].ToString() != "*" || first[new YamlScalarNode("effect")].ToString() != "deny")
                        throw new InvalidDataException("OpenCode agent must deny tools by default.");
                    foreach (var rule in rules.Children.Skip(1).Cast<YamlMappingNode>())
                        if (rule[new YamlScalarNode("effect")].ToString() != "deny" && rule[new YamlScalarNode("action")].ToString() is not ("read" or "glob" or "grep"))
                            throw new InvalidDataException("Agent write/execute permission cannot be installed automatically.");
                }
                else if (!yaml.Children.TryGetValue(new YamlScalarNode("tools"), out var tools) ||
                    tools.ToString().Split([',', ' ', '[', ']', '"', '\'', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Any(t => t.ToLowerInvariant() is not ("read" or "glob" or "grep")))
                    throw new InvalidDataException("Agents require an explicit read-only tools list.");
            }
            if (Encoding.UTF8.GetString(bytes).Contains("!`", StringComparison.Ordinal))
                throw new InvalidDataException("Command shell interpolation is not installed automatically.");
        }
        var root = artifact.Category == "agent" ? AgentsRoot : Path.Combine(Profile, "commands");
        var extension = artifact.Category == "agent" && Id == "codex" ? ".toml" :
            artifact.Category == "agent" && Id == "copilot" ? ".agent.md" : ".md";
        return [new(artifact.Id, InstallerPaths.Within(root, artifact.Id + extension), bytes)];
    }

    public static string SkillDescription(byte[] bytes)
        => SkillMetadata(bytes).Description;

    public static (string Name, string Description) SkillMetadata(byte[] bytes)
    {
        try
        {
            var yaml = ReadFrontmatter(bytes);
            var name = yaml.Children.TryGetValue(new YamlScalarNode("name"), out var nameNode) && nameNode is YamlScalarNode nameScalar
                ? nameScalar.Value ?? string.Empty : string.Empty;
            var descriptionText = yaml.Children.TryGetValue(new YamlScalarNode("description"), out var description)
                && description is YamlScalarNode scalar ? scalar.Value ?? string.Empty : string.Empty;
            return (name, descriptionText);
        }
        catch (Exception ex) when (ex is YamlDotNet.Core.YamlException or InvalidOperationException)
        { throw new InvalidDataException("Invalid YAML skill metadata.", ex); }
    }

    public bool IsAllowedDestination(string path)
    {
        path = Path.GetFullPath(path);
        return path.Equals(Path.GetFullPath(McpPath), StringComparison.OrdinalIgnoreCase) ||
            new[] { SkillsRoot, AgentsRoot, Path.Combine(Profile, "commands") }.Any(root =>
                path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
    }

    public bool IsObservedDestination(string path) => IsAllowedDestination(path) || CompatibleSkillsRoots.Any(root =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<string> ShadowPaths(string path)
    {
        var relative = Path.GetRelativePath(SkillsRoot, path).Replace('\\', '/');
        if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return [];
        var id = relative.Split('/')[0];
        return CompatibleSkillsRoots.Where(root => File.Exists(InstallerPaths.Within(root, id + "/SKILL.md")))
            .Select(root => InstallerPaths.Within(root, relative)).ToArray();
    }

    private static YamlMappingNode ReadFrontmatter(byte[] bytes)
    {
        var text = new UTF8Encoding(false, true).GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal);
        if (!text.StartsWith("---\n", StringComparison.Ordinal)) throw new InvalidDataException("Missing YAML frontmatter.");
        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0 || end > 32768) throw new InvalidDataException("Invalid YAML frontmatter.");
        var parser = new Parser(new StringReader(text[4..end]));
        var depth = 0;
        while (parser.MoveNext())
        {
            if (parser.Current is AnchorAlias) throw new InvalidDataException("YAML aliases are not supported in installer metadata.");
            if (parser.Current is MappingStart or SequenceStart && ++depth > 32) throw new InvalidDataException("YAML metadata is too deeply nested.");
            if (parser.Current is MappingEnd or SequenceEnd) depth--;
        }
        var stream = new YamlStream();
        stream.Load(new StringReader(text[4..end]));
        if (stream.Documents.Count != 1 || stream.Documents[0].RootNode is not YamlMappingNode map)
            throw new InvalidDataException("Expected a single YAML mapping.");
        return map;
    }
}
