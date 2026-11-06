using Hmy.Estao.Core.Harnesses.Installer;

namespace Hmy.Estao.Desktop.HarnessHub;

internal static class HubTools
{
    public static string Website(string id) => id switch
    {
        "codex" => "https://developers.openai.com/codex/cli/",
        "claude" => "https://code.claude.com/docs/en/setup",
        "copilot" => "https://docs.github.com/en/copilot/how-tos/set-up/install-copilot-cli",
        "opencode" => "https://opencode.ai/",
        _ => throw new ArgumentException("Unknown harness.")
    };

    public static bool IsDetected(string id)
    {
        if (!HarnessAdapter.Ids.Contains(id)) throw new ArgumentException("Unknown harness.");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var locations = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Concat(new[] { Path.Combine(roaming, "npm"), Path.Combine(home, ".local", "bin"),
                Path.Combine(home, ".bun", "bin"), Path.Combine(home, ".opencode", "bin"),
                Path.Combine(local, "Microsoft", "WinGet", "Links"), Path.Combine(local, "Programs", id) });
        return DetectIn(id, locations);
    }

    internal static bool DetectIn(string id, IEnumerable<string> directories) => directories
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Select(path => path.Trim().Trim('"'))
        .Any(path => new[] { ".exe", ".cmd", ".ps1", ".bat" }.Any(ext => File.Exists(Path.Combine(path, id + ext))));
}
