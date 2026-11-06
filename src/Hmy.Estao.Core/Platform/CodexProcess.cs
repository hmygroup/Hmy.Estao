using System.Diagnostics;
using System.Text;

namespace Hmy.Estao.Core.Platform;

/// <summary>Shared launcher for Codex usage, account checks, and browser login.</summary>
public static class CodexProcess
{
    public static string FindExecutable() => FindIn(SearchDirectories(), OperatingSystem.IsWindows())
        ?? throw new FileNotFoundException("Codex CLI could not be found. Install or update Codex, then restart Estao.");

    internal static string? FindIn(IEnumerable<string> directories, bool windows)
    {
        foreach (var directory in directories.Where(p => !string.IsNullOrWhiteSpace(p)))
        foreach (var extension in windows ? new[] { ".exe", ".cmd", ".bat" } : new[] { "" })
        {
            var candidate = Path.Combine(directory.Trim().Trim('"'), "codex" + extension);
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        }
        return null;
    }

    private static IEnumerable<string> SearchDirectories()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Concat(new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm"),
            Path.Combine(home, ".local", "bin"), Path.Combine(home, ".bun", "bin"),
            Path.Combine(local, "Microsoft", "WinGet", "Links"), Path.Combine(local, "Programs", "OpenAI", "Codex", "bin") });
    }

    public static ProcessStartInfo CreateStartInfo(string executable, string profileHome, IReadOnlyList<string> arguments)
    {
        var extension = Path.GetExtension(executable);
        var wrapper = extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
        var info = new ProcessStartInfo
        {
            FileName = wrapper ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe" : executable,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        if (wrapper)
        {
            // Only fixed, trusted command arguments are accepted for shell wrappers.
            if (executable.IndexOfAny(['"', '%', '!', '\r', '\n']) >= 0
                || arguments.Any(arg => arg.Length == 0 || arg.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or ':' or '/'))))
                throw new InvalidOperationException("Codex's launcher path or arguments cannot be safely executed. Install Codex in a standard location.");
            info.Arguments = $"/d /s /c \"\"{executable}\" {string.Join(' ', arguments)}\"";
        }
        else foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["CODEX_HOME"] = Path.GetFullPath(profileHome);
        return info;
    }

    public static async Task<T> RunAsync<T>(ProcessStartInfo startInfo,
        Func<TextReader, TextWriter, CancellationToken, Task<T>> exchange, CancellationToken ct)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var process = new Process { StartInfo = startInfo };
        ct.ThrowIfCancellationRequested();
        if (!process.Start()) throw new InvalidOperationException("Could not start Codex. Try updating Codex CLI.");
        var stderr = DrainAsync(process.StandardError, lifetime.Token);
        try { return await exchange(process.StandardOutput, process.StandardInput, ct).ConfigureAwait(false); }
        finally
        {
            lifetime.Cancel();
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(shutdown.Token).ConfigureAwait(false);
            }
            catch (InvalidOperationException) { }
            catch (OperationCanceledException) { }
            try { await stderr.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (IOException) { }
        }
    }

    private static async Task DrainAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[1024];
        while (await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false) > 0) { }
    }
}
