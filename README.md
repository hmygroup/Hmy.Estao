# Estao

A Windows tray usage monitor for Codex, Claude, GitHub Copilot, OpenCode and Z.ai, built with Rust and GPUI-CE.

## App

- Tray icon and native menu; left-click opens usage, reset countdowns and history charts.
- Floating GPUI taskbar strip with aligned provider tiles, slim usage bars, threshold colors and a drag handle. Click a provider to open usage; click again or click outside to dismiss it. Moving the pointer does not open or highlight anything. Drag to save the strip's screen position.
- Settings for providers and authentication, refresh interval, launch at sign-in, light/dark theme, per-provider pacing and usage colors.
- Copilot device-code sign-in, terminal-based provider logins and manual cookie/token setup.
- Pacing notifications, adaptive background refresh and locally persisted history.

One executable handles both the GUI and CLI:

```powershell
.\estao.exe                       # tray app
.\estao.exe --settings            # tray app with settings open
.\estao.exe usage
.\estao.exe usage --provider codex --format json --pretty
.\estao.exe config validate
.\estao.exe config dump
.\estao.exe config enable --provider claude
.\estao.exe config disable --provider zai
Get-Clipboard | .\estao.exe config set-cookie --provider claude --stdin
.\estao.exe config clear-cookie --provider claude
Get-Clipboard | .\estao.exe config set-api-key --provider zai --stdin
.\estao.exe config import --file .\config.json
```

Use `--help` on any command for its options. Usage/config commands return `0` on success and `1` for provider, validation or runtime failures; clap argument errors return `2`. API keys supplied through config commands remain in the JSON config; saved cookie headers and OAuth tokens use current-user Windows DPAPI protection. Prefer `--stdin` over putting secrets in shell arguments.

## Configuration and migration

The config stays at `%APPDATA%\Hmy.Estao\config.json`; `HMY_ESTAO_CONFIG` overrides the path. Existing DPAPI cookie/OAuth files and usage-history/pacing-state files remain compatible with the .NET app.

The Rust app reads schema v2 and automatically maps v1 configs in memory. The next save writes v2:

- `taskbarOverlay` becomes `overlay`, retaining enabled state, providers, size and saved position.
- Legacy global pacing is copied to providers without explicit pacing.
- Themes other than `light` or `dark` become `dark`.
- Harness Hub/manager, hooks, backdrop style/opacity, theme presets and overlay display/control customization are removed.

Back up the config before switching branches if you want to retain the removed settings for the .NET app. Config imports are explicit; Estao does not discover or import CodexBar config automatically.

The simplified overlay can still be configured in JSON:

```json
"overlay": {
  "enabled": true,
  "providerIds": ["codex", "claude"],
  "size": "normal"
}
```

Sizes are `compact`, `normal` and `spacious`. Empty `providerIds` shows up to four enabled providers with usage. Optional `positionX`/`positionY` are physical screen pixels; remove both to restore automatic taskbar placement. The strip shares the app's light/dark theme and design tokens. Restart after editing config externally.

## Build and test

Requires Windows x64, stable Rust with the MSVC toolchain, Visual Studio Build Tools with the Desktop development with C++ workload, and the Windows SDK. No .NET SDK or runtime is needed.

```powershell
rustup component add rustfmt clippy
cargo fmt --all -- --check
cargo clippy --locked --workspace --all-targets -- -D warnings
cargo test --locked --workspace
cargo run --bin estao -- --settings
cargo build --locked --release --bin estao
```

The workspace has two crates: `estao-core` (providers, config, HTTP, security, history, pacing and refresh) and `estao` (clap CLI, GPUI windows and Windows tray integration). Provider SVGs and their license are under `assets/ProviderIcons`; keep that directory next to the packaged executable as `assets/ProviderIcons`.

With the app running on an interactive Windows desktop, `pwsh -STA -File scripts/Test-Desktop.ps1` checks click toggling, outside dismissal, absence of hover opening and native geometry/visibility churn while idle. It temporarily moves the pointer and opens a test window; do not use the mouse during this check. This is not a GPU frame-rate benchmark.

CI runs formatting, clippy, tests and a Windows build. The release workflow builds `estao.exe` and packages it with `assets`, this README and `VERSION.txt` in a Windows x64 ZIP. Update the workspace version in `Cargo.toml` and regenerate `Cargo.lock` before pushing a matching `vX.Y.Z` tag, or dispatch the workflow with that version. MSIX/App Installer packaging has been removed.

## Current limitations

- Refresh-interval changes take effect after restart; an already-open popover retains its theme until reopened.
- Popovers close on repeat click, Escape or loss of activation (for example, clicking another app or the desktop). Clicks inside the popover leave it open.
- The strip captures clicks inside its bounds rather than passing unused-area clicks through to the taskbar. Drag follows GPUI frame callbacks instead of a separate timer; inactive windows are not capped at 30 fps, but idle content does not continuously redraw.
- Reset details live in the popover rather than the compact strip; there is no overlay customization panel.
- Multi-monitor/DPI transitions, drag persistence and all provider sign-in flows still need full manual regression testing. No memory/CPU target is claimed without measurement.

The previous .NET implementation, including Harness Hub, remains on `master`; `estao-rs` replaces it rather than maintaining both implementations.
