# Estao

Estao is a Windows tray-first .NET 10 port of CodexBar's provider-usage concept for low-memory background operation.

## MVP Scope

- Windows 10 2004+ x64, `net10.0-windows10.0.19041.0`.
- WinForms `NotifyIcon` tray app with native menus and a settings window.
- Compatible raw v1 JSON config at `%APPDATA%\Hmy.Estao\config.json`.
- Override config path with `HMY_ESTAO_CONFIG`.
- Explicit config import only; Estao does not silently copy CodexBar config files.
- Initial providers: Codex, Claude, GitHub Copilot, OpenCode, Z.ai.
- CLI: `estao usage` and `estao config` commands.
- GitHub-backed personal configuration installer for Codex, Claude Code, GitHub Copilot CLI, and OpenCode.
- Cookie-based providers use saved manual cookies; cookies are stored locally with Windows DPAPI protection instead of reading Chrome/Edge browser databases.
- Background target: hidden tray idle under 75 MB private memory, 0% sustained CPU, and adaptive 2-30 minute refreshes.

## Harness hub

Abre **Harness Hub...** desde la bandeja o **Settings > Harness hub**, conecta GitHub si es necesario y pulsa **Instalar** en el harness elegido. El origen es el repositorio privado `hmygroup/hmy-prompt-engineer`.

- Cuatro acciones personales; sin catálogo, propietarios, publicación ni selección manual de artefactos.
- Se utiliza un snapshot inmutable y un contrato explícito de compatibilidad. Un bootstrap incluido cubre únicamente su commit revisado; revisiones nuevas sin manifiesto requieren revisión, no se incorporan a ciegas.
- Se copian skills completas con sus recursos/licencias. No se ejecutan scripts ni se instalan dependencias; los prompts ChatGPT y herramientas específicas de mantenimiento se clasifican y excluyen explícitamente.
- Archivos idénticos: no-op. Archivos ajenos o modificados: conservar y comunicar conflicto. Solo se actualizan automáticamente archivos administrados que siguen intactos tras pulsar Actualizar.
- Estado y journal recuperable separados de los datos legacy. La recuperación no reemplaza cambios posteriores del usuario. No se promete atomicidad global del filesystem.
- La autorización del instalador es independiente de Copilot; una sesión de identidad no demuestra acceso al repositorio privado.

Consulta [seguridad, autorización, cobertura y contrato](docs/HarnessInstaller.md). Los backups `.estao` y la configuración histórica se conservan; el nuevo Hub no publica ni elimina paquetes o contenido legacy.

For a direct settings-only launch, use:

```powershell
Hmy.Estao.exe --settings
Hmy.Estao.exe --harness-hub
```

`--harness-publish` informa de la retirada de publicación y abre el nuevo Hub.

## Deferred

Dashboard web scraping, Claude PTY/claude-swap, local cost/history scans, status polling, hooks/HTTP server, agent-aware refresh, custom cards/charts, and broader providers are intentionally out of MVP.

## Build

```powershell
dotnet build
dotnet test
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
dotnet publish src\Hmy.Estao.App -c Release -r win-x64 --self-contained true
dotnet publish src\Hmy.Estao.Cli -c Release -r win-x64 --self-contained true
```

## Packaging

MSIX/App Installer scaffolding lives under `packaging\`. Release automation expects signing and feed values from CI variables and does not store certificates in this repo.
