using ZarpaSuite.Controls;
using Hmy.Estao.Core.Configuration;
using Hmy.Estao.Core.Security;
using Hmy.Estao.App.Controls.Zarpa;

namespace Hmy.Estao.App;

public sealed partial class OAuthLoginForm : Form
{
    private readonly ConfigStore _configStore;
    private readonly IOAuthTokenStore _oauthTokenStore;

    public OAuthLoginForm(ConfigStore configStore)
    {
        _configStore = configStore;
        _oauthTokenStore = new SecureOAuthTokenStore();
        InitializeComponent();
        _themeManager.Preset = ZarpaThemePreferences.Parse(_configStore.LoadAsync().GetAwaiter().GetResult().Theme);
        _themeManager.Attach(this);
    }

    private async void codexButton_Click(object? sender, EventArgs e) => await LaunchAsync("codex", "Codex", OAuthLoginService.StartCodexLogin);

    private async void claudeButton_Click(object? sender, EventArgs e) => await LaunchAsync("claude", "Claude Code", OAuthLoginService.StartClaudeLogin);

    private async void copilotButton_Click(object? sender, EventArgs e) => await SignInToCopilotAsync();

    private async Task LaunchAsync(string providerId, string provider, Action signIn)
    {
        try
        {
            signIn();
            await EnableProviderAsync(providerId);
            _status.Text = $"Finish the {provider} sign-in in the terminal/browser, then use Refresh usage.";
        }
        catch (Exception exception)
        {
            _status.Text = $"Could not launch sign-in: {exception.Message}";
        }
    }

    private async Task SignInToCopilotAsync()
    {
        SetSigningIn(true);
        try
        {
            var progress = new Progress<string>(message => _status.Text = message);
            var login = await OAuthLoginService.SignInToCopilotAsync(progress);
            await _oauthTokenStore.SaveAsync("copilot", new OAuthToken(login.AccessToken, login.AccountLabel));
            await EnableProviderAsync("copilot", "oauth");
            _status.Text = $"Copilot connected{(string.IsNullOrWhiteSpace(login.AccountLabel) ? string.Empty : $" as {login.AccountLabel}")}. Refresh usage now.";
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Copilot sign-in was cancelled.";
        }
        catch (Exception exception)
        {
            _status.Text = $"Copilot sign-in failed: {exception.Message}";
        }
        finally
        {
            SetSigningIn(false);
        }
    }

    private async Task EnableProviderAsync(string providerId, string source = "auto")
    {
        var config = await _configStore.LoadAsync();
        var provider = config.Providers.First(item => item.Id == providerId);
        provider.Enabled = true;
        provider.Source = source;
        await _configStore.SaveAsync(config);
    }

    private void SetSigningIn(bool signingIn)
    {
        _codexButton.Enabled = !signingIn;
        _claudeButton.Enabled = !signingIn;
        _copilotButton.Enabled = !signingIn;
        _closeButton.Enabled = !signingIn;
        UseWaitCursor = signingIn;
    }
}
