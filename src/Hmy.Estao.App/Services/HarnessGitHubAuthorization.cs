using System.Diagnostics;
using Hmy.Estao.Core.Harnesses.Installer;
using Hmy.Estao.Core.Security;

namespace Hmy.Estao.App.Services;

internal sealed class HarnessGitHubAuthorization
{
    private readonly SecureOAuthTokenStore _store;
    private readonly SecureOAuthTokenStore _copilotStore;
    private readonly string _directory;
    public HarnessGitHubAuthorization(string dataDirectory)
    {
        _directory = Path.Combine(dataDirectory, "harness-github");
        InstallerPaths.CheckLocal(_directory);
        _store = new(Path.Combine(_directory, "oauth-secrets.json"));
        _copilotStore = new(Path.Combine(dataDirectory, "oauth-secrets.json"));
    }

    public async Task<IReadOnlyList<string>> CandidatesAsync(CancellationToken ct)
    {
        var candidates = new List<string>();
        var saved = await ReadOptionalAsync(_store, "github-harness", ct).ConfigureAwait(false);
        if (saved is not null) candidates.Add(saved.AccessToken);
        var configured = Environment.GetEnvironmentVariable("HMY_ESTAO_GITHUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(configured)) candidates.Add(configured);
        // Optional interoperability, never a dependency of repository retrieval. Tokens never go into arguments or logs.
        var cliToken = await TryGitHubCliAsync(ct).ConfigureAwait(false);
        if (cliToken is not null) candidates.Add(cliToken);
        var copilot = await ReadOptionalAsync(_copilotStore, "copilot", ct).ConfigureAwait(false);
        if (copilot is not null) candidates.Add(copilot.AccessToken);
        return candidates.Distinct(StringComparer.Ordinal).ToList();
    }

    private static async Task<OAuthToken?> ReadOptionalAsync(IOAuthTokenStore store, string key, CancellationToken ct)
    {
        try { return await store.ReadAsync(key, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException or FormatException)
        { return null; } // A stale/corrupt optional credential must not hide another valid authorization. Never delete it.
    }

    public Task SaveAsync(string token, CancellationToken ct)
    {
        InstallerPaths.EnsurePrivateDirectory(_directory);
        InstallerPaths.CheckLocal(Path.Combine(_directory, "oauth-secrets.json"));
        return _store.SaveAsync("github-harness", new OAuthToken(token, "Harness Hub"), ct);
    }

    private static async Task<string?> TryGitHubCliAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        Process? process = null;
        try
        {
            process = Process.Start(new ProcessStartInfo("gh", "auth token --hostname github.com")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
            });
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            _ = await error.ConfigureAwait(false);
            var token = (await output.ConfigureAwait(false)).Trim();
            return process.ExitCode == 0 && token.Length is > 10 and < 1024 ? token : null;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (System.ComponentModel.Win32Exception) { return null; }
        finally
        {
            if (process is not null)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                process.Dispose();
            }
        }
    }
}
