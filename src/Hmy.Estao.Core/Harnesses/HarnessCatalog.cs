using Hmy.Estao.Core.Configuration;

namespace Hmy.Estao.Core.Harnesses;

public static class HarnessFeatureIds
{
    public const string Instructions = "instructions";
    public const string Skills = "skills";
    public const string Agents = "agents";
    public const string Prompts = "prompts";
    public const string Mcp = "mcp";
    public const string Hooks = "hooks";
    public const string Rules = "rules";
    public const string Plugins = "plugins";
    public const string Settings = "settings";

    public static readonly string[] All =
        [Instructions, Skills, Agents, Prompts, Mcp, Hooks, Rules, Plugins, Settings];

    public static readonly string[] Portable =
        [Instructions, Skills, Agents, Prompts, Mcp, Hooks, Settings];

    public static string DisplayName(string id) => id switch
    {
        Instructions => "Instructions",
        Skills => "Skills",
        Agents => "Agents",
        Prompts => "Prompts / commands",
        Mcp => "MCP servers",
        Hooks => "Hooks",
        Rules => "Command rules",
        Plugins => "Plugins",
        Settings => "Settings",
        _ => id
    };
}

public sealed record HarnessDefinition(
    string Id,
    string DisplayName,
    string Description,
    IReadOnlyList<string> SupportedFeatures);

public static class HarnessCatalog
{
    public static readonly HarnessDefinition[] All =
    [
        new("codex", "Codex", "OpenAI Codex CLI, IDE and desktop customizations.", HarnessFeatureIds.All),
        new("claude", "Claude Code", "Claude Code memory, skills, agents, commands and integrations.", HarnessFeatureIds.Portable),
        new("copilot", "GitHub Copilot", "Copilot CLI and repository customizations.", HarnessFeatureIds.Portable),
        new("opencode", "OpenCode", "OpenCode agents, skills, commands and configuration.", HarnessFeatureIds.Portable)
    ];

    public static string NormalizeId(string? id) => id?.Trim().ToLowerInvariant() switch
    {
        "github-copilot" or "githubcopilot" => "copilot",
        "claude-code" or "claudecode" => "claude",
        "open-code" => "opencode",
        var value => value ?? string.Empty
    };

    public static bool IsSupported(string? id) =>
        All.Any(item => string.Equals(item.Id, NormalizeId(id), StringComparison.Ordinal));

    public static HarnessDefinition Get(string id) =>
        All.First(item => string.Equals(item.Id, NormalizeId(id), StringComparison.Ordinal));

    public static HarnessProfileConfig CreateDefaultProfile(string id, string? userProfile = null)
    {
        var definition = Get(id);
        return new HarnessProfileConfig
        {
            Id = definition.Id,
            Enabled = true,
            Scope = "personal",
            BasePath = userProfile ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            // Raw settings may contain machine-specific paths or values that do not
            // belong in a team package. Keep the capability available, but opt in.
            Features = definition.SupportedFeatures
                .Where(feature => !string.Equals(feature, HarnessFeatureIds.Settings, StringComparison.Ordinal))
                .ToList()
        };
    }

    public static HarnessManagerConfig CreateDefaultManager(string? userProfile = null) => new()
    {
        SchemaVersion = 2,
        Author = Environment.UserName,
        Profiles = All.Select(item => CreateDefaultProfile(item.Id, userProfile)).ToList()
    };

    public static bool Supports(string harnessId, string featureId) =>
        IsSupported(harnessId) && Get(harnessId).SupportedFeatures.Contains(featureId, StringComparer.OrdinalIgnoreCase);
}
