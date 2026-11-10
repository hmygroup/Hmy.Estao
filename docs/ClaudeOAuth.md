# Claude subscription usage authentication

Desktop's Claude **Sign in** uses the default browser and OAuth Authorization Code
with PKCE (S256), not WebView cookies or an installed CLI. Estao binds an IPv4
loopback listener before opening the browser and uses
`http://localhost:<ephemeral-port>/callback`. A random state must match before any
code is exchanged. Invalid callbacks do not terminate the pending login.

## Provider compatibility boundary

The authorization request uses Claude Code's public native client identity
`9d1c250a-e61b-44d9-88ed-5944d1962f5e`. **This is a compatibility integration, not an
independently registered or approved Estao OAuth application.** The consent page
may identify the native Claude client. Provider acceptance is not guaranteed.

The loopback route and JSON token-exchange format were checked against the
published `@anthropic-ai/claude-code@2.1.37` protocol implementation. No CLI is
installed or executed. The requested scope is only `user:profile`; no inference
scope is requested.

- Authorization: `https://claude.ai/oauth/authorize`
- Exchange/refresh: `https://platform.claude.com/v1/oauth/token`
- Usage: `https://api.anthropic.com/api/oauth/usage`, with bearer authentication
  and `anthropic-beta: oauth-2025-04-20`.

The callback page acknowledges receipt of authorization, not successful usage
access. Estao validates the granted scope and makes a usage request before saving
credentials or reporting successful sign-in. Token-exchange and usage-access
failures display separate localized errors with the HTTP status, never response
bodies or secrets. A live provider login is still required to establish acceptance;
mocked tests establish protocol handling, not provider approval.

## Storage and lifetime

Access and refresh tokens are separately DPAPI-encrypted in Estao's own
`oauth-secrets.json`; only expiry, granted scopes and optional account label are
unencrypted metadata. Writes are serialized and atomic. Expiring access tokens are
refreshed and rotated; late refresh writes cannot undo a sign-out or overwrite a
new login. Desktop does not write or delete Claude Code's credential files.

Sign-in has a five-minute deadline and is canceled when Settings closes or the
user chooses Cancel. The listener then closes. The browser tab can be closed after
the callback; it has no tokens or code embedded in its response page. Explicit
OAuth usage mode reads Estao-owned tokens. CLI-first discovery on startup,
enabling, and manual sign-in can automatically select an existing CLI session,
regardless of the previously saved mode. See [provider authentication](ProviderAuthentication.md).

## Server-directed usage retries

When a Claude usage request returns HTTP 429 with a positive `Retry-After` delay
or a future HTTP date, the provider records an in-memory retry deadline. Desktop
uses a separate one-shot native timer to retry **Claude only** at that deadline,
independently of the normal configured refresh interval. No request or UI thread
sleeps until the deadline. Automatic refresh and the Claude provider must remain
enabled; shutdown, disabling refresh, and sign-out stop the pending timer.

Settings authentication checks and usage refreshes share the same factory and
cooldown, so manual refresh or reopening Settings does not send another usage
request before the deadline. Another 429 can replace the deadline; success clears
it. Missing, invalid, zero, or past `Retry-After` values do not invent a delay:
normal configured refresh behavior remains in place. Deadlines are not persisted
across app restarts. Initial OAuth authorization/token validation is separate
from this usage-refresh scheduler.
