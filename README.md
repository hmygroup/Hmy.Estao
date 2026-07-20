# Estao

Estao is a Windows tray-first .NET 10 port of CodexBar's provider-usage concept for low-memory background operation.

## MVP Scope

- Windows 10 2004+ x64, `net10.0-windows10.0.19041.0`.
- WinForms `NotifyIcon` tray app with native menus and a settings window.
- Compatible raw v1 JSON config at `%APPDATA%\Hmy.Estao\config.json`.
- Override config path with `HMY_ESTAO_CONFIG`.
- Explicit config import only; Estao does not silently copy CodexBar config files.
- Initial providers: Codex, Claude, GitHub Copilot, OpenCode.
- CLI: `estao usage` and `estao config` commands.
- Automatic browser cookie import is opt-in, user-triggered, Chrome/Edge-only, and falls back to manual cookies when Chromium app-bound encryption blocks decryption.
- Background target: hidden tray idle under 75 MB private memory, 0% sustained CPU, and adaptive 2-30 minute refreshes.

## Deferred

Dashboard web scraping, Claude PTY/claude-swap, local cost/history scans, status polling, hooks/HTTP server, agent-aware refresh, custom cards/charts, and broader providers are intentionally out of MVP.

## Build

```powershell
dotnet build
dotnet test
dotnet run --project src\Hmy.Estao.Cli -- usage --format json --pretty
```

## Publish

```powershell
dotnet publish src\Hmy.Estao.App -c Release -r win-x64 --self-contained true
dotnet publish src\Hmy.Estao.Cli -c Release -r win-x64 --self-contained true
```

## Packaging

MSIX/App Installer scaffolding lives under `packaging\`. Release automation expects signing and feed values from CI variables and does not store certificates in this repo.
