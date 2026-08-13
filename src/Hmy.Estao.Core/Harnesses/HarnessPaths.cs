using Hmy.Estao.Core.Configuration;

namespace Hmy.Estao.Core.Harnesses;

public static class HarnessPaths
{
    public static string ResolveBasePath(HarnessProfileConfig profile)
    {
        var value = Environment.ExpandEnvironmentVariables(profile.BasePath.Trim());
        if (value == "~") return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
            return Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), value[2..]));
        return Path.GetFullPath(value);
    }

    public static string PrimaryInstructions(HarnessProfileConfig profile)
    {
        var root = ResolveBasePath(profile);
        var personal = IsPersonal(profile);
        return profile.Id switch
        {
            "codex" => personal ? Path.Combine(root, ".codex", "AGENTS.md") : Path.Combine(root, "AGENTS.md"),
            "claude" => personal ? Path.Combine(root, ".claude", "CLAUDE.md") : Path.Combine(root, "CLAUDE.md"),
            "copilot" => personal
                ? Path.Combine(root, ".copilot", "copilot-instructions.md")
                : Path.Combine(root, ".github", "copilot-instructions.md"),
            "opencode" => personal
                ? Path.Combine(root, ".config", "opencode", "AGENTS.md")
                : Path.Combine(root, "AGENTS.md"),
            _ => throw Unsupported(profile)
        };
    }

    public static string? AdditionalInstructions(HarnessProfileConfig profile)
    {
        var root = ResolveBasePath(profile);
        var personal = IsPersonal(profile);
        return profile.Id switch
        {
            "copilot" => personal
                ? Path.Combine(root, ".copilot", "instructions")
                : Path.Combine(root, ".github", "instructions"),
            "claude" => personal
                ? Path.Combine(root, ".claude", "rules")
                : Path.Combine(root, ".claude", "rules"),
            _ => null
        };
    }

    public static string FeatureDirectory(HarnessProfileConfig profile, string feature)
    {
        var root = ResolveBasePath(profile);
        var personal = IsPersonal(profile);
        return (profile.Id, feature) switch
        {
            ("codex", HarnessFeatureIds.Skills) => Path.Combine(root, ".agents", "skills"),
            ("codex", HarnessFeatureIds.Agents) => Path.Combine(root, ".codex", "agents"),
            ("codex", HarnessFeatureIds.Prompts) => Path.Combine(root, ".codex", "prompts"),
            ("codex", HarnessFeatureIds.Hooks) => Path.Combine(root, ".codex"),
            ("codex", HarnessFeatureIds.Rules) => Path.Combine(root, ".codex", "rules"),
            ("codex", HarnessFeatureIds.Plugins) => Path.Combine(root, ".codex"),

            ("claude", HarnessFeatureIds.Skills) => Path.Combine(root, ".claude", "skills"),
            ("claude", HarnessFeatureIds.Agents) => Path.Combine(root, ".claude", "agents"),
            ("claude", HarnessFeatureIds.Prompts) => Path.Combine(root, ".claude", "commands"),
            ("claude", HarnessFeatureIds.Hooks) => Path.Combine(root, ".claude"),

            ("copilot", HarnessFeatureIds.Skills) => Path.Combine(root, personal ? ".copilot" : ".github", "skills"),
            ("copilot", HarnessFeatureIds.Agents) => Path.Combine(root, personal ? ".copilot" : ".github", "agents"),
            ("copilot", HarnessFeatureIds.Prompts) => Path.Combine(root, personal ? ".copilot" : ".github", "prompts"),
            ("copilot", HarnessFeatureIds.Hooks) => Path.Combine(root, personal ? ".copilot" : ".github", "hooks"),

            ("opencode", HarnessFeatureIds.Skills) => OpenCodeDirectory(root, personal, "skills"),
            ("opencode", HarnessFeatureIds.Agents) => OpenCodeDirectory(root, personal, "agents"),
            ("opencode", HarnessFeatureIds.Prompts) => OpenCodeDirectory(root, personal, "commands"),
            ("opencode", HarnessFeatureIds.Hooks) => OpenCodeDirectory(root, personal, "plugins"),
            _ => throw new InvalidOperationException($"Feature '{feature}' has no directory for harness '{profile.Id}'.")
        };
    }

    public static string McpConfiguration(HarnessProfileConfig profile)
    {
        var root = ResolveBasePath(profile);
        var personal = IsPersonal(profile);
        return profile.Id switch
        {
            "codex" => Path.Combine(root, ".codex", "config.toml"),
            "claude" => personal ? Path.Combine(root, ".claude.json") : Path.Combine(root, ".mcp.json"),
            "copilot" => personal
                ? Path.Combine(root, ".copilot", "mcp-config.json")
                : Path.Combine(root, ".mcp.json"),
            "opencode" => OpenCodeConfig(root, personal),
            _ => throw Unsupported(profile)
        };
    }

    public static IReadOnlyList<string> SettingsFiles(HarnessProfileConfig profile)
    {
        var root = ResolveBasePath(profile);
        var personal = IsPersonal(profile);
        return profile.Id switch
        {
            "codex" => CodexSettingsFiles(root),
            "claude" => personal
                ? [Path.Combine(root, ".claude", "settings.json")]
                : [Path.Combine(root, ".claude", "settings.json"), Path.Combine(root, ".claude", "settings.local.json")],
            "copilot" => personal
                ? [Path.Combine(root, ".copilot", "settings.json")]
                : [Path.Combine(root, ".github", "copilot", "settings.json"), Path.Combine(root, ".github", "copilot", "settings.local.json")],
            "opencode" => [OpenCodeConfig(root, personal)],
            _ => throw Unsupported(profile)
        };
    }

    public static string SettingsTarget(HarnessProfileConfig profile, string logicalPath)
    {
        if (profile.Id == "codex")
            return Path.Combine(ResolveBasePath(profile), ".codex", Path.GetFileName(logicalPath));
        return SettingsFiles(profile).FirstOrDefault(path => string.Equals(Path.GetFileName(path), Path.GetFileName(logicalPath), StringComparison.OrdinalIgnoreCase))
            ?? SettingsFiles(profile)[0];
    }

    public static string CodexLegacySkills(HarnessProfileConfig profile) =>
        Path.Combine(ResolveBasePath(profile), ".codex", "skills");

    private static IReadOnlyList<string> CodexSettingsFiles(string root)
    {
        var directory = Path.Combine(root, ".codex");
        var files = new List<string> { Path.Combine(directory, "config.toml") };
        if (!Directory.Exists(directory)) return files;
        files.AddRange(Directory.EnumerateFiles(directory, "*.config.toml", SearchOption.TopDirectoryOnly));
        var requirements = Path.Combine(directory, "requirements.toml");
        if (File.Exists(requirements)) files.Add(requirements);
        return files.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsPersonal(HarnessProfileConfig profile) =>
        !string.Equals(profile.Scope, "project", StringComparison.OrdinalIgnoreCase);

    private static string OpenCodeDirectory(string root, bool personal, string name) => personal
        ? Path.Combine(root, ".config", "opencode", name)
        : Path.Combine(root, ".opencode", name);

    private static string OpenCodeConfig(string root, bool personal) => personal
        ? Path.Combine(root, ".config", "opencode", "opencode.json")
        : Path.Combine(root, "opencode.json");

    private static Exception Unsupported(HarnessProfileConfig profile) =>
        new InvalidOperationException($"Harness '{profile.Id}' is not supported.");
}
