//! Usage providers: trait, factory, helpers, and one module per provider.
//! Ported from `Providers/`.
//!
//! The `.NET` implementation injected an `HttpClient` plus the cookie and
//! OAuth token stores. The Rust port mirrors that with [`HttpClient`] (a thin
//! blocking `ureq` wrapper with a 30 second timeout) and the
//! [`CookieSecretStore`] / [`OAuthTokenStore`] traits.

pub mod claude;
pub mod codex;
pub mod copilot;
pub mod helpers;
pub mod opencode;
pub mod zai;

use std::sync::Arc;
use std::time::Duration;

use anyhow::{bail, Context, Result};
use chrono::{DateTime, Utc};
use serde_json::Value;

use crate::catalog;
use crate::config::ProviderConfig;
use crate::models::{CreditsSnapshot, FetchRequest, ProviderAccount, RateWindow, UsageSnapshot};
use crate::security::{OAuthToken, SecureCookieStore, SecureOAuthTokenStore};

/// A usage provider: one API/CLI integration per supported service.
pub trait UsageProvider: Send + Sync {
    /// The normalized provider id, e.g. `codex`.
    fn id(&self) -> &'static str;

    /// The accounts (profiles, tokens or workspaces) configured for the
    /// provider.
    fn accounts(&self, config: &ProviderConfig) -> Vec<ProviderAccount>;

    /// Fetches a usage snapshot.
    fn fetch(&self, request: &FetchRequest<'_>) -> Result<UsageSnapshot>;
}

/// Reads and writes encrypted provider cookie headers.
///
/// Mirrors `ICookieSecretStore`; the default implementation is
/// [`SecureCookieStore`].
pub trait CookieSecretStore: Send + Sync {
    /// Reads the saved cookie header for a provider.
    fn read_cookie_header(&self, provider_id: &str) -> Result<Option<String>>;

    /// Saves a cookie header for a provider.
    fn save_cookie_header(&self, provider_id: &str, cookie_header: &str) -> Result<()>;

    /// Removes a provider's saved cookie header.
    fn clear_cookie_header(&self, provider_id: &str) -> Result<()>;
}

/// The default [`CookieSecretStore`]: DPAPI-backed [`SecureCookieStore`],
/// serialized so the provider can be shared across threads.
pub struct DefaultCookieSecretStore {
    inner: std::sync::Mutex<SecureCookieStore>,
}

impl DefaultCookieSecretStore {
    /// A store in the default data directory using DPAPI.
    pub fn new() -> Self {
        Self {
            inner: std::sync::Mutex::new(SecureCookieStore::new()),
        }
    }
}

impl CookieSecretStore for DefaultCookieSecretStore {
    fn read_cookie_header(&self, provider_id: &str) -> Result<Option<String>> {
        let store = self
            .inner
            .lock()
            .unwrap_or_else(|poison| poison.into_inner());
        store.read_cookie_header(provider_id)
    }

    fn save_cookie_header(&self, provider_id: &str, cookie_header: &str) -> Result<()> {
        let store = self
            .inner
            .lock()
            .unwrap_or_else(|poison| poison.into_inner());
        store.save_cookie_header(provider_id, cookie_header)
    }

    fn clear_cookie_header(&self, provider_id: &str) -> Result<()> {
        let store = self
            .inner
            .lock()
            .unwrap_or_else(|poison| poison.into_inner());
        store.clear_cookie_header(provider_id)
    }
}

impl Default for DefaultCookieSecretStore {
    fn default() -> Self {
        Self::new()
    }
}

/// Reads and writes encrypted OAuth tokens.
///
/// Mirrors `IOAuthTokenStore`; the default implementation is
/// [`SecureOAuthTokenStore`].
pub trait OAuthTokenStore: Send + Sync {
    /// Reads the saved OAuth token for a provider.
    fn read(&self, provider_id: &str) -> Result<Option<OAuthToken>>;

    /// Saves an OAuth token for a provider.
    fn save(&self, provider_id: &str, token: OAuthToken) -> Result<()>;
}

/// The default [`OAuthTokenStore`]: DPAPI-backed [`SecureOAuthTokenStore`],
/// serialized so the provider can be shared across threads.
pub struct DefaultOAuthTokenStore {
    inner: std::sync::Mutex<SecureOAuthTokenStore>,
}

impl DefaultOAuthTokenStore {
    /// A store in the default data directory using DPAPI.
    pub fn new() -> Self {
        Self {
            inner: std::sync::Mutex::new(SecureOAuthTokenStore::new()),
        }
    }
}

impl OAuthTokenStore for DefaultOAuthTokenStore {
    fn read(&self, provider_id: &str) -> Result<Option<OAuthToken>> {
        let store = self
            .inner
            .lock()
            .unwrap_or_else(|poison| poison.into_inner());
        store.read(provider_id)
    }

    fn save(&self, provider_id: &str, token: OAuthToken) -> Result<()> {
        let store = self
            .inner
            .lock()
            .unwrap_or_else(|poison| poison.into_inner());
        store.save(provider_id, token)
    }
}

impl Default for DefaultOAuthTokenStore {
    fn default() -> Self {
        Self::new()
    }
}

/// A minimal blocking HTTP client shared by every provider.
///
/// The .NET providers took an `HttpClient` with a 30 second timeout. This
/// wrapper keeps that contract: non-2xx statuses are returned to the caller
/// (they become `UsageSnapshot::Failure`s) instead of being raised as errors.
#[derive(Clone)]
pub struct HttpClient {
    agent: ureq::Agent,
}

impl HttpClient {
    /// A client with the default 30 second timeout.
    pub fn new() -> Self {
        let config = ureq::Agent::config_builder()
            .timeout_global(Some(Duration::from_secs(30)))
            .http_status_as_error(false)
            // .NET's HttpClient adds no User-Agent or Accept header by
            // default; the providers set the ones the APIs expect.
            .user_agent("")
            .accept("")
            .build();
        Self {
            agent: config.into(),
        }
    }

    /// A client backed by an existing `ureq` agent.
    pub fn with_agent(agent: ureq::Agent) -> Self {
        Self { agent }
    }

    /// Issues a GET request with the given headers.
    pub fn get(&self, url: &str, headers: &[(&str, &str)]) -> Result<HttpResponse> {
        let mut request = self.agent.get(url);
        for (name, value) in headers {
            request = request.header(*name, *value);
        }

        let response = request
            .call()
            .with_context(|| format!("request to {url} failed"))?;
        read_response(response)
    }

    /// Issues a form-encoded POST (GitHub's OAuth device flow uses these).
    pub fn post_form(
        &self,
        url: &str,
        headers: &[(&str, &str)],
        form: &[(&str, &str)],
    ) -> Result<HttpResponse> {
        let mut request = self.agent.post(url);
        for (name, value) in headers {
            request = request.header(*name, *value);
        }

        let response = request
            .send_form(form.iter().copied())
            .with_context(|| format!("request to {url} failed"))?;
        read_response(response)
    }

    /// Issues a POST request with a JSON body and the given headers.
    pub fn post_json(
        &self,
        url: &str,
        headers: &[(&str, &str)],
        body: &str,
    ) -> Result<HttpResponse> {
        let mut request = self.agent.post(url);
        for (name, value) in headers {
            request = request.header(*name, *value);
        }

        let response = request
            .content_type("application/json; charset=utf-8")
            .send(body)
            .with_context(|| format!("request to {url} failed"))?;
        read_response(response)
    }
}

impl Default for HttpClient {
    fn default() -> Self {
        Self::new()
    }
}

/// A raw HTTP response returned by [`HttpClient`].
pub struct HttpResponse {
    /// HTTP status code.
    pub status: u16,
    /// Response body decoded as UTF-8.
    pub body: String,
}

impl HttpResponse {
    /// Whether the status is in the 2xx range.
    pub fn is_success(&self) -> bool {
        (200..300).contains(&self.status)
    }
}

fn read_response(mut response: ureq::http::Response<ureq::Body>) -> Result<HttpResponse> {
    let status = response.status().as_u16();
    let body = response
        .body_mut()
        .read_to_string()
        .context("failed to read the response body")?;
    Ok(HttpResponse { status, body })
}

/// Creates the provider for `id`, mirroring `UsageProviderFactory.Create`.
pub struct UsageProviderFactory {
    http_client: HttpClient,
    cookie_store: Arc<dyn CookieSecretStore>,
    oauth_token_store: Arc<dyn OAuthTokenStore>,
}

impl UsageProviderFactory {
    /// A factory using the default HTTP client and secure stores.
    pub fn new() -> Self {
        Self {
            http_client: HttpClient::new(),
            cookie_store: Arc::new(DefaultCookieSecretStore::new()),
            oauth_token_store: Arc::new(DefaultOAuthTokenStore::new()),
        }
    }

    /// A factory with injected dependencies (tests, hosts).
    pub fn with_parts(
        http_client: HttpClient,
        cookie_store: Arc<dyn CookieSecretStore>,
        oauth_token_store: Arc<dyn OAuthTokenStore>,
    ) -> Self {
        Self {
            http_client,
            cookie_store,
            oauth_token_store,
        }
    }

    /// Creates the provider registered for `id`.
    pub fn create(&self, id: &str) -> Result<Box<dyn UsageProvider>> {
        let provider: Box<dyn UsageProvider> = match catalog::normalize_id(id).as_str() {
            "codex" => Box::new(codex::CodexProvider::new(self.http_client.clone())),
            "claude" => Box::new(claude::ClaudeProvider::new(
                self.http_client.clone(),
                self.cookie_store.clone(),
            )),
            "copilot" => Box::new(copilot::CopilotProvider::with_oauth_token_store(
                self.http_client.clone(),
                self.oauth_token_store.clone(),
            )),
            "opencode" => Box::new(opencode::OpenCodeProvider::new(
                self.http_client.clone(),
                self.cookie_store.clone(),
            )),
            "zai" => Box::new(zai::ZaiProvider::new(self.http_client.clone())),
            unknown => bail!("Provider '{unknown}' is not supported by Estao MVP."),
        };

        Ok(provider)
    }
}

impl Default for UsageProviderFactory {
    fn default() -> Self {
        Self::new()
    }
}

/// Builds a [`RateWindow`] without absolute amounts.
pub(crate) fn rate_window(
    id: &str,
    title: &str,
    percent_used: Option<f64>,
    reset_at: Option<DateTime<Utc>>,
) -> RateWindow {
    RateWindow {
        id: id.to_string(),
        title: title.to_string(),
        percent_used,
        reset_at,
        used: None,
        limit: None,
        unit: None,
    }
}

/// Builds a successful [`UsageSnapshot`] with the catalog display name.
#[allow(clippy::too_many_arguments)]
pub(crate) fn build_snapshot(
    provider: &str,
    source: &str,
    windows: Vec<RateWindow>,
    account: Option<String>,
    plan: Option<String>,
    credits: Option<CreditsSnapshot>,
) -> UsageSnapshot {
    UsageSnapshot {
        provider: provider.to_string(),
        display_name: catalog::display_name(provider),
        source: source.to_string(),
        updated_at: Utc::now(),
        windows,
        account,
        plan,
        credits,
        error: None,
        is_stale: false,
    }
}

/// Parses a JSON body, adding the response context to the error.
pub(crate) fn parse_json(text: &str, what: &str) -> Result<Value> {
    serde_json::from_str(text).with_context(|| format!("failed to parse {what}"))
}

/// Returns `true` when the text is empty or whitespace only, like
/// `string.IsNullOrWhiteSpace`.
pub(crate) fn is_blank(value: Option<&str>) -> bool {
    value.is_none_or(|value| value.trim().is_empty())
}
