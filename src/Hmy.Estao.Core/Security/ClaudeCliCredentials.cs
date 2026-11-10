using System.Text.Json;
using Hmy.Estao.Core.Platform;

namespace Hmy.Estao.Core.Security;

public static class ClaudeCliCredentials
{
    public static string PathName
    {
        get
        {
            var directory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            return Path.Combine(string.IsNullOrWhiteSpace(directory)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
                : Environment.ExpandEnvironmentVariables(directory), ".credentials.json");
        }
    }

    public static async Task<OAuthToken?> ReadAsync(CancellationToken ct, string? path = null, Func<bool>? installed = null)
    {
        if (!(installed?.Invoke() ?? CliExecutable.Find("claude") is not null)) return null;
        try
        {
            var file = path ?? PathName;
            if (!File.Exists(file)) return null;
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false));
            var root = json.RootElement;
            if (root.TryGetProperty("claudeAiOauth", out var subscription)) root = subscription;
            var access = root.TryGetProperty("accessToken", out var a) ? a.GetString()
                : root.TryGetProperty("access_token", out a) ? a.GetString() : null;
            if (string.IsNullOrWhiteSpace(access)) return null;
            DateTimeOffset? expires = root.TryGetProperty("expiresAt", out var e) && e.TryGetInt64(out var milliseconds)
                ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds) : null;
            if (expires <= DateTimeOffset.UtcNow) return null;
            // Read the CLI's current access token every time. Never copy or rotate
            // its refresh token, and never modify its credentials file.
            return new(access, null, ExpiresAt: expires);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentOutOfRangeException)
        { return null; }
    }
}
