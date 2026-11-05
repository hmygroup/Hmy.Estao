# Estao Desktop

The replacement desktop project lives entirely here. Only the reusable Core and OAuth sign-in service are shared with the previous app; no Zarpa UI assembly is loaded.

- `ProviderOverlay.cs`: the original rounded, inline taskbar pill with native provider logos (no painted provider names), grip and separators. Percentage, donut and reset are fixed, regardless of legacy overlay display options. Names remain available to accessibility tools. Drag its grip to move; hover or click a provider for details. It follows Windows light/dark personalization and live preference changes, independently of the web windows.
- `TrayApplicationContext.cs`: native tray menu, refresh scheduling, accounts, pacing notifications, and on-demand web window lifetime.
- `DesktopWindow.cs`: Shofia.WebHost custom-chrome windows hosting embedded React assets, without a backend/server. Settings uses the template's frontend title bar and shared minimize/maximize/close controls. The popover has no title bar or branding; its settings button sits at the right of the provider tabs. Closing either window disposes its WebView2 control.
- `Frontend/`: settings and popover. Provider credentials are write-only to the bridge; stored secrets are never returned to JavaScript. An empty credential field keeps the stored value; removal is explicit.
- **Settings → Appearance**: three light themes (Pearl, Ocean, Lavender) and three dark themes (Midnight, Graphite, Aurora). Preview immediately, then Save or Discard. Both web windows share the saved theme; the native overlay is unaffected.

To add a theme, add an entry to [`Frontend/src/themes/themes.json`](Frontend/src/themes/themes.json). This single catalog drives the picker, CSS color tokens, native validation, and startup appearance. See [`Frontend/src/themes/README.md`](Frontend/src/themes/README.md) for the color contract and checks.

`dotnet run --project src/Hmy.Estao.Desktop -- --settings` opens settings. Without the flag only the native tray and enabled overlay start. Building automatically installs and bundles the frontend; Node.js is a build-time requirement only.

## Compatibility

Uses the existing config path, `HMY_ESTAO_CONFIG`, DPAPI cookie/OAuth stores, usage history, and provider accounts. The existing `theme` field stores the web theme ID; the former `Graphite` name maps to Graphite, and unsupported names fall back to Pearl. Legacy backdrop settings, overlay display options, provider alert rules, and Harness Hub sections are retained but ignored by this UI.

On first load without `usageAlerts`, the first enabled provider with custom colors supplies the global rule. If none exists, the first other custom rule is used, then the original defaults (75% amber, 90% red, customization disabled). The rule is copied, not aliased to the old provider setting. Once saved, the explicit global rule is authoritative, including when disabled.

Automatic refresh can be disabled. Initial, manual, account-change, and settings-save refreshes still run. Daily pacing remains provider-specific and uses the existing once-per-day notification store.

NuGet and npm need access to the HMY Azure Artifacts feeds. Configure authentication outside this repository. Do not commit credentials to `NuGet.Config` or `.npmrc`.
