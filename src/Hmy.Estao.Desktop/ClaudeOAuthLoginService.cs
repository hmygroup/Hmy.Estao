using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Hmy.Estao.Core.Models;
using Hmy.Estao.Core.Security;

namespace Hmy.Estao.Desktop;

internal static class ClaudeOAuthLoginService
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(30) };

    internal static (string Verifier, string Challenge, string State) CreateProof()
    {
        static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var verifier = Encode(RandomNumberGenerator.GetBytes(32));
        return (verifier, Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))), Encode(RandomNumberGenerator.GetBytes(32)));
    }

    internal static Uri AuthorizationUri(string redirect, string challenge, string state)
    {
        var values = new Dictionary<string, string>
        {
            ["code"] = "true", ["response_type"] = "code", ["client_id"] = ClaudeOAuthClient.ClientId,
            ["redirect_uri"] = redirect, ["scope"] = ClaudeOAuthClient.UsageScope,
            ["code_challenge"] = challenge, ["code_challenge_method"] = "S256", ["state"] = state
        };
        return new Uri("https://claude.ai/oauth/authorize?" + string.Join("&", values.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}")));
    }

    public static async Task<OAuthToken> SignInAsync(Action<Uri> openBrowser, CancellationToken ct, HttpClient? client = null)
    {
        ct.ThrowIfCancellationRequested();
        using var callback = new CallbackListener();
        var proof = CreateProof();
        // Bind before opening the browser; cancellation always closes the listener.
        openBrowser(AuthorizationUri(callback.RedirectUri, proof.Challenge, proof.State));
        var code = await callback.WaitForCodeAsync(proof.State, ct).ConfigureAwait(false);
        var token = await ClaudeOAuthClient.ExchangeAsync(client ?? Client, code, proof.State, proof.Verifier, callback.RedirectUri, ct).ConfigureAwait(false);
        await ClaudeOAuthClient.ValidateUsageAsync(client ?? Client, token, ct).ConfigureAwait(false);
        return token;
    }

    internal sealed class CallbackListener : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly string _path;
        private readonly string _deniedMessage;
        public string RedirectUri { get; }
        public CallbackListener(int port = 0, string path = "/callback", string deniedMessage = "signin.oauth_denied")
        {
            _listener = new(IPAddress.Loopback, port);
            _path = path;
            _deniedMessage = deniedMessage;
            _listener.Start();
            RedirectUri = $"http://localhost:{((IPEndPoint)_listener.LocalEndpoint).Port}{path}";
        }

        public async Task<string> WaitForCodeAsync(string state, CancellationToken ct)
        {
            while (true)
            {
                using var socket = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                using var requestTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                requestTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    using var stream = socket.GetStream();
                    var bytes = new byte[8192];
                    var length = 0;
                    string headers;
                    do
                    {
                        var read = await stream.ReadAsync(bytes.AsMemory(length), requestTimeout.Token).ConfigureAwait(false);
                        if (read == 0) break;
                        length += read;
                        headers = Encoding.ASCII.GetString(bytes, 0, length);
                        if (headers.Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                    } while (length < bytes.Length);
                    headers = Encoding.ASCII.GetString(bytes, 0, length);
                    var line = headers.Split("\r\n", 2)[0].Split(' ');
                    var validRoute = line.Length == 3 && line[0] == "GET" && line[1].StartsWith(_path + "?", StringComparison.Ordinal)
                        && headers.Contains("\r\n\r\n", StringComparison.Ordinal)
                        && headers.Split("\r\n").Any(h => h.Equals($"Host: localhost:{new Uri(RedirectUri).Port}", StringComparison.OrdinalIgnoreCase));
                    var query = validRoute ? System.Web.HttpUtility.ParseQueryString(new Uri(new Uri(RedirectUri), line[1]).Query) : null;
                    var returnedState = query?["state"];
                    var matches = returnedState is not null && CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(returnedState), Encoding.UTF8.GetBytes(state));
                    var code = query?["code"];
                    var error = query?["error"];
                    var accepted = matches && (!string.IsNullOrWhiteSpace(code) || error is not null);
                    var message = accepted ? DesktopLocalization.Text("signin.oauth_browser_done") : "Invalid authentication callback.";
                    var body = Encoding.UTF8.GetBytes($"<!doctype html><meta charset=utf-8><title>Estao</title><p>{WebUtility.HtmlEncode(message)}</p>");
                    var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {(accepted ? "200 OK" : "400 Bad Request")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nContent-Security-Policy: default-src 'none'\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(head, requestTimeout.Token).ConfigureAwait(false);
                    await stream.WriteAsync(body, requestTimeout.Token).ConfigureAwait(false);
                    if (!accepted) continue;
                    if (error is not null) throw new InvalidOperationException("Authorization was declined.").WithUserMessage(_deniedMessage);
                    return code!;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                catch (IOException) { }
            }
        }

        public void Dispose() => _listener.Stop();
    }
}
