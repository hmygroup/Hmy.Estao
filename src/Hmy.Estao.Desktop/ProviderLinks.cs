namespace Hmy.Estao.Desktop;

internal static class ProviderLinks
{
    // Match the destinations exposed by the original App popover. No arbitrary
    // URL supplied by web content is ever passed to the Windows shell.
    public static Uri Dashboard(string provider) => new(provider switch
    {
        "codex" => "https://platform.openai.com/usage",
        "claude" => "https://claude.ai/settings/usage",
        "copilot" => "https://github.com/settings/billing",
        "opencode" => "https://opencode.ai/",
        "zai" => "https://z.ai/manage-apikey/subscription",
        _ => throw new ArgumentException("Unknown provider.")
    });

    public static Uri Status(string provider) => new(provider switch
    {
        "claude" => "https://status.anthropic.com/",
        "copilot" => "https://www.githubstatus.com/",
        "codex" or "opencode" or "zai" => "https://status.openai.com/",
        _ => throw new ArgumentException("Unknown provider.")
    });
}
