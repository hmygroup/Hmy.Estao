using ZarpaSuite.Controls;

namespace Hmy.Estao.App;

partial class OAuthLoginForm
{
    private System.ComponentModel.IContainer components = null!;
    private ZarpaThemeManager _themeManager = null!;
    private Label _title = null!;
    private Label _description = null!;
    private ZarpaButton _codexButton = null!;
    private ZarpaButton _claudeButton = null!;
    private ZarpaButton _copilotButton = null!;
    private Label _status = null!;
    private ZarpaButton _closeButton = null!;

    protected override void Dispose(bool disposing)
    {
        if (disposing) components?.Dispose();
        base.Dispose(disposing);
    }

    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        _themeManager = new ZarpaThemeManager(components) { Preset = ZarpaThemePreset.Graphite, ApplyThemeFontToNativeControls = true };
        _title = new Label();
        _description = new Label();
        _codexButton = new ZarpaButton();
        _claudeButton = new ZarpaButton();
        _copilotButton = new ZarpaButton();
        _status = new Label();
        _closeButton = new ZarpaButton();
        SuspendLayout();
        // 
        // _title
        // 
        _title.AutoSize = true;
        _title.Font = new Font("Segoe UI", 18F, FontStyle.Bold);
        _title.Location = new Point(24, 22);
        _title.Text = "Sign in with OAuth";
        // 
        // _description
        // 
        _description.Location = new Point(27, 62);
        _description.Size = new Size(420, 46);
        _description.Text = "Sign in without API keys. Copilot OAuth is encrypted for this Windows user and is only used to read usage.";
        // 
        // _codexButton
        // 
        _codexButton.ButtonStyle = ZarpaButtonStyle.Primary;
        _codexButton.IconKey = "ic_fluent_bot_24_regular";
        _codexButton.Location = new Point(27, 126);
        _codexButton.Size = new Size(200, 38);
        _codexButton.Text = "Sign in to Codex";
        _codexButton.Click += codexButton_Click;
        // 
        // _claudeButton
        // 
        _claudeButton.ButtonStyle = ZarpaButtonStyle.Secondary;
        _claudeButton.IconKey = "ic_fluent_sparkle_24_regular";
        _claudeButton.Location = new Point(247, 126);
        _claudeButton.Size = new Size(200, 38);
        _claudeButton.Text = "Sign in to Claude";
        _claudeButton.Click += claudeButton_Click;
        // 
        // _copilotButton
        // 
        _copilotButton.ButtonStyle = ZarpaButtonStyle.Secondary;
        _copilotButton.IconKey = "ic_fluent_people_24_regular";
        _copilotButton.Location = new Point(27, 176);
        _copilotButton.Size = new Size(200, 38);
        _copilotButton.Text = "Sign in to Copilot";
        _copilotButton.Click += copilotButton_Click;
        // 
        // _status
        // 
        _status.ForeColor = Color.FromArgb(105, 96, 134);
        _status.Location = new Point(27, 235);
        _status.Size = new Size(420, 62);
        _status.Text = "Choose a provider to open its supported sign-in flow.";
        // 
        // _closeButton
        // 
        _closeButton.ButtonStyle = ZarpaButtonStyle.Subtle;
        _closeButton.IconKey = "ic_fluent_dismiss_24_regular";
        _closeButton.Location = new Point(347, 312);
        _closeButton.Size = new Size(100, 36);
        _closeButton.Text = "Close";
        _closeButton.Click += (_, _) => Close();
        // 
        // OAuthLoginForm
        // 
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(474, 368);
        Controls.Add(_closeButton);
        Controls.Add(_status);
        Controls.Add(_copilotButton);
        Controls.Add(_claudeButton);
        Controls.Add(_codexButton);
        Controls.Add(_description);
        Controls.Add(_title);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        Name = "OAuthLoginForm";
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        Text = "Estao · OAuth sign-in";
        ResumeLayout(false);
        PerformLayout();
    }
}
