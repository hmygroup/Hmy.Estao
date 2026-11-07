# Localization

Settings → General → Language selects **System language**, **English**, **Español** or **Français**. Save applies the language to the web UI, native window titles, tray menu, overlay accessibility and subsequent notifications. The preference is stored in `config.json` as `language` (`system`, `en`, `es`, `fr`). Old configurations default to `system`; unsupported system languages fall back to English. Regional French preferences such as `fr-FR` and `fr-CA` resolve to French. The bridge also returns the effective native language so WebView and WinForms agree even if the browser's preferred language differs from Windows.

## Resources and formatting

- `Frontend/src/locales/en.json` is the fallback catalog; `es.json` supplies Spanish and `fr.json` supplies French. All are bundled into the website and embedded in the Desktop assembly. Native UI does not need a WebView, network connection or frontend process to translate text.
- `Frontend/src/i18n.ts` formats frontend messages, plural forms, dates, times, percentages and numbers. It has no native-host dependency. Browser storage is optional.
- `DesktopLocalization.cs` formats the same message codes for native surfaces without changing the process culture or Core's parsing behavior.
- Use stable keys and numbered arguments (`{0}`, `{1}`). Do not concatenate translated sentence fragments. Plural entries use `_one` and `_other`; `plural` uses `Intl.PluralRules`. Additional categories (such as French `many` for millions) fall back to `_other` unless explicitly translated.
- The app name is a separate `{appName}` argument supplied by `DesktopBranding.DisplayName` for native text and `Frontend/src/branding.json` for the frontend. HTML title branding is escaped during the frontend build. Technical identifiers and saved-data locations do not follow branding or language changes.

## Core messages

`Core/Models/UserMessage.cs` defines a culture-independent `{ code, arguments }` contract. Usage failures expose `ErrorMessage`, windows expose `TitleMessage`, generated account labels expose `LabelMessage`, and configuration validation has `ValidateMessages`. Installer progress supports `IUserMessageProgress` while keeping the old string progress callback available to legacy consumers.

For a known failure, attach presentation metadata without changing its exception type or diagnostic:

```csharp
throw new InvalidOperationException("English diagnostic for logs and legacy clients.")
    .WithUserMessage("hub.review_changed");
```

Core does not load translation resources. The Desktop bridge returns structured command errors; the frontend translates them locally. Codes, booleans and operation identifiers—not English message text—determine authentication and cancellation UI states. Cached structured messages can therefore be rendered again after switching languages.

Unclassified internal exceptions use a translated safe category (network, permission, file I/O, invalid data, or general operation failure). Raw exception text and remote response bodies are not used as frontend copy. Existing diagnostic strings remain for legacy/CLI compatibility; this change does not localize the retired WinForms app or CLI. Provider/product names, user-authored labels, paths, repository skill descriptions, requirements and exclusion explanations remain source content, not application translations.

## Adding a language or message

1. Add a catalog beside `en.json`, with matching keys and argument placeholders.
2. Register the locale in `i18n.ts` and `DesktopLocalization.cs`, including validation.
3. Add its autonym to the language selector in `App.tsx`.
4. Add messages in both catalogs and pass code/data from Core. Never add credentials or raw server errors to message arguments.
5. Run frontend tests/build and `dotnet test Hmy.Estao.slnx -c Release`. Check all screens at minimum window size with longer translations.

Tests cover catalog parity, placeholders, fallback, disabled browser storage, plural/number formatting, message serialization, language persistence, authentication-state independence, native overlay text and live tray updates. Native persistence tests use disabled providers and an isolated configuration, without signing in/out real accounts or changing Windows startup registration.
