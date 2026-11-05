//! Secure storage for provider cookies and OAuth tokens, ported from
//! `SecureCookieStore.cs` / `SecureOAuthTokenStore.cs`.
//!
//! Secrets are encrypted with Windows DPAPI for the current user and stored
//! in `cookie-secrets.json` / `oauth-secrets.json` next to the config file.
//! The on-disk format matches the original .NET implementation so existing
//! installs keep working.

use std::collections::BTreeMap;
use std::fs;
use std::path::{Path, PathBuf};

use anyhow::{anyhow, bail, Context, Result};
use serde::{Deserialize, Serialize};

use crate::catalog;
use crate::paths;

/// Encrypts/decrypts secret bytes. Swap in a fake in tests.
pub trait SecretProtector {
    fn protect(&self, plaintext: &[u8]) -> Result<Vec<u8>>;
    fn unprotect(&self, protected: &[u8]) -> Result<Vec<u8>>;
}

/// Windows DPAPI protector bound to the current user, ported from
/// `DpapiSecretProtector.cs`.
pub struct DpapiSecretProtector;

#[cfg(windows)]
mod dpapi {
    use anyhow::{Context, Result};
    use windows::core::PCWSTR;
    use windows::Win32::Security::Cryptography::{
        CryptProtectData, CryptUnprotectData, CRYPT_INTEGER_BLOB,
    };
    use windows::Win32::System::Memory::{GetProcessHeap, HeapFree, HEAP_FLAGS};

    pub fn protect(plaintext: &[u8]) -> Result<Vec<u8>> {
        unsafe {
            let input = CRYPT_INTEGER_BLOB {
                cbData: plaintext.len() as u32,
                pbData: plaintext.as_ptr() as *mut u8,
            };
            let mut output = CRYPT_INTEGER_BLOB {
                cbData: 0,
                pbData: std::ptr::null_mut(),
            };
            CryptProtectData(&input, PCWSTR::null(), None, None, None, 0, &mut output)
                .context("DPAPI CryptProtectData failed")?;
            take_blob(output)
        }
    }

    pub fn unprotect(protected: &[u8]) -> Result<Vec<u8>> {
        unsafe {
            let input = CRYPT_INTEGER_BLOB {
                cbData: protected.len() as u32,
                pbData: protected.as_ptr() as *mut u8,
            };
            let mut output = CRYPT_INTEGER_BLOB {
                cbData: 0,
                pbData: std::ptr::null_mut(),
            };
            CryptUnprotectData(&input, None, None, None, None, 0, &mut output)
                .context("DPAPI CryptUnprotectData failed")?;
            take_blob(output)
        }
    }

    unsafe fn take_blob(blob: CRYPT_INTEGER_BLOB) -> Result<Vec<u8>> {
        if blob.pbData.is_null() {
            return Ok(Vec::new());
        }

        let bytes = std::slice::from_raw_parts(blob.pbData, blob.cbData as usize).to_vec();
        // The DPAPI output buffer is process-heap memory (windows 0.62 dropped
        // the deprecated LocalFree, which frees the same heap for fixed blocks).
        if let Ok(heap) = GetProcessHeap() {
            let _ = HeapFree(
                heap,
                HEAP_FLAGS(0),
                Some(blob.pbData as *const core::ffi::c_void),
            );
        }
        Ok(bytes)
    }
}

#[cfg(windows)]
impl SecretProtector for DpapiSecretProtector {
    fn protect(&self, plaintext: &[u8]) -> Result<Vec<u8>> {
        dpapi::protect(plaintext)
    }

    fn unprotect(&self, protected: &[u8]) -> Result<Vec<u8>> {
        dpapi::unprotect(protected)
    }
}

#[cfg(not(windows))]
impl SecretProtector for DpapiSecretProtector {
    fn protect(&self, _: &[u8]) -> Result<Vec<u8>> {
        bail!("DPAPI secret storage is only supported on Windows.");
    }

    fn unprotect(&self, _: &[u8]) -> Result<Vec<u8>> {
        bail!("DPAPI secret storage is only supported on Windows.");
    }
}

/// An OAuth access token with an optional account label.
#[derive(Debug, Clone)]
pub struct OAuthToken {
    pub access_token: String,
    pub account_label: Option<String>,
}

const CURRENT_VERSION: i32 = 1;
const COOKIE_SECRETS_FILE_NAME: &str = "cookie-secrets.json";
const OAUTH_SECRETS_FILE_NAME: &str = "oauth-secrets.json";

#[derive(Serialize, Deserialize, Default)]
struct CookieSecretsFile {
    #[serde(default)]
    version: i32,
    #[serde(default)]
    cookies: BTreeMap<String, String>,
}

#[derive(Serialize, Deserialize, Default)]
struct OAuthSecretsFile {
    #[serde(default)]
    version: i32,
    #[serde(default)]
    tokens: BTreeMap<String, OAuthTokenEntry>,
}

#[derive(Serialize, Deserialize, Default, Clone)]
#[serde(rename_all = "camelCase")]
struct OAuthTokenEntry {
    #[serde(default)]
    protected_token: Option<String>,
    #[serde(default)]
    account_label: Option<String>,
}

/// Stores provider cookie headers encrypted with DPAPI, ported from
/// `SecureCookieStore.cs`.
pub struct SecureCookieStore {
    path: PathBuf,
    protector: Box<dyn SecretProtector>,
}

impl SecureCookieStore {
    /// A store in the default data directory using DPAPI.
    pub fn new() -> Self {
        Self::with_path_and_protector(
            paths::resolve_data_directory().join(COOKIE_SECRETS_FILE_NAME),
            Box::new(DpapiSecretProtector),
        )
    }

    /// A store at an explicit path with a custom protector (tests).
    pub fn with_path_and_protector(path: PathBuf, protector: Box<dyn SecretProtector>) -> Self {
        Self { path, protector }
    }

    /// The file backing this store.
    pub fn path(&self) -> &Path {
        &self.path
    }

    /// Reads and decrypts the saved cookie header for a provider.
    pub fn read_cookie_header(&self, provider_id: &str) -> Result<Option<String>> {
        let file = self.load()?;
        let Some(protected_text) = file.cookies.get(&catalog::normalize_id(provider_id)) else {
            return Ok(None);
        };

        if protected_text.trim().is_empty() {
            return Ok(None);
        }

        let protected_bytes = base64_decode(protected_text)?;
        let plaintext = self.protector.unprotect(&protected_bytes)?;
        Ok(Some(String::from_utf8_lossy(&plaintext).into_owned()))
    }

    /// Encrypts and saves a cookie header for a provider.
    pub fn save_cookie_header(&self, provider_id: &str, cookie_header: &str) -> Result<()> {
        if cookie_header.trim().is_empty() {
            bail!("Cookie header cannot be empty.");
        }

        let mut file = self.load()?;
        let normalized = normalize_cookie_header(cookie_header);
        let protected = self.protector.protect(normalized.as_bytes())?;
        file.cookies.insert(
            catalog::normalize_id(provider_id),
            base64_encode(&protected),
        );
        self.save(file)
    }

    /// Removes a provider's saved cookie header.
    pub fn clear_cookie_header(&self, provider_id: &str) -> Result<()> {
        if !self.path.exists() {
            return Ok(());
        }

        let mut file = self.load()?;
        if file
            .cookies
            .remove(&catalog::normalize_id(provider_id))
            .is_none()
        {
            return Ok(());
        }

        if file.cookies.is_empty() {
            fs::remove_file(&self.path)
                .with_context(|| format!("failed to delete {}", self.path.display()))?;
            return Ok(());
        }

        self.save(file)
    }

    fn load(&self) -> Result<CookieSecretsFile> {
        if !self.path.exists() {
            return Ok(CookieSecretsFile::default());
        }

        let text = fs::read_to_string(&self.path)
            .with_context(|| format!("failed to read {}", self.path.display()))?;
        let file: CookieSecretsFile = serde_json::from_str(&text)
            .with_context(|| format!("failed to parse {}", self.path.display()))?;
        Ok(CookieSecretsFile {
            version: if file.version <= 0 {
                CURRENT_VERSION
            } else {
                file.version
            },
            cookies: file.cookies,
        })
    }

    fn save(&self, mut file: CookieSecretsFile) -> Result<()> {
        if let Some(directory) = self.path.parent() {
            fs::create_dir_all(directory)
                .with_context(|| format!("failed to create {}", directory.display()))?;
        }

        file.version = CURRENT_VERSION;
        let text = serde_json::to_string_pretty(&file).context("failed to serialize cookies")?;
        fs::write(&self.path, text)
            .with_context(|| format!("failed to write {}", self.path.display()))
    }
}

impl Default for SecureCookieStore {
    fn default() -> Self {
        Self::new()
    }
}

/// Stores OAuth access tokens encrypted with DPAPI, ported from
/// `SecureOAuthTokenStore.cs`.
pub struct SecureOAuthTokenStore {
    path: PathBuf,
    protector: Box<dyn SecretProtector>,
}

impl SecureOAuthTokenStore {
    /// A store in the default data directory using DPAPI.
    pub fn new() -> Self {
        Self::with_path_and_protector(
            paths::resolve_data_directory().join(OAUTH_SECRETS_FILE_NAME),
            Box::new(DpapiSecretProtector),
        )
    }

    /// A store at an explicit path with a custom protector (tests).
    pub fn with_path_and_protector(path: PathBuf, protector: Box<dyn SecretProtector>) -> Self {
        Self { path, protector }
    }

    /// The file backing this store.
    pub fn path(&self) -> &Path {
        &self.path
    }

    /// Reads and decrypts the saved OAuth token for a provider.
    pub fn read(&self, provider_id: &str) -> Result<Option<OAuthToken>> {
        let file = self.load()?;
        let Some(entry) = file.tokens.get(&catalog::normalize_id(provider_id)) else {
            return Ok(None);
        };

        let Some(protected_text) = entry.protected_token.as_deref() else {
            return Ok(None);
        };

        if protected_text.trim().is_empty() {
            return Ok(None);
        }

        let protected_bytes = base64_decode(protected_text)?;
        let plaintext = self.protector.unprotect(&protected_bytes)?;
        let token = String::from_utf8_lossy(&plaintext).into_owned();
        if token.trim().is_empty() {
            return Ok(None);
        }

        Ok(Some(OAuthToken {
            access_token: token,
            account_label: entry.account_label.clone(),
        }))
    }

    /// Encrypts and saves an OAuth token for a provider.
    pub fn save(&self, provider_id: &str, token: OAuthToken) -> Result<()> {
        if token.access_token.trim().is_empty() {
            bail!("OAuth access token cannot be empty.");
        }

        let mut file = self.load()?;
        let protected = self
            .protector
            .protect(token.access_token.trim().as_bytes())?;
        file.tokens.insert(
            catalog::normalize_id(provider_id),
            OAuthTokenEntry {
                protected_token: Some(base64_encode(&protected)),
                account_label: token
                    .account_label
                    .as_deref()
                    .map(str::trim)
                    .filter(|label| !label.is_empty())
                    .map(|label| label.to_string()),
            },
        );
        self.save_file(file)
    }

    fn load(&self) -> Result<OAuthSecretsFile> {
        if !self.path.exists() {
            return Ok(OAuthSecretsFile::default());
        }

        let text = fs::read_to_string(&self.path)
            .with_context(|| format!("failed to read {}", self.path.display()))?;
        let file: OAuthSecretsFile = serde_json::from_str(&text)
            .with_context(|| format!("failed to parse {}", self.path.display()))?;
        Ok(file)
    }

    fn save_file(&self, mut file: OAuthSecretsFile) -> Result<()> {
        if let Some(directory) = self.path.parent() {
            fs::create_dir_all(directory)
                .with_context(|| format!("failed to create {}", directory.display()))?;
        }

        file.version = CURRENT_VERSION;
        let text = serde_json::to_string_pretty(&file).context("failed to serialize tokens")?;
        fs::write(&self.path, text)
            .with_context(|| format!("failed to write {}", self.path.display()))
    }
}

impl Default for SecureOAuthTokenStore {
    fn default() -> Self {
        Self::new()
    }
}

fn normalize_cookie_header(cookie_header: &str) -> String {
    let trimmed = cookie_header.trim();
    if trimmed.len() >= 7 && trimmed[..7].eq_ignore_ascii_case("Cookie:") {
        trimmed[7..].trim().to_string()
    } else {
        trimmed.to_string()
    }
}

fn base64_encode(bytes: &[u8]) -> String {
    use base64::Engine;
    base64::engine::general_purpose::STANDARD.encode(bytes)
}

fn base64_decode(text: &str) -> Result<Vec<u8>> {
    use base64::Engine;
    base64::engine::general_purpose::STANDARD
        .decode(text.trim())
        .map_err(|error| anyhow!("invalid base64 secret: {error}"))
}

#[cfg(test)]
mod tests {
    use super::*;

    struct ReversingSecretProtector;

    impl SecretProtector for ReversingSecretProtector {
        fn protect(&self, plaintext: &[u8]) -> Result<Vec<u8>> {
            Ok(plaintext.iter().rev().copied().collect())
        }

        fn unprotect(&self, protected: &[u8]) -> Result<Vec<u8>> {
            Ok(protected.iter().rev().copied().collect())
        }
    }

    fn temp_path(name: &str) -> PathBuf {
        static COUNTER: std::sync::atomic::AtomicUsize = std::sync::atomic::AtomicUsize::new(0);
        let unique = COUNTER.fetch_add(1, std::sync::atomic::Ordering::Relaxed);
        std::env::temp_dir().join(format!(
            "estao-security-{}-{}-{name}.json",
            std::process::id(),
            unique
        ))
    }

    // Ported from SecureCookieStoreTests.save_read_and_clear_round_trips_cookie_header.
    #[test]
    fn save_read_and_clear_round_trips_cookie_header() {
        let path = temp_path("cookie-secrets");
        let store = SecureCookieStore::with_path_and_protector(
            path.clone(),
            Box::new(ReversingSecretProtector),
        );

        store
            .save_cookie_header("Claude", "Cookie: sessionKey=abc; other=123")
            .unwrap();

        assert_eq!(
            store.read_cookie_header("claude").unwrap(),
            Some("sessionKey=abc; other=123".to_string())
        );

        store.clear_cookie_header("claude").unwrap();

        assert_eq!(store.read_cookie_header("claude").unwrap(), None);
        assert!(!path.exists());
    }

    // Ported from SecureCookieStoreTests.saved_cookie_file_does_not_contain_plaintext_secret.
    #[test]
    fn saved_cookie_file_does_not_contain_plaintext_secret() {
        let path = temp_path("cookie-secrets");
        let store = SecureCookieStore::with_path_and_protector(
            path.clone(),
            Box::new(ReversingSecretProtector),
        );

        store
            .save_cookie_header("opencode", "auth=secret-value")
            .unwrap();

        let stored_text = fs::read_to_string(&path).unwrap();
        assert!(!stored_text.contains("secret-value"));
    }

    // Ported from SecureCookieStoreTests.save_rejects_empty_cookie_header.
    #[test]
    fn save_rejects_empty_cookie_header() {
        let path = temp_path("cookie-secrets");
        let store =
            SecureCookieStore::with_path_and_protector(path, Box::new(ReversingSecretProtector));

        assert!(store.save_cookie_header("claude", "   ").is_err());
    }

    #[test]
    fn oauth_tokens_round_trip() {
        let path = temp_path("oauth-secrets");
        let store = SecureOAuthTokenStore::with_path_and_protector(
            path.clone(),
            Box::new(ReversingSecretProtector),
        );

        store
            .save(
                "copilot",
                OAuthToken {
                    access_token: "gho_token".to_string(),
                    account_label: Some("someone".to_string()),
                },
            )
            .unwrap();

        let token = store.read("copilot").unwrap().unwrap();
        assert_eq!(token.access_token, "gho_token");
        assert_eq!(token.account_label.as_deref(), Some("someone"));
        assert!(store.read("claude").unwrap().is_none());

        let stored_text = fs::read_to_string(&path).unwrap();
        assert!(!stored_text.contains("gho_token"));
    }
}
