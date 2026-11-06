using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Models;
using Hmy.Estao.Desktop;

namespace Hmy.Estao.Desktop.Tests;

public sealed class CodexLoginTests
{
    private const string LoginResult = "{\"id\":2,\"result\":{\"type\":\"chatgpt\",\"loginId\":\"expected\",\"authUrl\":\"https://auth.openai.com/oauth/authorize?state=test\"}}";
    private const string InitializeResult = "{\"id\":1,\"result\":{\"userAgent\":\"fixture\"}}";
    private const string Success = "{\"method\":\"account/login/completed\",\"params\":{\"loginId\":\"expected\",\"success\":true}}";

    [Fact]
    public async Task SignOutUsesTheAccountLogoutProtocolWithoutStartingLogin()
    {
        using var output = new StringReader(InitializeResult + "\n{\"id\":2,\"result\":{}}");
        using var input = new StringWriter();
        Assert.True(await CodexLoginService.SignOutExchangeAsync(output, input, CancellationToken.None));
        Assert.Contains("account/logout", input.ToString());
        Assert.DoesNotContain("account/login/start", input.ToString());
    }

    [Theory]
    [InlineData("{\"type\":\"chatgpt\",\"email\":\"private@example.test\",\"planType\":\"plus\"}", true, "Signed in with ChatGPT")]
    [InlineData("{\"type\":\"apiKey\"}", true, "API key configured")]
    [InlineData("null", false, "Not signed in")]
    public async Task ExistingAccountDetectionDoesNotStartANewLogin(string account, bool signedIn, string expected)
    {
        using var output = new StringReader(InitializeResult + "\n" + "{\"id\":2,\"result\":{\"account\":" + account + ",\"requiresOpenaiAuth\":true}}");
        using var input = new StringWriter();
        var status = await CodexLoginService.ReadAccountExchangeAsync(output, input, CancellationToken.None);
        Assert.Equal(signedIn, status.SignedIn);
        Assert.Contains(expected, status.Message);
        Assert.DoesNotContain("private@example.test", JsonSerializer.Serialize(status));
        Assert.Contains("account/read", input.ToString());
        Assert.Contains("\"refreshToken\":false", input.ToString());
        Assert.DoesNotContain("account/login/start", input.ToString());
    }

    [Fact]
    public async Task AccountReadErrorsAreNotReportedAsSignedOutAndDoNotExposeServerDetails()
    {
        using var output = new StringReader(InitializeResult + "\n{\"id\":2,\"error\":{\"message\":\"sensitive-server-error\"}}");
        using var input = new StringWriter();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CodexLoginService.ReadAccountExchangeAsync(output, input, CancellationToken.None));
        Assert.DoesNotContain("sensitive-server-error", error.Message);
        Assert.Contains("Could not check", error.Message);
    }

    [Fact]
    public async Task MissingProfileIsNotCreatedWhenCheckingLogin()
    {
        var path = Path.Combine(Path.GetTempPath(), "estao-no-profile-" + Guid.NewGuid().ToString("N"));
        var status = await CodexLoginService.ReadAccountAsync(path, CancellationToken.None);
        Assert.False(status.SignedIn);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task BrowserFlowInitializesStartsChatGptLoginAndWaitsForMatchingCompletion()
    {
        using var output = new StringReader(string.Join('\n', InitializeResult, LoginResult,
            "{\"method\":\"account/login/completed\",\"params\":{\"loginId\":\"other\",\"success\":false}}", Success));
        using var input = new StringWriter();
        var opened = new List<Uri>();
        await CodexLoginService.ExchangeAsync(output, input, opened.Add, null, CancellationToken.None);
        Assert.Equal("auth.openai.com", Assert.Single(opened).Host);
        var sent = input.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Select(line => JsonDocument.Parse(line)).ToArray();
        try
        {
            Assert.Equal(3, sent.Length);
            Assert.Equal("initialize", sent[0].RootElement.GetProperty("method").GetString());
            Assert.Equal("initialized", sent[1].RootElement.GetProperty("method").GetString());
            Assert.Equal("account/login/start", sent[2].RootElement.GetProperty("method").GetString());
            Assert.Equal("chatgpt", sent[2].RootElement.GetProperty("params").GetProperty("type").GetString());
        }
        finally { foreach (var document in sent) document.Dispose(); }
    }

    [Fact]
    public async Task CancellationCancelsTheMatchingPendingLogin()
    {
        using var ct = new CancellationTokenSource();
        using var output = new PendingReader(InitializeResult, LoginResult);
        using var input = new StringWriter();
        var opened = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = CodexLoginService.ExchangeAsync(output, input, _ => opened.TrySetResult(), null, ct.Token);
        await opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(pending.IsCompleted);
        ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        var last = input.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Last();
        using var cancel = JsonDocument.Parse(last);
        Assert.Equal("account/login/cancel", cancel.RootElement.GetProperty("method").GetString());
        Assert.Equal("expected", cancel.RootElement.GetProperty("params").GetProperty("loginId").GetString());
    }

    [Theory]
    [InlineData("{\"id\":1,\"error\":{\"message\":\"sensitive-server-error\"}}")]
    [InlineData("{\"id\":1,\"result\":{}}\n{\"id\":2,\"error\":{\"code\":-32601,\"message\":\"sensitive-server-error\"}}")]
    [InlineData(InitializeResult + "\n" + LoginResult + "\n{\"method\":\"account/login/completed\",\"params\":{\"loginId\":\"expected\",\"success\":false,\"error\":\"sensitive-server-error\"}}")]
    public async Task UnsupportedCliOrFailedLoginShowsSafeErrors(string transcript)
    {
        using var output = new StringReader(transcript);
        using var input = new StringWriter();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CodexLoginService.ExchangeAsync(output, input, _ => { }, null, CancellationToken.None));
        Assert.DoesNotContain("sensitive-server-error", error.Message);
    }

    [Fact]
    public async Task PrematureExitIsNotReportedAsSuccess()
    {
        using var output = new StringReader(InitializeResult + "\n" + LoginResult);
        using var input = new StringWriter();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CodexLoginService.ExchangeAsync(output, input, _ => { }, null, CancellationToken.None));
        Assert.Contains("closed before", error.Message);
    }

    [Theory]
    [InlineData("http://auth.openai.com/login")]
    [InlineData("https://auth.openai.com.evil.example/login")]
    [InlineData("https://evil.example/login")]
    [InlineData("https://user:pass@auth.openai.com/login")]
    [InlineData("https://auth.openai.com:8443/login")]
    [InlineData("file:///C:/test.exe")]
    [InlineData(null)]
    public void UnexpectedBrowserDestinationsAreRejected(string? url) => Assert.Throws<InvalidOperationException>(() => CodexLoginService.AuthorizationUri(url));

    [Fact]
    public void ProfileUsesSameActiveAccountSelectionAsUsageFetching()
    {
        var first = Path.Combine(Path.GetTempPath(), "codex-first");
        var second = Path.Combine(Path.GetTempPath(), "codex-second");
        var accounts = new[] { new ProviderAccount("1", "First", HomePath: first), new ProviderAccount("2", "Second", HomePath: second) };
        var config = new ProviderConfig { ActiveAccountIndex = 1 };
        Assert.Equal(second, CodexLoginService.ActiveProfile(config, accounts));
        config.ActiveAccountIndex = 999;
        Assert.Equal(first, CodexLoginService.ActiveProfile(config, accounts));
        config.ActiveAccountIndex = null;
        config.TokenAccounts = new() { ActiveIndex = 1 };
        Assert.Equal(second, CodexLoginService.ActiveProfile(config, accounts));
    }

    [Theory]
    [InlineData("codex.exe")]
    [InlineData("codex.cmd")]
    [InlineData("codex.CMD")]
    public void LaunchIsHiddenAndSetsTheSelectedCodexHome(string launcher)
    {
        var exe = Path.Combine(Path.GetTempPath(), "a directory with spaces", launcher);
        var home = Path.Combine(Path.GetTempPath(), "codex-work");
        var info = CodexLoginService.CreateStartInfo(exe, home);
        Assert.False(info.UseShellExecute);
        Assert.True(info.CreateNoWindow);
        Assert.True(info.RedirectStandardInput && info.RedirectStandardOutput && info.RedirectStandardError);
        Assert.Equal(home, info.Environment["CODEX_HOME"]);
        if (launcher.EndsWith(".exe")) Assert.Equal("app-server", Assert.Single(info.ArgumentList));
        else
        {
            Assert.Contains("/d /s /c", info.Arguments);
            Assert.DoesNotContain("/k", info.Arguments);
            Assert.Contains("app-server", info.Arguments);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HiddenServerProcessIsStoppedAfterSuccessOrCancellation(bool cancel)
    {
        var root = Path.Combine(Path.GetTempPath(), "estao-codex-login-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pidFile = Path.Combine(root, "pid.txt");
        var script = Path.Combine(root, "server.ps1");
        // A fake stdio server: no OpenAI calls, credentials, or real profiles.
        await File.WriteAllTextAsync(script, """
            [IO.File]::WriteAllText($env:ESTAO_PID_FILE, [string]$PID)
            $null = [Console]::ReadLine()
            [Console]::WriteLine('{"id":1,"result":{}}')
            $null = [Console]::ReadLine()
            $null = [Console]::ReadLine()
            [Console]::WriteLine('{"id":2,"result":{"type":"chatgpt","loginId":"expected","authUrl":"https://auth.openai.com/oauth/authorize?state=test"}}')
            if ($env:ESTAO_FAKE_CANCEL -ne 'yes') {
                [Console]::WriteLine('{"method":"account/login/completed","params":{"loginId":"expected","success":true}}')
            }
            Start-Sleep -Seconds 60
            """);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-Command", await File.ReadAllTextAsync(script) }) info.ArgumentList.Add(arg);
            info.Environment["ESTAO_PID_FILE"] = pidFile;
            info.Environment["ESTAO_FAKE_CANCEL"] = cancel ? "yes" : "no";
            var task = CodexLoginService.RunAsync(info, _ => { if (cancel) ct.Cancel(); }, null, ct.Token);
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            else await task;
            var pid = int.Parse(await File.ReadAllTextAsync(pidFile));
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(pid));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private sealed class PendingReader : TextReader
    {
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
        public PendingReader(params string[] lines) { foreach (var line in lines) _lines.Writer.TryWrite(line); }
        public override ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken) => ReadAsync(cancellationToken);
        private async ValueTask<string?> ReadAsync(CancellationToken ct) => await _lines.Reader.ReadAsync(ct);
    }
}
