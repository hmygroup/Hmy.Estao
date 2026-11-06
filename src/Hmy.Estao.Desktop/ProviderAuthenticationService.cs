using System.Text.Json;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Models;
using Hmy.Estao.Core.Providers;
using Hmy.Estao.Core.Security;

namespace Hmy.Estao.Desktop;

internal sealed class ProviderAuthenticationService(ICookieSecretStore cookies, IOAuthTokenStore oauth, UsageProviderFactory factory,
    string? claudeCredentialPath = null)
{
    internal static string ClaudeCredentialPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json");

    internal static void ValidateSource(string id, string source)
    {
        var supported = id switch
        {
            "codex" => source is "auto" or "cli" or "oauth",
            "claude" => source is "oauth" or "web",
            "copilot" => source is "oauth" or "api",
            "opencode" => source == "web",
            "zai" => source == "api",
            _ => false
        };
        if (!supported) throw new ArgumentException("Choose a supported sign-in method for this provider.");
    }

    public async Task<CodexAccountStatus> CheckAsync(ProviderConfig config, CancellationToken ct)
    {
        if (config.Id == "codex")
            return await CodexLoginService.ReadAccountAsync(CodexLoginService.ActiveProfile(config, factory.Create("codex").GetAccounts(config)), ct);
        if (!await HasCredentialsAsync(config, ct)) return new(false, "Not signed in");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            var provider = factory.Create(config.Id);
            var accounts = provider.GetAccounts(config);
            var index = config.ActiveAccountIndex ?? config.TokenAccounts?.ActiveIndex ?? 0;
            var account = index >= 0 && index < accounts.Count ? accounts[index] : accounts.FirstOrDefault();
            var source = Enum.TryParse<ProviderSource>(config.Source, true, out var parsed) ? parsed : ProviderSource.Auto;
            var snapshot = await provider.FetchAsync(new FetchRequest(config, account, source, timeout.Token));
            return FromSnapshot(snapshot);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(null, "Could not check sign-in. Try again when the provider is available."); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { return FromSnapshot(UsageSnapshot.Failure(config.Id, config.Source ?? "auto", exception.Message)); }
    }

    internal static CodexAccountStatus FromSnapshot(UsageSnapshot snapshot)
    {
        if (snapshot.Error is null) return new(true, "Signed in");
        var error = snapshot.Error;
        if (error.Contains("401", StringComparison.Ordinal) || error.Contains("403", StringComparison.Ordinal)
            || error.Contains("not connected", StringComparison.OrdinalIgnoreCase)
            || error.Contains("not signed in", StringComparison.OrdinalIgnoreCase)
            || error.Contains("credentials were not found", StringComparison.OrdinalIgnoreCase)
            || error.Contains("cookie was not saved", StringComparison.OrdinalIgnoreCase)
            || error.Contains("requires a saved", StringComparison.OrdinalIgnoreCase))
            return new(false, "Not signed in");
        return new(null, "Could not verify sign-in. Check the provider connection or credentials.");
    }

    private async Task<bool> HasCredentialsAsync(ProviderConfig config, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(config.ApiKey) || !string.IsNullOrWhiteSpace(config.CookieHeader)
            || config.TokenAccounts?.Accounts.Any(a => !string.IsNullOrWhiteSpace(a.Token)) == true) return true;
        if (config.Id == "copilot") return await oauth.ReadAsync("copilot", ct) is not null;
        if (config.Id is "claude" or "opencode" && !string.IsNullOrWhiteSpace(await cookies.ReadCookieHeaderAsync(config.Id, ct))) return true;
        var path = claudeCredentialPath ?? ClaudeCredentialPath;
        if (config.Id == "claude" && File.Exists(path))
        {
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
            return ContainsAccessToken(document.RootElement);
        }
        return false;
    }

    private static bool ContainsAccessToken(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        return value.EnumerateObject().Any(p => p.Name is "access_token" or "accessToken"
            ? p.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(p.Value.GetString()) : ContainsAccessToken(p.Value));
    }

    internal static void ClearConfiguredCredentials(ProviderConfig provider)
    {
        var codexIndex = provider.ActiveAccountIndex ?? provider.TokenAccounts?.ActiveIndex ?? 0;
        provider.ApiKey = null;
        provider.CookieHeader = null;
        provider.TokenAccounts = null;
        provider.ActiveAccountIndex = provider.Id == "codex" ? codexIndex : null;
    }
}
