import { useEffect, useLayoutEffect, useState, type ReactNode } from "react";
import { command, getState, saveSettings, signInProvider, WindowControls } from "./nativeHost";
import type { DesktopState, HistoryPoint, Provider, RateWindow, SettingsUpdate, UsageAlerts } from "./types";
import { applyTheme, cachedTheme, cacheTheme, resolveTheme } from "./themes";
import ThemePicker from "./themes/ThemePicker";
import HarnessHub from "./HarnessHub";
import { connectionPresentation } from "./providerConnection";
import { appName } from "./branding";
import { t, plural, message as formatMessage, setLanguage, currentLanguage, dateTime, time, percent, number, windowTitle, sourceName, errorText } from "./i18n";

function Icon({ id }: { id: string }) {
  return <img className="provider-icon" src={`/providers/${id}.svg`} alt="" />;
}
function Toggle({ label, checked, onChange, disabled = false }: {
  label: string; checked: boolean; onChange: (value: boolean) => void; disabled?: boolean;
}) {
  return <label className="toggle"><span>{label}</span><input type="checkbox" role="switch"
    checked={checked} onChange={e => onChange(e.target.checked)} disabled={disabled} /></label>;
}
function Field({ label, children }: { label: string; children: ReactNode }) {
  return <label className="field"><span>{label}</span>{children}</label>;
}
function Section({ title, description, children }: { title: string; description?: string; children: ReactNode }) {
  return <section className="section"><h2>{title}</h2>{description && <p>{description}</p>}{children}</section>;
}

export default function App() {
  const [state, setState] = useState<DesktopState>();
  const [error, setError] = useState("");
  const [previewTheme, setPreviewTheme] = useState<string | null>(null);
  useLayoutEffect(() => { applyTheme(previewTheme ?? state?.theme ?? cachedTheme()); }, [previewTheme, state?.theme]);
  useEffect(() => { if (state?.theme) cacheTheme(state.theme); }, [state?.theme]);
  const load = async () => { try { const next = await getState(); setLanguage(next.effectiveLanguage || next.language || "system"); setState(next); } catch (e) { setError(errorText(e)); } };
  useEffect(() => {
    void load();
    const timer = window.setInterval(() => void load(), state?.providerSignIn?.pending ? 500 : 4000);
    return () => clearInterval(timer);
  }, [state?.providerSignIn?.pending]);
  const view = state?.view || new URLSearchParams(window.location.search).get("view") || "settings";
  useLayoutEffect(() => { document.title = `${appName} · ${t(view === "popover" ? "usage" : "settings")}`; }, [view, state?.language, currentLanguage()]);
  return <div className={`desktop-shell${view === "popover" ? " popover-shell" : ""}`}>
    {view !== "popover" && <header className="window-header"><span>{appName} · {t("settings")}</span><WindowControls /></header>}
    {!state ? <main className="loading" aria-busy={!error}>{error && <p role="alert">{error}</p>}</main>
      : state.view === "settings" ? <Settings state={state} reload={load} previewTheme={setPreviewTheme} /> : <Popover state={state} reload={load} />}
  </div>;
}

function Settings({ state, reload, previewTheme }: { state: DesktopState; reload: () => Promise<void>; previewTheme: (id: string | null) => void }) {
  const normalizeDraft = (value: DesktopState): SettingsUpdate => ({ ...structuredClone(value), theme: resolveTheme(value.theme).id });
  const [draft, setDraft] = useState<SettingsUpdate>(() => normalizeDraft(state));
  useEffect(() => { previewTheme(draft.theme); return () => previewTheme(null); }, [draft.theme, previewTheme]);
  const [page, setPage] = useState("general");
  const [dirty, setDirty] = useState(false);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const change = (update: Partial<SettingsUpdate>) => { setDraft(d => ({ ...d, ...update })); setDirty(true); setMessage(""); };
  const providerChange = (id: string, update: Partial<Provider>) =>
    change({ providers: draft.providers.map(p => p.id === id ? { ...p, ...update } : p) });
  const alertChange = (update: Partial<UsageAlerts>) => change({ usageAlerts: { ...draft.usageAlerts, ...update } });
  const save = async () => {
    setBusy(true); setMessage("");
    try {
      await saveSettings(draft);
      const fresh = await getState();
      setDraft(normalizeDraft(fresh)); setDirty(false); setMessage("");
      await reload();
    } catch (e) { setMessage(errorText(e)); }
    finally { setBusy(false); }
  };
  const discard = async () => {
    try { setDraft(normalizeDraft(await getState())); setDirty(false); setMessage(""); }
    catch (e) { setMessage(errorText(e)); }
  };
  const run = async (action: string, provider?: string) => {
    try { await command(action, provider); await reload(); }
    catch (e) { setMessage(errorText(e)); }
  };
  const providerSignIn = async (provider: Provider) => {
    setMessage("");
    try {
      const source = provider.id === "zai" ? "api" : provider.id === "opencode" ? "web"
        : provider.id === "copilot" ? (provider.source === "api" ? "api" : "oauth")
        : provider.id === "claude" ? "oauth"
        : ["auto", "cli", "oauth"].includes(provider.source) ? provider.source : "auto";
      await signInProvider({ ...provider, source, cookieSource: source === "web" ? "manual" : provider.cookieSource });
      const fresh = await getState();
      const saved = fresh.providers.find(p => p.id === provider.id)!;
      setDraft(d => ({ ...d, providers: d.providers.map(p => p.id === provider.id
        ? { ...p, source: saved.source, cookieSource: saved.cookieSource, apiKey: "", newCookie: "", clearApiKey: false, clearCookie: false } : p) }));
      await reload();
    } catch (e) { setMessage(errorText(e)); }
  };
  const providerSignOut = async (id: string) => {
    setDraft(d => ({ ...d, providers: d.providers.map(p => p.id === id ? { ...p, apiKey: "", newCookie: "", clearApiKey: false, clearCookie: false } : p) }));
    await run("signout", id);
  };
  useEffect(() => {
    const listener = (event: BeforeUnloadEvent) => { if (dirty) event.preventDefault(); };
    window.addEventListener("beforeunload", listener);
    return () => window.removeEventListener("beforeunload", listener);
  }, [dirty]);
  const titles: Record<string, [string, string]> = {
    general: [t("general"), t("general.description")],
    appearance: [t("appearance"), t("appearance.description")],
    providers: [t("providers"), t("providers.description")],
    overlay: [t("overlay"), t("overlay.description")],
    alerts: [t("alerts"), t("alerts.description")],
    hub: [t("hub"), t("hub.description")],
  };
  return <div className="settings-shell">
    <aside>
      <nav aria-label={t("settings")}>{Object.entries(titles).map(([id, [title]], i) =>
        <button key={id} className={page === id ? "nav-item active" : "nav-item"} onClick={() => setPage(id)}
          aria-current={page === id ? "page" : undefined}><span aria-hidden="true">{["⚙", "◐", "◈", "▣", "◷", "◇"][i]}</span>{title}</button>)}</nav>
    </aside>
    <div className="settings-body"><header className="page-header"><div>
      <h1>{titles[page][0]}</h1><p>{titles[page][1]}</p></div></header>
      <main className="settings-content">{page === "hub" ? <HarnessHub /> : <fieldset className="editor" disabled={busy}>
        {page === "providers" && <>
          <div className="section-caption">{t("connections")} <span>{plural("enabled_count", draft.providers.filter(p => p.enabled).length)}</span></div>
           {draft.providers.map(p => <ProviderCard key={p.id} provider={p} update={u => providerChange(p.id, u)}
             signIn={() => void providerSignIn(p)} signOut={() => void providerSignOut(p.id)}
             signInState={state.providerSignIn} signInCommand={action => void run(action)}
             authentication={state.providers.find(provider => provider.id === p.id)?.authentication} />)}
        </>}
        {page === "alerts" && <>
          <Section title={t("thresholds")} description={t("thresholds.description")}>
            <Toggle label={t("alerts.enable")} checked={draft.usageAlerts.enabled} onChange={enabled => alertChange({ enabled })} />
            <fieldset className="thresholds" disabled={!draft.usageAlerts.enabled}>
              <div className="threshold-row"><div><span className="status-dot" style={{ background: draft.usageAlerts.warningColor }} /><strong>{t("warning")}</strong><small>{t("warning.description")}</small></div>
                <Field label={t("usage_at")}><input type="number" min="0" max="99" value={draft.usageAlerts.warningPercent}
                  onChange={e => alertChange({ warningPercent: e.target.valueAsNumber })} /></Field>
                <Field label={t("color")}><input type="color" value={draft.usageAlerts.warningColor} onChange={e => alertChange({ warningColor: e.target.value })} /></Field></div>
              <div className="threshold-row"><div><span className="status-dot" style={{ background: draft.usageAlerts.criticalColor }} /><strong>{t("critical")}</strong><small>{t("critical.description")}</small></div>
                <Field label={t("usage_at")}><input type="number" min={draft.usageAlerts.warningPercent + 1} max="100" value={draft.usageAlerts.criticalPercent}
                  onChange={e => alertChange({ criticalPercent: e.target.valueAsNumber })} /></Field>
                <Field label={t("color")}><input type="color" value={draft.usageAlerts.criticalColor} onChange={e => alertChange({ criticalColor: e.target.value })} /></Field></div>
            </fieldset>
          </Section>
          <div className="info-box">{t("alerts.applies")}</div>
        </>}
        {page === "overlay" && <>
          <Section title={t("overlay.title")} description={t("overlay.details")}>
            <Toggle label={t("overlay.show")} checked={draft.overlayEnabled} onChange={overlayEnabled => change({ overlayEnabled })} />
          </Section>
          <Section title={t("overlay.providers")} description={t("overlay.providers.description")}>
            {draft.providers.map(p => <Toggle key={p.id} label={p.name + (!p.enabled ? t("provider.disabled_suffix") : "")} checked={draft.overlayProviderIds.includes(p.id)}
              onChange={include => change({ overlayProviderIds: include ? [...draft.overlayProviderIds, p.id] : draft.overlayProviderIds.filter(id => id !== p.id) })} />)}
          </Section>
        </>}
        {page === "appearance" && <>
          <ThemePicker value={draft.theme} onChange={theme => change({ theme })} />
        </>}
        {page === "general" && <>
          <Section title={t("language")} description={t("language.description")}><Field label={t("language")}><select value={draft.language || "system"} onChange={e => change({ language: e.target.value })}>
            <option value="system">{t("language.system")}</option><option value="en">English</option><option value="es">Español</option><option value="fr">Français</option>
          </select></Field></Section>
          <Section title={t("startup")}><Toggle label={t("startup.enable")} checked={draft.launchAtSignIn} onChange={launchAtSignIn => change({ launchAtSignIn })} /></Section>
          <Section title={t("refresh")} description={t("refresh.description")}>
            <Toggle label={t("refresh.enable")} checked={draft.refresh.enabled} onChange={enabled => change({ refresh: { ...draft.refresh, enabled } })} />
            <Field label={t("refresh.interval")}><select value={draft.refresh.intervalMinutes} disabled={!draft.refresh.enabled}
              onChange={e => change({ refresh: { ...draft.refresh, intervalMinutes: Number(e.target.value) } })}>
              {[1, 5, 10, 15, 30, 60].map(n => <option key={n} value={n}>{plural("minutes", n)}</option>)}</select></Field>
          </Section>
        </>}
      </fieldset>}</main>
       <footer className="settings-footer"><span role="status">{message || formatMessage(state.statusMessage) || (dirty ? t("unsaved") : t("all_saved"))}</span>
         <button className="secondary" disabled={busy || !dirty} onClick={() => void discard()}>{t("discard")}</button>
         <button className="primary" disabled={busy || !dirty} onClick={() => void save()}>{busy ? t("saving") : t("save")}</button></footer>
    </div>
  </div>;
}

function ProviderCard({ provider: p, update, signIn, signOut, signInState, signInCommand, authentication }: {
  provider: Provider; update: (change: Partial<Provider>) => void; signIn: () => void; signOut: () => void;
  signInState: DesktopState["providerSignIn"]; signInCommand: (action: string) => void;
  authentication: Provider["authentication"];
}) {
  const [expanded, setExpanded] = useState(false);
  const signedIn = authentication?.signedIn === true;
  const pending = signInState?.pending === true;
  const activeSignIn = signInState?.provider === p.id && pending;
  const checking = authentication?.signedIn == null && (!authentication || authentication.checking === true);
  const connection = connectionPresentation(p.enabled, authentication);
  return <article className={`provider-card ${expanded ? "expanded" : ""}`}>
    <div className="provider-header"><Icon id={p.id} /><button className="provider-expand" onClick={() => setExpanded(!expanded)} aria-expanded={expanded}>
      <strong>{p.name}</strong>{!(activeSignIn && checking) && <small className={connection.warning ? "connection-status warning" : "connection-status"}>{t(connection.key)}</small>}
    </button><button type="button" className="chevron" aria-label={t(expanded ? "provider.collapse" : "provider.expand", p.name)}
      aria-expanded={expanded} onClick={() => setExpanded(value => !value)}><svg aria-hidden="true" viewBox="0 0 16 16" width="16" height="16" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
      <polyline points={expanded ? "4 10 8 6 12 10" : "4 6 8 10 12 6"} />
    </svg></button>
      <Toggle label={t("provider.enable", p.name)} checked={p.enabled} onChange={enabled => update({ enabled })} /></div>
    {expanded && <div className="provider-details">
      {activeSignIn ? null : signedIn ? <div className="provider-auth-row provider-signed-in"><div><p>{t("provider.signed_in", p.name)}</p>
        {p.id === "codex" && <p className="footnote">{t("provider.codex_signout")}</p>}
        {p.id === "claude" && p.source !== "web" && <p className="footnote">{t("provider.claude_signout")}</p>}
        </div>
        <button type="button" className="secondary" disabled={pending} onClick={signOut}>{t("signout")}</button>
      </div> : checking ? <p role="status">{t("connection.checking")}</p> : <>
      {authentication?.signedIn == null && <p className="inline-warning">{formatMessage(authentication?.messageInfo) || t("connection.verify")}</p>}
      <div className="provider-auth-row">
      <div className="fields-grid">
        {p.id === "claude" && authentication?.signedIn === false && <p className="provider-auth-hint">{t("provider.claude_signed_out")}</p>}
        {p.id === "copilot" && <Field label={t("signin.method")}><select value={p.source === "api" ? "api" : "oauth"} onChange={e => update({ source: e.target.value })}>
          <option value="oauth">{t("signin.github")}</option><option value="api">{t("signin.token")}</option></select></Field>}
        {p.id === "codex" && <Field label={t("signin.profiles")}><input value={p.codexProfileHomePaths?.join("; ") || ""}
          onChange={e => update({ codexProfileHomePaths: e.target.value.split(";").map(s => s.trim()).filter(Boolean) })} /></Field>}
        {p.id === "copilot" && <Field label={t("signin.host")}><input value={p.enterpriseHost || ""} onChange={e => update({ enterpriseHost: e.target.value })} placeholder="github.example.com" /></Field>}
        {p.id === "opencode" && <Field label={t("signin.workspace")}><input value={p.workspaceId || ""} onChange={e => update({ workspaceId: e.target.value })} /></Field>}
        {p.id === "opencode" && <Field label={t("signin.cookie_header")}><input type="password" autoComplete="new-password" value={p.newCookie || ""}
          placeholder={t("signin.enter_cookie")} onChange={e => update({ newCookie: e.target.value })} /></Field>}
        {(p.id === "zai" || (p.id === "copilot" && p.source === "api")) && <Field label={t(p.id === "zai" ? "signin.key" : "signin.token")}><input type="password" autoComplete="new-password" value={p.apiKey || ""}
          placeholder={t("signin.enter_credential")} onChange={e => update({ apiKey: e.target.value })} /></Field>}
      </div>
      <button type="button" className="secondary" disabled={pending} onClick={signIn}>{t(pending && signInState.provider === p.id ? "signin.pending" : "signin")}</button>
      </div>
      </>}
      {signInState?.provider === p.id && (signInState.pending || signInState.isError) && <div className="provider-signin" role="status" aria-live="polite">
        <p>{formatMessage(signInState.messageInfo) || t("signin.pending")}</p>
        {signInState.pending && signInState.userCode && <>
          <div className="provider-device-code-row"><strong className="provider-device-code" aria-label={t("signin.device_code")}>{signInState.userCode}</strong>
            <button type="button" className="icon-button provider-copy-code" aria-label={t("copy_code")} title={t("copy_code")} onClick={() => signInCommand("copy-signin-code")}>
              <svg aria-hidden="true" viewBox="0 0 24 24" width="19" height="19" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round"><rect x="8" y="8" width="12" height="12" rx="2" /><path d="M16 8V5a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2v9a2 2 0 0 0 2 2h3" /></svg>
            </button></div>
        </>}
        <div className="provider-signin-actions">
          {signInState.pending && signInState.userCode && <button type="button" className="secondary" onClick={() => signInCommand("open-signin-page")}>{t("open_github")}</button>}
          {signInState.pending && signInState.operation !== "signout" && <button type="button" className="secondary" onClick={() => signInCommand("cancel-signin")}>{t("signin.cancel")}</button>}
        </div>
      </div>}
    </div>}
  </article>;
}

function usageColor(alerts: UsageAlerts, used: number | null) {
  if (alerts.enabled && used !== null) {
    if (used * 100 >= alerts.criticalPercent) return alerts.criticalColor;
    if (used * 100 >= alerts.warningPercent) return alerts.warningColor;
  }
  return "var(--accent)";
}
function resetText(value: string | null) {
  if (!value) return t("reset.unknown");
  const mins = Math.max(0, Math.ceil((Date.parse(value) - Date.now()) / 60_000));
  return !mins ? t("reset.due") : t("reset.in", mins >= 1440 ? t("duration.days", Math.floor(mins / 1440), Math.floor(mins % 1440 / 60))
    : mins >= 60 ? t("duration.hours", Math.floor(mins / 60), mins % 60) : t("duration.minutes", mins));
}
function WindowUsage({ window: w, alerts, points }: { window: RateWindow; alerts: UsageAlerts; points: HistoryPoint[] }) {
  const used = w.percentUsed === null ? null : Math.min(1, Math.max(0, w.percentUsed));
  const color = usageColor(alerts, used);
  const sorted = points.toSorted((a, b) => Date.parse(a.timestamp) - Date.parse(b.timestamp));
  const first = sorted[0] ? Date.parse(sorted[0].timestamp) : 0;
  const last = sorted.at(-1) ? Date.parse(sorted.at(-1)!.timestamp) : 0;
  const coords = sorted.map(p => `${4 + (Date.parse(p.timestamp) - first) / Math.max(1, last - first) * 332},${57 - Math.min(1, Math.max(0, p.percentUsed)) * 50}`).join(" ");
  const title = formatMessage(w.titleMessage) || windowTitle(w.id);
  return <section className="usage-window"><div className="usage-window-header"><div><h3>{title}</h3><small title={w.resetAt ? dateTime(w.resetAt) : undefined}>{resetText(w.resetAt)}</small></div>
    <strong style={{ color }}>{used === null ? "—" : percent(used)}<small>{t("used")}</small></strong></div>
    <div className="meter" role="progressbar" aria-label={t("window.usage", title)} aria-valuemin={0} aria-valuemax={100} aria-valuenow={used === null ? undefined : used * 100}>
      <div style={{ width: `${(used || 0) * 100}%`, background: color }} /></div>
    {w.used !== null && <p className="footnote">{number(w.used)}{w.limit !== null ? ` / ${number(w.limit)}` : ""} {formatMessage(w.unitMessage) || w.unit}</p>}
    {sorted.length > 1 && <svg className="history-chart" viewBox="0 0 340 64" role="img" aria-label={t("window.history", title)}>
      <path d="M4 57 H336" stroke="var(--border)" /><polyline points={coords} fill="none" stroke={color} strokeWidth="2" /></svg>}
  </section>;
}
function Popover({ state, reload }: { state: DesktopState; reload: () => Promise<void> }) {
  const [selected, setSelected] = useState(state.selectedProvider || state.snapshots[0]?.provider || "");
  useEffect(() => { if (state.selectedProvider) setSelected(state.selectedProvider); }, [state.selectedProvider]);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const snapshot = state.snapshots.find(s => s.provider === selected) || state.snapshots[0];
  const run = async (action: string, provider?: string, index?: number) => {
    setBusy(true); setError("");
    try { await command(action, provider, index); await reload(); }
    catch (e) { setError(errorText(e)); }
    finally { setBusy(false); }
  };
  const accounts = snapshot ? state.accounts[snapshot.provider] || [] : [];
  return <div className="popover"><header className="popover-toolbar">
    <nav className="provider-tabs" aria-label={t("providers")}>{state.snapshots.map(s => <button key={s.provider} title={s.displayName}
      aria-label={s.displayName} aria-current={snapshot?.provider === s.provider ? "page" : undefined}
       className={snapshot?.provider === s.provider ? "active" : ""} onClick={() => setSelected(s.provider)}><Icon id={s.provider} /></button>)}</nav>
    <button className="icon-button popover-settings" aria-label={t("open_settings")} title={t("open_settings")} onClick={() => void run("settings")}>⚙</button>
    </header>
    <main className="popover-content">{snapshot ? <>
      <div className="snapshot-heading"><span className="eyebrow">{t("provider_usage")}</span><h1>{snapshot.displayName}</h1>
        <p>{[formatMessage(snapshot.accountMessage) || snapshot.account, snapshot.plan].filter(Boolean).join(" · ") || sourceName(snapshot.source)}</p></div>
      {accounts.length > 1 && <Field label={t("account")}><select onChange={e => void run("account", snapshot.provider, Number(e.target.value))} defaultValue="" disabled={busy}>
        <option value="" disabled>{t("account.switch")}</option>{accounts.map(a => <option key={a.index} value={a.index}>{formatMessage(a.labelMessage) || a.label}</option>)}</select></Field>}
      {snapshot.error ? <div className="inline-warning">{formatMessage(snapshot.errorMessage) || t("error.provider", snapshot.displayName)}<p>{snapshot.retryAt ? t("usage.retry_at", dateTime(snapshot.retryAt))
        : snapshot.errorMessage?.code === "error.claude_rate_limited" ? t("usage.retry_later") : t("provider.check")}</p></div> : null}
      {(!snapshot.error || snapshot.isStale) && <>
        {snapshot.isStale && <p className="inline-warning">{t("usage.stale")}</p>}
        {snapshot.windows.map(w => <WindowUsage key={w.id} window={w} alerts={state.usageAlerts}
          points={state.history.filter(p => p.provider === snapshot.provider && p.window === w.id)} />)}
        {!snapshot.windows.length && <p>{t("usage.no_windows")}</p>}
        {snapshot.credits && <div className="credits">{t("credits")} <strong>{snapshot.credits.unlimited ? t("unlimited") : `${snapshot.credits.balance == null ? "—" : number(snapshot.credits.balance)} ${snapshot.credits.unit || ""}`}</strong></div>}
      </>}
    </> : <div className="empty-state"><h2>{t("usage.empty")}</h2><p>{t("usage.get_started")}</p><button className="primary" onClick={() => void run("settings")}>{t("open_settings")}</button></div>}
      {error && <p role="alert" className="inline-warning">{error}</p>}
    </main><footer className="popover-footer"><small>{snapshot ? t("updated", time(snapshot.updatedAt)) : ""}</small>
      <button className="secondary" disabled={busy} onClick={() => void run("refresh")}>{t(busy ? "refreshing" : "refresh.action")}</button></footer>
  </div>;
}
