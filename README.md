# Estao

Estao is a Windows tray-first .NET 10 port of CodexBar's provider-usage concept for low-memory background operation.

## MVP Scope

- Windows 10 2004+ x64, `net10.0-windows10.0.19041.0`.
- `Hmy.Estao.Desktop` replaces `Hmy.Estao.App`: WinForms tray and provider overlay, with React settings and usage popover hosted in Shofia.WebHost.
- The overlay always shows Percentage, Donut, and Reset time. It never creates a WebView.
- The overlay retains its original rounded taskbar appearance and follows Windows light/dark personalization, including live changes.
- WebView2 is created only when opening settings or the popover; closing the window disposes it. No ASP.NET server or background Vite process is used.
- Global usage alert thresholds and colors apply to every provider; provider configuration and daily pacing remain provider-specific.
- Compatible raw v1 JSON config at `%APPDATA%\Hmy.Estao\config.json`.
- Override config path with `HMY_ESTAO_CONFIG`.
- Explicit config import only; Estao does not silently copy CodexBar config files.
- Initial providers: Codex, Claude, GitHub Copilot, OpenCode, Z.ai.
- CLI: `estao usage` and `estao config` commands.
- Appearance themes, backdrop controls, and Harness Hub are not part of the first Desktop version. Existing config sections are retained for compatibility.
- Cookie-based providers use saved manual cookies; cookies are stored locally with Windows DPAPI protection instead of reading Chrome/Edge browser databases.
- Background target: hidden tray idle under 75 MB private memory, 0% sustained CPU, and adaptive 2-30 minute refreshes.

## Legacy Harness hub

The following describes the previous `Hmy.Estao.App` UI, which remains in source but is no longer built or packaged by the solution. Harness Hub is not exposed in the new Desktop frontend.

Open **Harness Hub...** from the tray menu, or launch it from **Settings > Harness hub**. The dedicated Zarpa workspace separates team discovery from local desired-state management:

- Browse multiple repositories using switchable cards and table views, search, artifact type, harness, and team filters.
- Publish individual immutable skills, MCP servers, and other harness artifacts from discovered local content; import v3 artifacts or compose versioned collections and snapshots.
- See explicit Native, Converted, Partial, or Unsupported compatibility for Codex, Claude Code, GitHub Copilot, and OpenCode.
- Adopt personal or project environments, stage artifact toggles, preview every operation, and apply desired state atomically.
- Detect byte drift for every managed file and semantic drift for structured JSON/MCP configuration.
- Remove managed content while either keeping local files or creating a restore point and deleting them.
- Store personal desired state in Estao data and project desired state in `.estao/environment.json`.
- Retain bulk harness configuration without erasing settings Estao does not manage.
- Convert portable content between harness-native locations and formats. For example, Codex `AGENTS.md`, Agent Skills, TOML agents, and MCP configuration are translated to Copilot instructions, skills, Markdown agents, and `mcp-config.json`.

Every synchronization creates a restore point under `<base>/.estao/backups`; the latest ten complete points per environment are retained. Estao recognizes both current `~/.agents/skills` and legacy `~/.codex/skills` user skills, excluding bundled `.system` skills. Codex plugin artifacts contain marketplace and enablement declarations rather than the downloaded plugin cache. Known literal credentials are replaced with environment placeholders during publishing, reparse-point directories are not followed, payload paths and hashes are verified, and hooks/rules/plugins/raw settings are not copied across harnesses when no safe mapping exists. Raw settings are available but disabled by default.

For a direct settings-only launch, use:

```powershell
Hmy.Estao.exe --settings
```

## Deferred

Dashboard web scraping, Claude PTY/claude-swap, local cost/history scans, status polling, hooks/HTTP server, agent-aware refresh, custom cards/charts, and broader providers are intentionally out of MVP.

## Build

Requires .NET 10, Node.js 22.12+ (or 24+), and access to the HMY Azure Artifacts NuGet/npm feeds. Configure any required credentials in your user/runner credential stores, never in source. The frontend `.npmrc` routes only `@shofia` packages to the internal feed; public packages use npmjs. CI runners need the same feed access.

The desktop build installs locked frontend dependencies, type-checks, builds Vite assets, and embeds them. End users need the WebView2 Evergreen Runtime, but not Node.js. The original Graphite palette is fixed for this version.

```powershell
dotnet build
dotnet test
dotnet run --project src\Hmy.Estao.Desktop -- --settings
dotnet run --project src\Hmy.Estao.Cli -- usage --format json --pretty
```

## Cookie setup

For providers that require web cookies, save a cookie header once and Estao will reuse it for refreshes:

```powershell
Get-Clipboard | dotnet run --project src\Hmy.Estao.Cli -- config set-cookie --provider claude --stdin
dotnet run --project src\Hmy.Estao.Cli -- config clear-cookie --provider claude
```

The saved cookie is encrypted for the current Windows user with DPAPI. Existing `cookieHeader` values in `config.json` are still read as a legacy fallback.

## Publish

```powershell
dotnet publish src\Hmy.Estao.Desktop -c Release -r win-x64 --self-contained true
dotnet publish src\Hmy.Estao.Cli -c Release -r win-x64 --self-contained true
```

## Packaging

MSIX/App Installer scaffolding lives under `packaging\`. Release automation expects signing and feed values from CI variables and does not store certificates in this repo.
