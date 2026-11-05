# Desktop themes

`themes.json` is the single catalog used by React **and** embedded in the native host for validation. Add a new entry, rebuild, and it automatically appears in Settings → Appearance. No enum, switch statement, or new stylesheet is needed.

Each entry needs a unique lowercase `id`, a `name`, `mode` (`light` or `dark`), `description`, `aliases`, and the same eleven hex color keys as the existing entries. IDs are stored in the existing config `theme` field. Aliases read older names; unknown names fall back to the first entry, Pearl. The former `Graphite` setting resolves case-insensitively to the new Graphite theme.

- `canvas`, `canvas-end`: background gradient.
- `text`, `muted`: primary and secondary foregrounds; target at least 7:1 and 4.5:1 contrast against the canvas respectively.
- `accent`: focus rings, selected navigation, and default usage indicators.
- `primary`, `primary-light`, `primary-hover`: filled buttons and switches; button text is white.
- `glow-a`, `glow-b`, `glow-c`: static background reflections.

Shared material and layout rules live in `../liquid-glass.css`. They derive translucent surfaces, borders, shadows, and input styles from these tokens and the light/dark mode. Do not add per-theme component CSS. Theme changes must not override configured warning/critical usage colors.

The picker previews unsaved changes locally. Save persists the ID through the native bridge; Discard restores the saved theme. Only the saved ID is cached in local storage for browser previews. The real desktop host injects its configured ID before page scripts run to prevent a light/dark startup flash. The native overlay is deliberately unaffected and follows Windows personalization.

Run `dotnet test tests/Hmy.Estao.Desktop.Tests` to check catalog integrity, palette contrast, persistence, and overlay independence. When intentionally changing the number of built-in presets, update the count assertion in `DesktopThemeTests`.
