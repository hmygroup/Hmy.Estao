//! Interactive OAuth sign-in helpers for the settings window.
//!
//! Wave 7 contract: the GitHub device flow for Copilot (port of
//! `src/Hmy.Estao.App/OAuthLoginService.cs` — request device code, open the
//! verification URL, poll for the token, verify the account login) and
//! terminal launches for `codex login` / `claude login`.
