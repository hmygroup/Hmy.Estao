using System.Text.Json;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Models;
using Hmy.Estao.Core.Platform;
using Hmy.Estao.Core.Providers;
using Hmy.Estao.Core.Security;

namespace Hmy.Estao.Desktop;

internal sealed record ProviderCliSession(string Source, OAuthToken? Token = null);

internal sealed class ProviderCliSessionService(UsageProviderFactory factory,
    Func<string, string?>? locate = null,
    Func<string, IReadOnlyList<string>, CancellationToken, Task<string?>>? read = null,
    Func<CancellationToken, Task<OAuthToken?>>? claudeCredentials = null,
    Func<ProviderConfig, CancellationToken, Task<CodexAccountStatus>>? codexStatus = null)
{
    public async Task<ProviderCliSession?> TryReadAsync(ProviderConfig provider, CancellationToken ct)
    {
        if (provider.CliAutoSignInDisabled) return null;
        try
        {
            switch (provider.Id)
            {
                case "codex":
                    var status = codexStatus is not null ? await codexStatus(provider, ct)
                        : await CodexLoginService.ReadAccountAsync(CodexLoginService.ActiveProfile(provider, factory.Create("codex").GetAccounts(provider)), ct);
                    return status.SignedIn == true ? new("cli") : null;
                case "claude":
                    var claude = (locate ?? CliExecutable.Find)("claude");
                    if (claude is null) return null;
                    // auth status already returns JSON; --json is not a supported
                    // flag on all Claude versions and can make detection fail.
                    var output = await (read ?? CliExecutable.ReadAsync)(claude, ["auth", "status"], ct);
                    if (output is null) return null;
                    using (var json = JsonDocument.Parse(output))
                    {
                        if (!json.RootElement.TryGetProperty("loggedIn", out var loggedIn) || loggedIn.ValueKind != JsonValueKind.True) return null;
                        if (json.RootElement.TryGetProperty("authMethod", out var method)
                            && method.GetString() is not ("claude.ai" or "oauth_token")) return null;
                    }
                    return await (claudeCredentials ?? (c => ClaudeCliCredentials.ReadAsync(c)))(ct) is not null ? new("auto") : null;
                case "copilot":
                    if (!string.IsNullOrWhiteSpace(provider.EnterpriseHost) && !provider.EnterpriseHost.Equals("github.com", StringComparison.OrdinalIgnoreCase)) return null;
                    var gh = (locate ?? CliExecutable.Find)("gh");
                    if (gh is null) return null;
                    var githubToken = (await (read ?? CliExecutable.ReadAsync)(gh, ["auth", "token"], ct))?.Trim();
                    if (string.IsNullOrWhiteSpace(githubToken) || githubToken.Any(char.IsControl)) return null;
                    var configured = new ProviderConfig { Id = "copilot", Source = "api", ApiKey = githubToken };
                    var snapshot = await factory.Create("copilot").FetchAsync(new(configured, null, ProviderSource.Api, ct));
                    return snapshot.Error is null ? new("oauth", new(githubToken, snapshot.Account)) : null;
                default:
                    // No compatible CLI usage session for OpenCode web/Z.ai API.
                    return null;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return null; } // CLI absence/failure is a normal OAuth fallback.
    }
}
