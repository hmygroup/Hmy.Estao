//! Interactive OAuth sign-in helpers for the settings window.
//!
//! Wave 7 contract: the GitHub device flow for Copilot (port of
//! `src/Hmy.Estao.App/OAuthLoginService.cs` — request device code, open the
//! verification URL, poll for the token, verify the account login) and
//! terminal launches for `codex login` / `claude login`.
//!
//! The .NET implementation uses `System.Net.Http`. This crate deliberately has
//! no HTTP client of its own, so the device-flow requests are issued with the
//! system `curl.exe` (present on every supported Windows release). The request
//! shape is identical to the .NET code: form-encoded POSTs with an
//! `Accept: application/json` header, and the same endpoints and JSON fields.
//! All work runs on a background thread; progress crosses back to the UI
//! through [`CopilotSignInEvent`].

use std::io;
use std::process::{Command, Stdio};
use std::thread::{self, JoinHandle};
use std::time::{Duration, Instant};

use anyhow::{bail, Context, Result};
use estao_core::security::{OAuthToken, SecureOAuthTokenStore};
use serde_json::Value;

/// GitHub's public OAuth client used by VS Code, exactly as in the .NET port.
pub const COPILOT_OAUTH_CLIENT_ID: &str = "Iv1.b507a08c87ecfe98";

/// GitHub's OAuth scope for the Copilot sign-in.
pub const COPILOT_OAUTH_SCOPE: &str = "read:user";

const DEVICE_CODE_URL: &str = "https://github.com/login/device/code";
const ACCESS_TOKEN_URL: &str = "https://github.com/login/oauth/access_token";
const GITHUB_USER_URL: &str = "https://api.github.com/user";
const STATUS_MARKER: &str = "__ESTAO_STATUS__:";

/// Progress line sent before the first request.
pub const REQUESTING_DEVICE_CODE: &str = "Requesting a GitHub device code…";
/// Progress line sent after the device code arrives.
pub fn waiting_message(user_code: &str) -> String {
    format!("Enter code {user_code} in GitHub. Waiting for confirmation…")
}
/// Progress line sent after GitHub issues the token.
pub const VERIFYING_ACCOUNT: &str = "GitHub confirmed the sign-in. Verifying the Copilot account…";

/// Runs the GitHub device flow on a background thread.
///
/// `progress` receives the same status strings as the .NET
/// `IProgress<string>`. The access token is stored in
/// [`SecureOAuthTokenStore`] under `copilot` before `finished` is called, with
/// the GitHub login as the account label.
pub fn start_copilot_sign_in<F, G>(progress: F, finished: G) -> JoinHandle<()>
where
    F: Fn(&str) + Send + 'static,
    G: FnOnce(Result<OAuthToken, String>) + Send + 'static,
{
    thread::spawn(move || {
        let result = sign_in_to_copilot(&progress).and_then(|token| {
            save_copilot_token(&token)?;
            Ok(token)
        });

        finished(result.map_err(|error| format!("{error:#}")));
    })
}

/// A GitHub device-code response.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CopilotDeviceCode {
    /// Opaque code used when polling for the token.
    pub device_code: String,
    /// Short code the user types into GitHub.
    pub user_code: String,
    /// Verification page.
    pub verification_uri: String,
    /// Verification page with the code prefilled.
    pub verification_uri_complete: Option<String>,
    /// Lifetime of the device code, in seconds.
    pub expires_in: i64,
    /// Minimum polling interval, in seconds.
    pub interval: i64,
}

impl CopilotDeviceCode {
    /// Parses the JSON body returned by `POST /login/device/code`.
    pub fn parse(body: &str) -> Result<Self> {
        let value: Value = serde_json::from_str(body)
            .context("GitHub returned an invalid device-code response")?;

        let device_code = string_field(&value, "device_code").unwrap_or_default();
        if device_code.trim().is_empty() {
            bail!("GitHub did not return a device code.");
        }

        Ok(Self {
            device_code,
            user_code: string_field(&value, "user_code").unwrap_or_default(),
            verification_uri: string_field(&value, "verification_uri").unwrap_or_default(),
            verification_uri_complete: string_field(&value, "verification_uri_complete")
                .filter(|value| !value.trim().is_empty()),
            expires_in: value.get("expires_in").and_then(Value::as_i64).unwrap_or(0),
            interval: value.get("interval").and_then(Value::as_i64).unwrap_or(5),
        })
    }

    /// The URL opened in the browser: the prefilled one when GitHub sent it.
    pub fn verification_url(&self) -> &str {
        self.verification_uri_complete
            .as_deref()
            .unwrap_or(&self.verification_uri)
    }
}

/// A GitHub OAuth token response (success or device-flow error).
#[derive(Debug, Clone, Default, PartialEq, Eq)]
pub struct CopilotTokenResponse {
    /// Issued access token.
    pub access_token: Option<String>,
    /// Device-flow error code (`authorization_pending`, `slow_down`, ...).
    pub error: Option<String>,
    /// Human-readable error description.
    pub error_description: Option<String>,
}

impl CopilotTokenResponse {
    /// Parses the JSON body returned by `POST /login/oauth/access_token`.
    pub fn parse(body: &str) -> Result<Self> {
        let value: Value = serde_json::from_str(body)
            .context("GitHub returned an invalid OAuth token response")?;
        Ok(Self {
            access_token: string_field(&value, "access_token"),
            error: string_field(&value, "error"),
            error_description: string_field(&value, "error_description"),
        })
    }
}

/// What the polling loop should do with one token response.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum TokenPollOutcome {
    /// GitHub issued a token.
    Granted(String),
    /// The user has not confirmed yet; keep polling.
    Pending,
    /// GitHub asked us to slow down; keep polling with a larger delay.
    SlowDown,
    /// The device code expired.
    Expired,
    /// Any other error; stop polling.
    Failed(String),
}

/// Classifies a token response exactly like the .NET polling switch.
pub fn classify_token_response(response: &CopilotTokenResponse) -> TokenPollOutcome {
    if let Some(token) = response
        .access_token
        .as_deref()
        .map(str::trim)
        .filter(|token| !token.is_empty())
    {
        return TokenPollOutcome::Granted(token.to_string());
    }

    match response.error.as_deref() {
        Some("authorization_pending") => TokenPollOutcome::Pending,
        Some("slow_down") => TokenPollOutcome::SlowDown,
        Some("expired_token") => TokenPollOutcome::Expired,
        _ => TokenPollOutcome::Failed(
            response
                .error_description
                .as_deref()
                .filter(|description| !description.trim().is_empty())
                .or(response
                    .error
                    .as_deref()
                    .filter(|error| !error.trim().is_empty()))
                .unwrap_or("GitHub did not issue an OAuth token.")
                .to_string(),
        ),
    }
}

/// The initial polling delay, mirroring `Math.Max(1, interval)`.
pub fn initial_poll_delay(interval: i64) -> u64 {
    interval.max(1) as u64
}

/// Adds the `slow_down` back-off, mirroring `delay += 5`.
pub fn slowed_poll_delay(delay: u64) -> u64 {
    delay + 5
}

/// Extracts the `login` field from a `GET /user` response.
pub fn parse_github_login(body: &str) -> Option<String> {
    let value: Value = serde_json::from_str(body).ok()?;
    string_field(&value, "login")
}

/// Runs the synchronous device flow and returns the issued token.
///
/// Call this from a background thread. `progress` receives the same status
/// strings as the .NET `IProgress<string>`.
pub fn sign_in_to_copilot(progress: impl Fn(&str)) -> Result<OAuthToken> {
    progress(REQUESTING_DEVICE_CODE);
    let device_code = request_device_code()?;

    let verification_url = device_code.verification_url().to_string();
    progress(&waiting_message(&device_code.user_code));

    if let Err(error) = open_url(&verification_url) {
        progress(&format!(
            "Open {verification_url} and enter code {}. ({error})",
            device_code.user_code
        ));
    }

    let access_token = poll_token(&device_code)?;
    progress(VERIFYING_ACCOUNT);
    let account_label = try_get_github_login(&access_token)?;

    Ok(OAuthToken {
        access_token,
        account_label,
    })
}

/// Saves a Copilot token in the encrypted OAuth store.
pub fn save_copilot_token(token: &OAuthToken) -> Result<()> {
    SecureOAuthTokenStore::new()
        .save("copilot", token.clone())
        .context("failed to save the Copilot OAuth token")
}

/// Opens an arbitrary login command in a visible terminal window.
///
/// The command line is executed by `cmd /K`, so the window stays open after the
/// command exits, mirroring the .NET `Process.Start(UseShellExecute = true)`
/// behaviour.
pub fn open_terminal_login(command: &str) -> Result<()> {
    let executable = command.split_whitespace().next().unwrap_or(command);
    let display_name = match executable {
        "codex" => "Codex CLI",
        "claude" => "Claude Code CLI",
        other => other,
    };
    start_interactive_cli(executable, command, display_name)
}

fn string_field(value: &Value, name: &str) -> Option<String> {
    value.get(name).and_then(Value::as_str).map(str::to_string)
}

fn request_device_code() -> Result<CopilotDeviceCode> {
    let response = http_post_form(
        DEVICE_CODE_URL,
        &[
            ("client_id", COPILOT_OAUTH_CLIENT_ID),
            ("scope", COPILOT_OAUTH_SCOPE),
        ],
    )?;

    if !response.is_success() {
        bail!(
            "GitHub rejected the device-code request (HTTP {}).",
            response.status
        );
    }

    CopilotDeviceCode::parse(&response.body)
}

fn poll_token(device_code: &CopilotDeviceCode) -> Result<String> {
    let expires_at = Instant::now() + Duration::from_secs(device_code.expires_in.max(0) as u64);
    let mut delay = initial_poll_delay(device_code.interval);

    while Instant::now() < expires_at {
        thread::sleep(Duration::from_secs(delay));

        let response = http_post_form(
            ACCESS_TOKEN_URL,
            &[
                ("client_id", COPILOT_OAUTH_CLIENT_ID),
                ("device_code", &device_code.device_code),
                ("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
            ],
        )?;

        let parsed = CopilotTokenResponse::parse(&response.body)?;
        match classify_token_response(&parsed) {
            TokenPollOutcome::Granted(token) => return Ok(token),
            TokenPollOutcome::Pending => continue,
            TokenPollOutcome::SlowDown => {
                delay = slowed_poll_delay(delay);
                continue;
            }
            TokenPollOutcome::Expired => {
                bail!("The GitHub device code expired. Start sign-in again.")
            }
            TokenPollOutcome::Failed(message) => bail!("{message}"),
        }
    }

    bail!("The GitHub device code expired. Start sign-in again.")
}

fn try_get_github_login(token: &str) -> Result<Option<String>> {
    let authorization = format!("token {token}");
    let response = http_get(
        GITHUB_USER_URL,
        &[
            ("Authorization", authorization.as_str()),
            ("User-Agent", "Hmy.Estao"),
        ],
    )?;

    if !response.is_success() {
        return Ok(None);
    }

    Ok(parse_github_login(&response.body))
}

fn start_interactive_cli(executable: &str, command: &str, display_name: &str) -> Result<()> {
    if !is_available(executable) {
        bail!(
            "{display_name} is not installed or is not on PATH. Install it first, then try OAuth sign-in again."
        );
    }

    let command_shell = std::env::var("ComSpec").unwrap_or_else(|_| "cmd.exe".to_string());
    let working_directory = std::env::current_dir().unwrap_or_else(|_| ".".into());

    let mut process = Command::new(command_shell);
    process.current_dir(working_directory);

    #[cfg(target_os = "windows")]
    {
        use std::os::windows::process::CommandExt;
        // `start` creates the visible console window; the inner `cmd /K` keeps
        // it open after the login command finishes.
        process.raw_arg(format!("/C start \"Estao sign-in\" cmd /K {command}"));
    }

    #[cfg(not(target_os = "windows"))]
    process.arg("-c").arg(command);

    process
        .spawn()
        .with_context(|| format!("could not open the {display_name} sign-in window"))?;
    Ok(())
}

fn is_available(executable: &str) -> bool {
    let mut process = Command::new("where.exe");
    process
        .arg(executable)
        .stdout(Stdio::null())
        .stderr(Stdio::null());

    #[cfg(target_os = "windows")]
    {
        use std::os::windows::process::CommandExt;
        // CREATE_NO_WINDOW: the availability probe must not flash a console.
        process.creation_flags(0x0800_0000);
    }

    process
        .status()
        .map(|status| status.success())
        .unwrap_or(false)
}

#[cfg(target_os = "windows")]
fn open_url(url: &str) -> io::Result<()> {
    use std::os::windows::process::CommandExt;

    let mut process = Command::new("cmd.exe");
    process.raw_arg(format!("/C start \"\" \"{}\"", url.replace('"', "%22")));
    process.spawn().map(|_| ())
}

#[cfg(not(target_os = "windows"))]
fn open_url(url: &str) -> io::Result<()> {
    Command::new("xdg-open").arg(url).spawn().map(|_| ())
}

/// A minimal HTTP response produced by a `curl.exe` invocation.
#[derive(Debug, Clone, PartialEq, Eq)]
struct CurlResponse {
    status: u16,
    body: String,
}

impl CurlResponse {
    fn is_success(&self) -> bool {
        (200..300).contains(&self.status)
    }
}

fn http_post_form(url: &str, form: &[(&str, &str)]) -> Result<CurlResponse> {
    let mut args = vec![
        "-X".to_string(),
        "POST".to_string(),
        "-H".to_string(),
        "Accept: application/json".to_string(),
        "-H".to_string(),
        "Content-Type: application/x-www-form-urlencoded".to_string(),
    ];
    for (name, value) in form {
        args.push("--data-urlencode".to_string());
        args.push(format!("{name}={value}"));
    }
    args.push(url.to_string());

    run_curl(args)
}

fn http_get(url: &str, headers: &[(&str, &str)]) -> Result<CurlResponse> {
    let mut args = Vec::new();
    for (name, value) in headers {
        args.push("-H".to_string());
        args.push(format!("{name}: {value}"));
    }
    args.push(url.to_string());

    run_curl(args)
}

fn run_curl(mut args: Vec<String>) -> Result<CurlResponse> {
    args.push("-sS".to_string());
    args.push("--max-time".to_string());
    args.push("120".to_string());
    args.push("--write-out".to_string());
    args.push(format!("\n{STATUS_MARKER}%{{http_code}}"));

    let output = Command::new("curl.exe")
        .args(&args)
        .output()
        .context("failed to run curl.exe for the GitHub sign-in")?;

    let text = String::from_utf8_lossy(&output.stdout);
    if let Some(response) = parse_curl_output(&text) {
        return Ok(response);
    }

    let stderr = String::from_utf8_lossy(&output.stderr);
    let stderr = stderr.trim();
    if stderr.is_empty() {
        bail!("the GitHub request failed without a response");
    }

    bail!("the GitHub request failed: {stderr}")
}

fn parse_curl_output(text: &str) -> Option<CurlResponse> {
    let (body, status) = text.rsplit_once(STATUS_MARKER)?;
    let status = status.trim().parse().ok()?;
    // `--write-out` is prefixed with a newline so the marker never lands on the
    // body's last line; remove exactly that separator, not arbitrary trailing
    // whitespace from the response.
    let body = body.strip_suffix('\n').unwrap_or(body);
    let body = body.strip_suffix('\r').unwrap_or(body);
    Some(CurlResponse {
        status,
        body: body.to_string(),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    // Shape of `POST https://github.com/login/device/code`, as consumed by the
    // .NET `CopilotDeviceCode` record.
    const DEVICE_CODE_FIXTURE: &str = r#"{
        "device_code": "3584d83530557fdd1f46af8289938c8ef79f9dc5",
        "user_code": "WDJB-MJHT",
        "verification_uri": "https://github.com/login/device",
        "expires_in": 900,
        "interval": 5
    }"#;

    const DEVICE_CODE_COMPLETE_FIXTURE: &str = r#"{
        "device_code": "3584d83530557fdd1f46af8289938c8ef79f9dc5",
        "user_code": "WDJB-MJHT",
        "verification_uri": "https://github.com/login/device",
        "verification_uri_complete": "https://github.com/login/device?user_code=WDJB-MJHT",
        "expires_in": 900,
        "interval": 5
    }"#;

    #[test]
    fn parses_the_device_code_response() {
        let code = CopilotDeviceCode::parse(DEVICE_CODE_FIXTURE).unwrap();

        assert_eq!(code.device_code, "3584d83530557fdd1f46af8289938c8ef79f9dc5");
        assert_eq!(code.user_code, "WDJB-MJHT");
        assert_eq!(code.verification_uri, "https://github.com/login/device");
        assert_eq!(code.verification_uri_complete, None);
        assert_eq!(code.expires_in, 900);
        assert_eq!(code.interval, 5);
        assert_eq!(code.verification_url(), "https://github.com/login/device");
    }

    #[test]
    fn prefers_the_complete_verification_url() {
        let code = CopilotDeviceCode::parse(DEVICE_CODE_COMPLETE_FIXTURE).unwrap();

        assert_eq!(
            code.verification_url(),
            "https://github.com/login/device?user_code=WDJB-MJHT"
        );
    }

    #[test]
    fn rejects_a_device_code_response_without_a_code() {
        let error = CopilotDeviceCode::parse(r#"{ "user_code": "WDJB-MJHT" }"#).unwrap_err();
        assert_eq!(error.to_string(), "GitHub did not return a device code.");
    }

    #[test]
    fn waiting_message_matches_the_dotnet_wording() {
        assert_eq!(
            waiting_message("WDJB-MJHT"),
            "Enter code WDJB-MJHT in GitHub. Waiting for confirmation…"
        );
    }

    // Shapes of `POST https://github.com/login/oauth/access_token`.
    #[test]
    fn classifies_token_responses_like_the_dotnet_poll() {
        let granted = CopilotTokenResponse::parse(
            r#"{ "access_token": "gho_abc", "token_type": "bearer", "scope": "read:user" }"#,
        )
        .unwrap();
        assert_eq!(
            classify_token_response(&granted),
            TokenPollOutcome::Granted("gho_abc".to_string())
        );

        let pending = CopilotTokenResponse::parse(
            r#"{ "error": "authorization_pending", "error_description": "waiting" }"#,
        )
        .unwrap();
        assert_eq!(classify_token_response(&pending), TokenPollOutcome::Pending);

        let slow_down = CopilotTokenResponse::parse(r#"{ "error": "slow_down" }"#).unwrap();
        assert_eq!(
            classify_token_response(&slow_down),
            TokenPollOutcome::SlowDown
        );

        let expired = CopilotTokenResponse::parse(r#"{ "error": "expired_token" }"#).unwrap();
        assert_eq!(classify_token_response(&expired), TokenPollOutcome::Expired);

        let denied = CopilotTokenResponse::parse(
            r#"{ "error": "access_denied", "error_description": "The user denied the request" }"#,
        )
        .unwrap();
        assert_eq!(
            classify_token_response(&denied),
            TokenPollOutcome::Failed("The user denied the request".to_string())
        );

        let unknown = CopilotTokenResponse::parse(r#"{ "error": "server_error" }"#).unwrap();
        assert_eq!(
            classify_token_response(&unknown),
            TokenPollOutcome::Failed("server_error".to_string())
        );

        let empty = CopilotTokenResponse::parse("{}").unwrap();
        assert_eq!(
            classify_token_response(&empty),
            TokenPollOutcome::Failed("GitHub did not issue an OAuth token.".to_string())
        );
    }

    #[test]
    fn treats_blank_tokens_as_not_granted() {
        let response = CopilotTokenResponse {
            access_token: Some("   ".to_string()),
            error: Some("authorization_pending".to_string()),
            error_description: None,
        };

        assert_eq!(
            classify_token_response(&response),
            TokenPollOutcome::Pending
        );
    }

    #[test]
    fn polling_delays_match_the_dotnet_math() {
        assert_eq!(initial_poll_delay(0), 1);
        assert_eq!(initial_poll_delay(-4), 1);
        assert_eq!(initial_poll_delay(5), 5);
        assert_eq!(slowed_poll_delay(5), 10);
        assert_eq!(slowed_poll_delay(10), 15);
    }

    #[test]
    fn parses_the_github_login() {
        assert_eq!(
            parse_github_login(r#"{ "login": "octocat", "id": 1, "type": "User" }"#).as_deref(),
            Some("octocat")
        );
        assert_eq!(parse_github_login(r#"{ "id": 1 }"#), None);
        assert_eq!(parse_github_login("not json"), None);
    }

    #[test]
    fn splits_the_curl_status_marker() {
        let response = parse_curl_output("{\"login\":\"octocat\"}\n__ESTAO_STATUS__:200").unwrap();
        assert_eq!(response.status, 200);
        assert_eq!(response.body, "{\"login\":\"octocat\"}");
        assert!(response.is_success());

        assert_eq!(
            parse_curl_output("oops\n__ESTAO_STATUS__:404")
                .unwrap()
                .status,
            404
        );
        assert!(parse_curl_output("no marker").is_none());
        assert!(parse_curl_output("\n__ESTAO_STATUS__:abc").is_none());
    }
}
