using System.Diagnostics;
using System.Text.Json;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Models;
using Hmy.Estao.Core.Platform;

namespace Hmy.Estao.Desktop;

internal static class CodexLoginService
{
    public static Task SignOutAsync(string profileHome, CancellationToken ct)
    {
        if (!Directory.Exists(profileHome)) return Task.CompletedTask;
        return RunServerAsync(CreateStartInfo(FindExecutable(), profileHome), SignOutExchangeAsync,
            TimeSpan.FromSeconds(20), "Codex sign-out timed out. Try again.", ct);
    }

    internal static async Task<bool> SignOutExchangeAsync(TextReader output, TextWriter input, CancellationToken ct)
    {
        await Send(new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "estao", version = "0.0.9" } } });
        var stage = 1;
        while (await output.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.Length > 1024 * 1024) throw new InvalidOperationException("Codex returned an oversized sign-out response.");
            JsonDocument doc;
            try { doc = JsonDocument.Parse(line); } catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var requestId) || requestId != stage) continue;
                if (root.TryGetProperty("error", out _)) throw new InvalidOperationException("Codex could not sign out. Update Codex CLI and try again.");
                if (!root.TryGetProperty("result", out _)) continue;
                if (stage == 2) return true;
                stage = 2;
                await Send(new { method = "initialized", @params = new { } });
                await Send(new { id = 2, method = "account/logout", @params = new { } });
            }
        }
        throw new InvalidOperationException("Codex closed before confirming sign-out.");
        async Task Send<T>(T message)
        {
            await input.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), ct).ConfigureAwait(false);
            await input.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    public static Task<CodexAccountStatus> ReadAccountAsync(string profileHome, CancellationToken ct)
    {
        if (!Directory.Exists(profileHome)) return Task.FromResult(new CodexAccountStatus(false, "Not signed in to this Codex profile."));
        return RunServerAsync(CreateStartInfo(FindExecutable(), profileHome), ReadAccountExchangeAsync,
            TimeSpan.FromSeconds(20), "Checking Codex sign-in timed out. Try Check sign-in again.", ct);
    }

    public static Task SignInAsync(string profileHome, Action<Uri> openBrowser, IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startInfo = CreateStartInfo(FindExecutable(), profileHome);
        Directory.CreateDirectory(profileHome);
        return RunAsync(startInfo, openBrowser, progress, cancellationToken);
    }

    internal static string ActiveProfile(ProviderConfig config, IReadOnlyList<ProviderAccount> accounts)
    {
        var index = config.ActiveAccountIndex ?? config.TokenAccounts?.ActiveIndex ?? 0;
        var account = index >= 0 && index < accounts.Count ? accounts[index] : accounts.FirstOrDefault();
        return Path.GetFullPath(account?.HomePath ?? Environment.GetEnvironmentVariable("CODEX_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"));
    }

    internal static ProcessStartInfo CreateStartInfo(string executable, string profileHome)
        => CodexProcess.CreateStartInfo(executable, profileHome, ["app-server"]);

    private static string FindExecutable()
        => CodexProcess.FindExecutable();

    internal static Task RunAsync(ProcessStartInfo startInfo, Action<Uri> openBrowser, IProgress<string>? progress,
        CancellationToken cancellationToken)
        => RunServerAsync(startInfo, async (output, input, ct) =>
        {
            progress?.Report("Starting Codex browser sign-in…");
            await ExchangeAsync(output, input, openBrowser, progress, ct).ConfigureAwait(false);
            return true;
        }, TimeSpan.FromMinutes(5), "Codex sign-in timed out. Start sign-in again.", cancellationToken);

    private static async Task<T> RunServerAsync<T>(ProcessStartInfo startInfo,
        Func<TextReader, TextWriter, CancellationToken, Task<T>> exchange, TimeSpan duration, string timeoutMessage,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(duration);
        try
        {
            return await CodexProcess.RunAsync(startInfo, exchange, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException(timeoutMessage); }
    }

    internal static async Task<CodexAccountStatus> ReadAccountExchangeAsync(TextReader output, TextWriter input, CancellationToken ct)
    {
        await SendAsync(new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "estao", version = "0.0.9" } } });
        var initialized = false;
        while (await output.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (line.Length > 1024 * 1024) throw new InvalidOperationException("Codex returned an oversized account response.");
            JsonDocument document;
            try { document = JsonDocument.Parse(line); }
            catch (JsonException) { continue; }
            using (document)
            {
                var message = document.RootElement;
                if (message.ValueKind != JsonValueKind.Object || !message.TryGetProperty("id", out var id)
                    || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var requestId) || requestId is not (1 or 2)) continue;
                if (message.TryGetProperty("error", out _))
                    throw new InvalidOperationException("Could not check the Codex account. Update Codex CLI or try Check sign-in again.");
                if (!message.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) continue;
                if (requestId == 1 && !initialized)
                {
                    initialized = true;
                    await SendAsync(new { method = "initialized", @params = new { } });
                    await SendAsync(new { id = 2, method = "account/read", @params = new { refreshToken = false } });
                }
                else if (requestId == 2 && initialized)
                {
                    if (!result.TryGetProperty("account", out var account)) break;
                    if (account.ValueKind == JsonValueKind.Null) return new(false, "Not signed in to this Codex profile.");
                    if (account.ValueKind != JsonValueKind.Object) break;
                    return String(account, "type") switch
                    {
                        "chatgpt" => new(true, "Signed in with ChatGPT"),
                        "apiKey" => new(true, "API key configured; ChatGPT usage requires a ChatGPT sign-in."),
                        _ => new(null, "Codex uses a different authentication method.")
                    };
                }
            }
        }
        throw new InvalidOperationException("Codex did not return an account status. Try Check sign-in again.");

        async Task SendAsync<T>(T message)
        {
            await input.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), ct).ConfigureAwait(false);
            await input.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    internal static async Task ExchangeAsync(TextReader output, TextWriter input, Action<Uri> openBrowser,
        IProgress<string>? progress, CancellationToken ct)
    {
        await SendAsync(new { id = 1, method = "initialize", @params = new { clientInfo = new { name = "estao", title = "Estao", version = "0.0.9" } } });
        string? loginId = null;
        var initialized = false;
        var opened = false;
        try
        {
            while (await output.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                if (line.Length > 1024 * 1024) throw new InvalidOperationException("Codex returned an oversized sign-in response.");
                JsonDocument document;
                try { document = JsonDocument.Parse(line); }
                catch (JsonException) { continue; } // Some launchers print a startup banner.
                using (document)
                {
                    var message = document.RootElement;
                    if (message.ValueKind != JsonValueKind.Object) continue;
                    if (message.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.Number && id.TryGetInt32(out var requestId))
                    {
                        if (requestId is not (1 or 2)) continue;
                        if (message.TryGetProperty("error", out _))
                            throw new InvalidOperationException("Codex could not start browser sign-in. Update the Codex CLI and try again.");
                        if (!message.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object) continue;
                        if (requestId == 1 && !initialized)
                        {
                            initialized = true;
                            await SendAsync(new { method = "initialized", @params = new { } });
                            await SendAsync(new { id = 2, method = "account/login/start", @params = new { type = "chatgpt" } });
                        }
                        else if (requestId == 2 && initialized && !opened)
                        {
                            if (String(result, "type") != "chatgpt" || string.IsNullOrWhiteSpace(String(result, "loginId")))
                                throw new InvalidOperationException("This Codex version did not provide browser sign-in. Update the Codex CLI.");
                            loginId = String(result, "loginId");
                            var uri = AuthorizationUri(String(result, "authUrl"));
                            try { openBrowser(uri); }
                            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
                            { throw new InvalidOperationException("Could not open the default browser. Check your browser settings, then start sign-in again."); }
                            opened = true;
                            progress?.Report("Complete sign-in in your browser. Estao will detect when you finish.");
                        }
                    }
                    else if (String(message, "method") == "account/login/completed" && loginId is not null
                        && message.TryGetProperty("params", out var details) && details.ValueKind == JsonValueKind.Object
                        && String(details, "loginId") == loginId)
                    {
                        if (!details.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True)
                            throw new InvalidOperationException("Codex browser sign-in failed or was cancelled. Start sign-in again.");
                        return;
                    }
                }
            }
            throw new InvalidOperationException("Codex closed before sign-in completed. Update the Codex CLI or try again.");
        }
        catch (OperationCanceledException)
        {
            if (loginId is not null)
            {
                using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try
                {
                    await input.WriteLineAsync(JsonSerializer.Serialize(new { id = 3, method = "account/login/cancel", @params = new { loginId } }).AsMemory(), cancel.Token).ConfigureAwait(false);
                    await input.FlushAsync(cancel.Token).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException) { }
            }
            throw;
        }

        async Task SendAsync<T>(T message)
        {
            await input.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), ct).ConfigureAwait(false);
            await input.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    internal static Uri AuthorizationUri(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.UserInfo.Length != 0 || !uri.IsDefaultPort
            || uri.Host is not ("auth.openai.com" or "chatgpt.com" or "auth.chatgpt.com"))
            throw new InvalidOperationException("Codex returned an unexpected authorization URL. Update Codex and try again.");
        return uri;
    }
    private static string? String(JsonElement value, string name) => value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
}
