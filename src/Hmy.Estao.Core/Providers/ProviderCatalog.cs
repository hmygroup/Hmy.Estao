namespace Hmy.Estao.Core.Configuration;

public static class ProviderCatalog
{
    public static readonly string[] InitialProviderIds = ["codex", "claude", "copilot", "opencode", "zai"];

    public static string NormalizeId(string id) => id.Trim().ToLowerInvariant() switch
    {
        "githubcopilot" or "github-copilot" => "copilot",
        "open-code" => "opencode",
        "z-ai" or "z.ai" or "zai-coding-plan" or "zaicodingplan" or "glm" or "glm-coding-plan" or "zhipu" or "zhipuai" => "zai",
        var normalized => normalized
    };

    public static bool IsSupported(string id) => InitialProviderIds.Contains(NormalizeId(id), StringComparer.Ordinal);

    public static string DisplayName(string id) => NormalizeId(id) switch
    {
        "codex" => "Codex",
        "claude" => "Claude",
        "copilot" => "GitHub Copilot",
        "opencode" => "OpenCode",
        "zai" => "Z.ai",
        _ => id
    };
}
