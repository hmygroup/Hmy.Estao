using System.Diagnostics;
using Hmy.Estao.Core.Models;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hmy.Estao.App;

internal static class OAuthLoginService
{
    // GitHub's public OAuth client used by VS Code. This is the same device flow
    // used by CodexBar for Copilot; it never asks the user for an API key.
    private const string CopilotOAuthClientId = "Iv1.b507a08c87ecfe98";
    // Public GitHub App client ID for Harness Hub user authorization via Device Flow.
    // App permissions and repository access are configured in GitHub, not as OAuth scopes.
    internal const string HarnessOAuthClientId = "Iv23lilqu6xECmxSGO99";
    private static readonly HttpClient HttpClient = new();

    public static void StartCodexLogin() => StartInteractiveCli("codex", "codex login", "Codex CLI");

    public static void StartClaudeLogin() => StartInteractiveCli("claude", "claude login", "Claude Code CLI");

    public static async Task<CopilotOAuthLogin> SignInToCopilotAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default,
        Action<string>? userCodeReceived = null, Action<UserMessage>? messageReceived = null)
    {
        progress?.Report("Requesting a GitHub device code…");
        messageReceived?.Invoke(new("signin.request_code"));
        var deviceCode = await RequestCopilotDeviceCodeAsync(CopilotOAuthClientId, "read:user", cancellationToken).ConfigureAwait(false);
        var verificationUrl = deviceCode.VerificationUriComplete ?? deviceCode.VerificationUri;
        userCodeReceived?.Invoke(deviceCode.UserCode);
        progress?.Report($"Enter code {deviceCode.UserCode} in GitHub. Waiting for confirmation…");
        messageReceived?.Invoke(new("signin.wait_code", deviceCode.UserCode));

        try
        {
            Process.Start(new ProcessStartInfo { FileName = verificationUrl, UseShellExecute = true });
        }
        catch (Exception exception)
        {
            progress?.Report($"Open {verificationUrl} and enter code {deviceCode.UserCode}. ({exception.Message})");
            messageReceived?.Invoke(new("signin.open_browser_manually"));
        }

        var token = await PollCopilotTokenAsync(deviceCode, CopilotOAuthClientId, cancellationToken).ConfigureAwait(false);
        progress?.Report("GitHub confirmed the sign-in. Verifying the Copilot account…");
        messageReceived?.Invoke(new("signin.verifying"));
        var account = await TryGetGitHubLoginAsync(token, cancellationToken).ConfigureAwait(false);
        return new CopilotOAuthLogin(token, account);
    }

    public static async Task<CopilotOAuthLogin> SignInToGitHubAppAsync(string clientId, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId)) throw new InvalidOperationException("La GitHub App de Estao no está configurada.");
        var deviceCode = await RequestCopilotDeviceCodeAsync(clientId, null, cancellationToken).ConfigureAwait(false);
        progress?.Report($"Abre {deviceCode.VerificationUri} e introduce {deviceCode.UserCode}.");
        Process.Start(new ProcessStartInfo { FileName = "https://github.com/login/device", UseShellExecute = true });
        var token = await PollCopilotTokenAsync(deviceCode, clientId, cancellationToken).ConfigureAwait(false);
        return new CopilotOAuthLogin(token, await TryGetGitHubLoginAsync(token, cancellationToken).ConfigureAwait(false));
    }

    internal static async Task<GitHubDeviceAuthorization> StartHarnessDeviceFlowAsync(CancellationToken cancellationToken, HttpClient? client = null)
    {
        var code = await RequestCopilotDeviceCodeAsync(HarnessOAuthClientId, null, cancellationToken, client).ConfigureAwait(false);
        if (code.VerificationUri != "https://github.com/login/device" || string.IsNullOrWhiteSpace(code.DeviceCode) ||
            string.IsNullOrWhiteSpace(code.UserCode) || code.ExpiresIn <= 0)
            throw new InvalidOperationException("GitHub devolvió datos de autorización no válidos.");
        return new GitHubDeviceAuthorization(code.DeviceCode, code.UserCode, code.VerificationUri,
            code.Interval, DateTimeOffset.UtcNow.AddSeconds(code.ExpiresIn));
    }

    internal static Task<string> CompleteHarnessDeviceFlowAsync(GitHubDeviceAuthorization authorization, CancellationToken cancellationToken, HttpClient? client = null) =>
        PollCopilotTokenAsync(new CopilotDeviceCode
        {
            DeviceCode = authorization.DeviceCode,
            UserCode = authorization.UserCode,
            VerificationUri = authorization.VerificationUri,
            Interval = authorization.Interval,
            ExpiresIn = Math.Max(1, (int)(authorization.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds)
        }, HarnessOAuthClientId, cancellationToken, client);

    private static async Task<CopilotDeviceCode> RequestCopilotDeviceCodeAsync(string clientId, string? scope, CancellationToken cancellationToken, HttpClient? client = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/device/code")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["scope"] = scope ?? string.Empty
            })
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await (client ?? HttpClient).SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<CopilotDeviceCode>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return result ?? throw new InvalidOperationException("GitHub did not return a device code.").WithUserMessage("signin.github_failed");
    }

    private static async Task<string> PollCopilotTokenAsync(CopilotDeviceCode deviceCode, string clientId, CancellationToken cancellationToken, HttpClient? client = null)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(deviceCode.ExpiresIn);
        var delay = Math.Max(1, deviceCode.Interval);
        while (DateTimeOffset.UtcNow < expiresAt)
        {
            await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken).ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://github.com/login/oauth/access_token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["device_code"] = deviceCode.DeviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
                })
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await (client ?? HttpClient).SendAsync(request, cancellationToken).ConfigureAwait(false);
            var result = await response.Content.ReadFromJsonAsync<CopilotTokenResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(result?.AccessToken))
            {
                return result.AccessToken;
            }

            switch (result?.Error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    delay += 5;
                    continue;
                case "expired_token":
                    throw new TimeoutException("The GitHub device code expired. Start sign-in again.").WithUserMessage("signin.code_expired");
                default:
                    throw new InvalidOperationException(result?.ErrorDescription ?? result?.Error ?? "GitHub did not issue an OAuth token.")
                        .WithUserMessage(result?.Error == "access_denied" ? "signin.authorization_denied" : "signin.github_failed");
            }
        }

        throw new TimeoutException("The GitHub device code expired. Start sign-in again.").WithUserMessage("signin.code_expired");
    }

    private static async Task<string?> TryGetGitHubLoginAsync(string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("token", token);
        request.Headers.UserAgent.ParseAdd("Hmy.Estao");
        using var response = await HttpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var user = await response.Content.ReadFromJsonAsync<GitHubUser>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return user?.Login;
    }

    private static void StartInteractiveCli(string executable, string command, string displayName)
    {
        if (!IsAvailable(executable))
        {
            throw new InvalidOperationException($"{displayName} is not installed or is not on PATH. Install it first, then try OAuth sign-in again.");
        }

        var commandShell = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
        Process.Start(new ProcessStartInfo
        {
            FileName = commandShell,
            Arguments = $"/k {command}",
            UseShellExecute = true,
            WorkingDirectory = Environment.CurrentDirectory
        });
    }

    private static bool IsAvailable(string executable)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "where.exe",
            Arguments = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        });
        process?.WaitForExit();
        return process?.ExitCode == 0;
    }

    private sealed class CopilotDeviceCode
    {
        [JsonPropertyName("device_code")]
        public string DeviceCode { get; init; } = string.Empty;

        [JsonPropertyName("user_code")]
        public string UserCode { get; init; } = string.Empty;

        [JsonPropertyName("verification_uri")]
        public string VerificationUri { get; init; } = string.Empty;

        [JsonPropertyName("verification_uri_complete")]
        public string? VerificationUriComplete { get; init; }

        [JsonPropertyName("expires_in")]
        public int ExpiresIn { get; init; }

        [JsonPropertyName("interval")]
        public int Interval { get; init; } = 5;
    }

    private sealed class CopilotTokenResponse
    {
        [JsonPropertyName("access_token")]
        public string? AccessToken { get; init; }

        [JsonPropertyName("error")]
        public string? Error { get; init; }

        [JsonPropertyName("error_description")]
        public string? ErrorDescription { get; init; }
    }

    private sealed class GitHubUser
    {
        [JsonPropertyName("login")]
        public string? Login { get; init; }
    }
}

internal sealed record CopilotOAuthLogin(string AccessToken, string? AccountLabel);
internal sealed record GitHubDeviceAuthorization(string DeviceCode, string UserCode, string VerificationUri, int Interval, DateTimeOffset ExpiresAt);
