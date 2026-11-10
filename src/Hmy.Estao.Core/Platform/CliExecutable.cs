using System.Diagnostics;

namespace Hmy.Estao.Core.Platform;

public static class CliExecutable
{
    public static string? Find(string name)
    {
        if (name is not ("claude" or "gh")) return null;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Concat([
            Path.Combine(home, ".local", "bin"), Path.Combine(home, ".bun", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
            Path.Combine(local, "Microsoft", "WinGet", "Links"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI")]);
        foreach (var raw in paths)
        {
            var directory = raw.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory)) continue;
            foreach (var extension in OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat" } : new[] { "" })
            {
                var path = Path.Combine(directory, name + extension);
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }

    public static async Task<string?> ReadAsync(string executable, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        // Reuse the existing validated shell-wrapper handling.
        var info = CodexProcess.CreateStartInfo(executable, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), arguments, setCodexHome: false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return await CodexProcess.RunAsync(info, async (output, _, token) =>
        {
            var text = new System.Text.StringBuilder();
            var buffer = new char[2048];
            int read;
            while ((read = await output.ReadAsync(buffer.AsMemory(), token).ConfigureAwait(false)) > 0)
            {
                if (text.Length + read > 64 * 1024) return null;
                text.Append(buffer, 0, read);
            }
            return text.ToString();
        }, timeout.Token).ConfigureAwait(false);
    }
}
