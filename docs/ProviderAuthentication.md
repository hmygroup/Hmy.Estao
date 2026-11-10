# Desktop provider sign-in

- Startup and newly enabled providers (after **Save changes**) silently probe
  compatible existing CLI sessions. No browser or interactive terminal is opened.
- Manual **Sign in** performs the same probe first. A usable session is adopted;
  otherwise the provider's normal browser/device OAuth or manual-credential flow
  is used. CLI absence, a signed-out CLI, timeout, or invalid status is a fallback,
  not an installation requirement.
- Codex uses the selected profile and `account/read` through its hidden
  `app-server`. Manual fallback uses default-browser PKCE OAuth, localhost port
  1455 and `/auth/callback`, without requiring a CLI. App-owned tokens are DPAPI
  protected and refreshed separately from CLI credentials. This uses Codex's
  native public OAuth client identity, not a separately registered Estao client.
- Claude discovery runs `claude auth status` (JSON is the default). It requires a
  subscription OAuth session with a readable, non-expired access token. Credential
  location honors `CLAUDE_CONFIG_DIR`, otherwise `%USERPROFILE%\.claude`. Usage
  reads the current access token from that file, so CLI-owned token rotation is
  observed. Estao never copies/rotates the CLI's refresh token or edits that file.
- Copilot can reuse `gh auth token` for github.com after verifying Copilot access.
  The token stays native and is encrypted in Estao's store. Unusable GitHub CLI
  authorization falls back to the existing device flow. OpenCode web and Z.ai API
  do not have compatible CLI session readers; their existing credential forms
  remain the fallback.
- Probes run off the UI thread, are bounded and do not print credentials. A 429
  from usage does not by itself mark an authenticated session signed out.
- Explicit **Sign out** disables automatic CLI reconnection for that provider.
  Enabling it again or choosing **Sign in** restores CLI-first discovery. This
  preference is persisted internally; there is no hidden mode the user must edit.
  Claude/GitHub CLI sessions remain untouched by local disconnection. Codex CLI
  sessions adopted by Estao keep the existing explicit CLI logout behavior.

Tests use isolated files, mocked CLI status, a local OAuth callback and mocked
provider HTTP responses. Actual account login remains a provider-side live check.
