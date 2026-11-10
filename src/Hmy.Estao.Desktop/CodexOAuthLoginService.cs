using Hmy.Estao.Core.Security;
using Hmy.Estao.Core.Models;

namespace Hmy.Estao.Desktop;

internal static class CodexOAuthLoginService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };
    internal static Uri AuthorizationUri(string redirect, string challenge, string state)
        => new("https://auth.openai.com/oauth/authorize?" + string.Join("&", new Dictionary<string, string>
        {
            ["client_id"] = CodexOAuthClient.ClientId, ["response_type"] = "code", ["redirect_uri"] = redirect,
            ["scope"] = "openid profile email offline_access", ["code_challenge"] = challenge, ["code_challenge_method"] = "S256",
            ["state"] = state, ["id_token_add_organizations"] = "true", ["codex_cli_simplified_flow"] = "true"
        }.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}")));

    internal static async Task<OAuthToken> SignInAsync(Action<Uri> openBrowser, CancellationToken ct, HttpClient? client = null, int port = 1455)
    {
        ct.ThrowIfCancellationRequested();
        using var listener = CreateListener(port);
        var proof = ClaudeOAuthLoginService.CreateProof();
        openBrowser(AuthorizationUri(listener.RedirectUri, proof.Challenge, proof.State));
        var code = await listener.WaitForCodeAsync(proof.State, ct).ConfigureAwait(false);
        return await CodexOAuthClient.ExchangeAsync(client ?? Client, code, proof.Verifier, listener.RedirectUri, ct).ConfigureAwait(false);
    }

    private static ClaudeOAuthLoginService.CallbackListener CreateListener(int port)
    {
        try { return new(port, "/auth/callback", "signin.codex_oauth_denied"); }
        catch (System.Net.Sockets.SocketException exception) when (exception.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse)
        {
            throw new InvalidOperationException("The Codex OAuth callback port is already in use.", exception)
                .WithUserMessage("signin.codex_callback_busy");
        }
    }
}
