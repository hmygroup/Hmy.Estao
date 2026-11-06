# Estao Desktop

The replacement desktop project lives entirely here. Core, OAuth sign-in, and Harness Hub credential discovery are shared with the previous app; no Zarpa UI assembly is loaded.

- `ProviderOverlay.cs`: the original rounded, inline taskbar pill with native provider logos (no painted provider names), grip and separators. Percentage, donut and reset are fixed, regardless of legacy overlay display options. Names remain available to accessibility tools. Drag its grip to move; hover or click a provider for details. It follows Windows light/dark personalization and live preference changes, independently of the web windows.
- `TrayApplicationContext.cs`: native tray menu, refresh scheduling, accounts, pacing notifications, and on-demand web window lifetime.
- `DesktopWindow.cs`: Shofia.WebHost custom-chrome windows hosting embedded React assets, without a backend/server. Settings uses the template's frontend title bar and shared minimize/maximize/close controls. The popover has no title bar or branding; its settings button sits at the right of the provider tabs. Closing either window disposes its WebView2 control.
- `Frontend/`: settings and popover. Provider credentials are write-only to the bridge; stored secrets are never returned to JavaScript. An empty credential field keeps the stored value; removal is explicit.
- **Settings → Appearance**: three light themes (Pearl, Ocean, Lavender) and three dark themes (Midnight, Graphite, Aurora). Preview immediately, then Save or Discard. Both web windows share the saved theme; the native overlay is unaffected.
- **Settings → Harness Hub**: review and install shared skills for Codex, Claude Code, GitHub Copilot CLI, and OpenCode. Missing tools offer an official installation website instead of config installation. The repository button opens GitHub in the default browser.

To add a theme, add an entry to [`Frontend/src/themes/themes.json`](Frontend/src/themes/themes.json). This single catalog drives the picker, CSS color tokens, native validation, and startup appearance. See [`Frontend/src/themes/README.md`](Frontend/src/themes/README.md) for the color contract and checks.

`dotnet run --project src/Hmy.Estao.Desktop -- --settings` opens settings. Without the flag only the native tray and enabled overlay start. Building automatically installs and bundles the frontend; Node.js is a build-time requirement only.

## Compatibility

Uses the existing config path, `HMY_ESTAO_CONFIG`, DPAPI cookie/OAuth stores, usage history, and provider accounts. The existing `theme` field stores the web theme ID; the former `Graphite` name maps to Graphite, and unsupported names fall back to Pearl. Legacy backdrop settings, overlay display options, provider alert rules, and Harness Hub sections are retained but ignored by this UI.

## Harness Hub

The Hub is a Settings page, after Usage alerts. It reuses `Core/Harnesses/Installer`, not the historical marketplace/installer services. Opening the Hub checks tools and verifies GitHub access; it does not install anything. Detection checks PATH and common npm, Bun, local-bin, OpenCode, and WinGet locations. **Check again** rechecks after installing a tool. No harness executable or repository script is run.

Choose **Review installation / update**, or **Install all…** to prepare every detected tool from a single commit. Review shows skill descriptions from validated `SKILL.md` metadata, New/Update/Already present/Local conflict states, personal destinations, requirements, exclusions, and optional file details. Compatible skills are selected by default; resource groups stay whole and dependency selections stay consistent. Confirm **Install selected** to write. Install all skips absent tools, preserves local conflicts, and continues other tools after a failure. Each tool has a separate recoverable transaction; the entire batch is not globally atomic.

The existing Copilot OAuth token is reused only after proving access to the private repository. Otherwise use the dedicated GitHub device flow; Copilot scopes and its saved token are never replaced. Tokens never cross the frontend bridge. Existing dedicated authorizations, `HMY_ESTAO_GITHUB_TOKEN`, and optional `gh` authorization are also supported. The manifest/reviewed-bootstrap restrictions described in `docs/HarnessInstaller.md` still apply; changed upstream revisions without a compatible manifest can block installation.

The session and downloaded plans exist only while Settings is open. Cancel or close Settings to cancel work; safe rollback finishes before payload cleanup. Persistent authorization and installer state use the existing `harness-github` and `harness-installer` directories beside `config.json`. An interrupted transaction exposes **Recover** in the tool card. General Settings Save/Discard do not apply or undo Hub installations.

`dotnet test Hmy.Estao.slnx -c Release` covers the source contract, installer, and Hub session with mock GitHub responses and isolated profiles. `npm test --prefix src/Hmy.Estao.Desktop/Frontend` covers skill selection and dependencies. Tests do not need a real GitHub authorization; the existing real-source integration test remains explicitly opt-in.

On first load without `usageAlerts`, the first enabled provider with custom colors supplies the global rule. If none exists, the first other custom rule is used, then the original defaults (75% amber, 90% red, customization disabled). The rule is copied, not aliased to the old provider setting. Once saved, the explicit global rule is authoritative, including when disabled.

Automatic refresh can be disabled. Initial, manual, account-change, and settings-save refreshes still run. Daily pacing remains provider-specific and uses the existing once-per-day notification store.

NuGet and npm need access to the HMY Azure Artifacts feeds. Configure authentication outside this repository. Do not commit credentials to `NuGet.Config` or `.npmrc`.
