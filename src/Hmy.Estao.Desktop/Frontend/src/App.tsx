import { useEffect, useLayoutEffect, useState, type ReactNode } from "react";
import { command, getState, saveSettings, WindowControls } from "./nativeHost";
import type { DesktopState, HistoryPoint, Provider, RateWindow, SettingsUpdate, UsageAlerts } from "./types";
import { applyTheme, cachedTheme, cacheTheme, resolveTheme } from "./themes";
import ThemePicker from "./themes/ThemePicker";

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
  const load = async () => { try { setState(await getState()); } catch (e) { setError(String(e)); } };
  useEffect(() => {
    void load();
    const timer = window.setInterval(() => void load(), 4000);
    return () => clearInterval(timer);
  }, []);
  const view = state?.view || new URLSearchParams(window.location.search).get("view") || "settings";
  return <div className={`desktop-shell${view === "popover" ? " popover-shell" : ""}`}>
    {view !== "popover" && <header className="window-header"><span>Estao · Settings</span><WindowControls /></header>}
    {!state ? <main className="loading"><h1>Estao</h1><p role="status">{error || "Loading your workspace…"}</p></main>
      : state.view === "settings" ? <Settings state={state} reload={load} previewTheme={setPreviewTheme} /> : <Popover state={state} reload={load} />}
  </div>;
}

function Settings({ state, reload, previewTheme }: { state: DesktopState; reload: () => Promise<void>; previewTheme: (id: string | null) => void }) {
  const normalizeDraft = (value: DesktopState): SettingsUpdate => ({ ...structuredClone(value), theme: resolveTheme(value.theme).id });
  const [draft, setDraft] = useState<SettingsUpdate>(() => normalizeDraft(state));
  useEffect(() => { previewTheme(draft.theme); return () => previewTheme(null); }, [draft.theme, previewTheme]);
  const [page, setPage] = useState("providers");
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
      setDraft(normalizeDraft(fresh)); setDirty(false); setMessage("Settings saved.");
      await reload();
    } catch (e) { setMessage(String(e)); }
    finally { setBusy(false); }
  };
  const discard = async () => {
    try { setDraft(normalizeDraft(await getState())); setDirty(false); setMessage(""); }
    catch (e) { setMessage(String(e)); }
  };
  const run = async (action: string, provider?: string) => {
    try { await command(action, provider); await reload(); }
    catch (e) { setMessage(String(e)); }
  };
  useEffect(() => {
    const listener = (event: BeforeUnloadEvent) => { if (dirty) event.preventDefault(); };
    window.addEventListener("beforeunload", listener);
    return () => window.removeEventListener("beforeunload", listener);
  }, [dirty]);
  const titles: Record<string, [string, string]> = {
    general: ["General", "Make Estao work your way."],
    appearance: ["Appearance", "Choose the glass that feels like home."],
    providers: ["Providers", "Connect the tools you use. Keep an eye on what’s left."],
    overlay: ["Overlay", "A small native window. Always within reach."],
    alerts: ["Usage alerts", "One set of thresholds. Every provider."],
  };
  return <div className="settings-shell">
    <aside>
      <nav aria-label="Settings">{Object.entries(titles).map(([id, [title]], i) =>
        <button key={id} className={page === id ? "nav-item active" : "nav-item"} onClick={() => setPage(id)}
          aria-current={page === id ? "page" : undefined}><span aria-hidden="true">{["⚙", "◐", "◈", "▣", "◷"][i]}</span>{title}</button>)}</nav>
    </aside>
    <div className="settings-body"><header className="page-header"><div><span className="eyebrow">SETTINGS</span>
      <h1>{titles[page][0]}</h1><p>{titles[page][1]}</p></div></header>
      <main className="settings-content"><fieldset className="editor" disabled={busy}>
        {page === "providers" && <>
          <div className="section-caption">YOUR CONNECTIONS <span>{draft.providers.filter(p => p.enabled).length} enabled</span></div>
          {draft.providers.map(p => <ProviderCard key={p.id} provider={p} update={u => providerChange(p.id, u)}
            snapshot={state.snapshots.find(s => s.provider === p.id)} signIn={() => void run("signin", p.id)} />)}
        </>}
        {page === "alerts" && <>
          <Section title="Color thresholds" description="Usage indicators in the overlay and popover use the same warning and critical colors.">
            <Toggle label="Enable usage alert colors" checked={draft.usageAlerts.enabled} onChange={enabled => alertChange({ enabled })} />
            <fieldset className="thresholds" disabled={!draft.usageAlerts.enabled}>
              <div className="threshold-row"><div><span className="status-dot" style={{ background: draft.usageAlerts.warningColor }} /><strong>Warning</strong><small>Getting close to your limit</small></div>
                <Field label="Usage at (%)"><input type="number" min="0" max="99" value={draft.usageAlerts.warningPercent}
                  onChange={e => alertChange({ warningPercent: e.target.valueAsNumber })} /></Field>
                <Field label="Color"><input type="color" value={draft.usageAlerts.warningColor} onChange={e => alertChange({ warningColor: e.target.value })} /></Field></div>
              <div className="threshold-row"><div><span className="status-dot" style={{ background: draft.usageAlerts.criticalColor }} /><strong>Critical</strong><small>Almost at your limit</small></div>
                <Field label="Usage at (%)"><input type="number" min={draft.usageAlerts.warningPercent + 1} max="100" value={draft.usageAlerts.criticalPercent}
                  onChange={e => alertChange({ criticalPercent: e.target.valueAsNumber })} /></Field>
                <Field label="Color"><input type="color" value={draft.usageAlerts.criticalColor} onChange={e => alertChange({ criticalColor: e.target.value })} /></Field></div>
            </fieldset>
          </Section>
          <div className="info-box">Applies to Codex, Claude, GitHub Copilot, OpenCode and Z.ai. Daily pacing targets remain in each provider’s configuration.</div>
        </>}
        {page === "overlay" && <>
          <Section title="Small window. Essential information." description="A lightweight Windows Forms overlay, with no browser running inside it.">
            <Toggle label="Show provider overlay" checked={draft.overlayEnabled} onChange={overlayEnabled => change({ overlayEnabled })} />
            <div className="overlay-preview" aria-label="Codex overlay: percentage, donut and reset time"><span aria-hidden="true">⠿</span><Icon id="codex" /><small>42%</small><div className="mini-donut" /><small>2h 15m</small></div>
            <p className="footnote">These three indicators are always shown. The original taskbar pill follows Windows light/dark mode. Drag its grip to move it; hover or click a provider for detailed usage.</p>
          </Section>
          <Section title="Visible providers" description="Choose which enabled providers appear. An empty selection automatically includes all enabled providers.">
            {draft.providers.map(p => <Toggle key={p.id} label={p.name + (!p.enabled ? " (disabled)" : "")} checked={draft.overlayProviderIds.includes(p.id)}
              onChange={include => change({ overlayProviderIds: include ? [...draft.overlayProviderIds, p.id] : draft.overlayProviderIds.filter(id => id !== p.id) })} />)}
          </Section>
        </>}
        {page === "appearance" && <>
          <ThemePicker value={draft.theme} onChange={theme => change({ theme })} />
        </>}
        {page === "general" && <>
          <Section title="Startup"><Toggle label="Launch Estao at Windows sign-in" checked={draft.launchAtSignIn} onChange={launchAtSignIn => change({ launchAtSignIn })} /></Section>
          <Section title="Refresh" description="Refreshes run in the background, even with settings closed.">
            <Toggle label="Automatically refresh usage" checked={draft.refresh.enabled} onChange={enabled => change({ refresh: { ...draft.refresh, enabled } })} />
            <Field label="Refresh interval"><select value={draft.refresh.intervalMinutes} disabled={!draft.refresh.enabled}
              onChange={e => change({ refresh: { ...draft.refresh, intervalMinutes: Number(e.target.value) } })}>
              {[1, 5, 10, 15, 30, 60].map(n => <option key={n} value={n}>{n} minutes</option>)}</select></Field>
            <button className="secondary" onClick={() => void run("refresh")}>Refresh now</button>
          </Section>
        </>}
      </fieldset></main>
      <footer className="settings-footer"><span role="status">{message || state.status || (dirty ? "Unsaved changes" : "All changes saved")}</span>
        <button className="secondary" disabled={busy || !dirty} onClick={() => void discard()}>Discard</button>
        <button className="primary" disabled={busy || !dirty} onClick={() => void save()}>{busy ? "Saving…" : "Save changes"}</button></footer>
    </div>
  </div>;
}

function ProviderCard({ provider: p, update, snapshot, signIn }: {
  provider: Provider; update: (change: Partial<Provider>) => void; snapshot: DesktopState["snapshots"][number] | undefined; signIn: () => void;
}) {
  const [expanded, setExpanded] = useState(false);
  const hasCookies = ["claude", "opencode"].includes(p.id);
  const hasKey = ["claude", "copilot", "zai"].includes(p.id);
  return <article className={`provider-card ${expanded ? "expanded" : ""}`}>
    <div className="provider-header"><Icon id={p.id} /><button className="provider-expand" onClick={() => setExpanded(!expanded)} aria-expanded={expanded}>
      <strong>{p.name}</strong><small>{!p.enabled ? "Disabled" : snapshot?.error ? "Needs attention" : snapshot ? `${snapshot.account || "Connected"} · ${snapshot.source}` : "Enabled · awaiting refresh"}</small>
    </button><button type="button" className="chevron" aria-label={`${expanded ? "Collapse" : "Expand"} ${p.name} settings`}
      aria-expanded={expanded} onClick={() => setExpanded(value => !value)}><svg aria-hidden="true" viewBox="0 0 16 16" width="16" height="16" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round">
      <polyline points={expanded ? "4 10 8 6 12 10" : "4 6 8 10 12 6"} />
    </svg></button>
      <Toggle label={`Enable ${p.name}`} checked={p.enabled} onChange={enabled => update({ enabled })} /></div>
    {expanded && <div className="provider-details">
      {snapshot?.error && <p className="inline-warning">{snapshot.error}</p>}
      <div className="fields-grid">
        <Field label="Usage source"><select value={p.source} onChange={e => update({ source: e.target.value })}>
          {["auto", "web", "cli", "oauth", "api"].map(v => <option key={v} value={v}>{v === "auto" ? "Automatic" : v.toUpperCase()}</option>)}</select></Field>
        {p.id === "codex" && <Field label="Codex profile homes (semicolon separated)"><input value={p.codexProfileHomePaths?.join("; ") || ""}
          onChange={e => update({ codexProfileHomePaths: e.target.value.split(";").map(s => s.trim()).filter(Boolean) })} /></Field>}
        {p.id === "copilot" && <Field label="Enterprise host"><input value={p.enterpriseHost || ""} onChange={e => update({ enterpriseHost: e.target.value })} placeholder="github.example.com" /></Field>}
        {p.id === "opencode" && <Field label="Workspace"><input value={p.workspaceId || ""} onChange={e => update({ workspaceId: e.target.value })} /></Field>}
        {hasCookies && <>
          <Field label="Cookie source"><select value={p.cookieSource} onChange={e => update({ cookieSource: e.target.value })}>
            {["auto", "manual", "off"].map(v => <option key={v} value={v}>{v}</option>)}</select></Field>
          <Field label={`New cookie header · ${p.hasCookie ? "saved" : "not configured"}`}><input type="password" autoComplete="new-password" value={p.newCookie || ""}
            placeholder="Leave blank to keep saved cookie" onChange={e => update({ newCookie: e.target.value, clearCookie: false })} /></Field>
          <Toggle label="Remove saved cookie on save" checked={p.clearCookie || false} onChange={clearCookie => update({ clearCookie })} />
        </>}
        {hasKey && <><Field label={`API key / token · ${p.hasApiKey ? "saved" : "not configured"}`}><input type="password" autoComplete="new-password" value={p.apiKey || ""}
          placeholder="Leave blank to keep saved key" onChange={e => update({ apiKey: e.target.value, clearApiKey: false })} /></Field>
          <Toggle label="Remove saved API key on save" checked={p.clearApiKey || false} onChange={clearApiKey => update({ clearApiKey })} /></>}
      </div>
      {["codex", "claude", "copilot"].includes(p.id) && <button className="secondary" onClick={signIn}>Sign in to {p.name} ↗</button>}
      <div className="pacing"><h3>Daily pacing</h3><Toggle label="Enable pacing for this provider" checked={p.pacing.enabled}
        onChange={enabled => update({ pacing: { ...p.pacing, enabled } })} />
        <div className="fields-grid"><Field label="Daily target (% / day)"><input type="number" min="1" max="100" disabled={!p.pacing.enabled} value={p.pacing.dailyTargetPercent}
          onChange={e => update({ pacing: { ...p.pacing, dailyTargetPercent: e.target.valueAsNumber } })} /></Field>
          <Toggle label="Notify once a day when over pace" checked={p.pacing.notifyOnExceed} disabled={!p.pacing.enabled}
            onChange={notifyOnExceed => update({ pacing: { ...p.pacing, notifyOnExceed } })} /></div></div>
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
  if (!value) return "Reset time unavailable";
  const mins = Math.max(0, Math.ceil((Date.parse(value) - Date.now()) / 60_000));
  return !mins ? "Reset due" : mins >= 1440 ? `Resets in ${Math.floor(mins / 1440)}d ${Math.floor(mins % 1440 / 60)}h`
    : mins >= 60 ? `Resets in ${Math.floor(mins / 60)}h ${mins % 60}m` : `Resets in ${mins}m`;
}
function WindowUsage({ window: w, alerts, points }: { window: RateWindow; alerts: UsageAlerts; points: HistoryPoint[] }) {
  const used = w.percentUsed === null ? null : Math.min(1, Math.max(0, w.percentUsed));
  const color = usageColor(alerts, used);
  const sorted = points.toSorted((a, b) => Date.parse(a.timestamp) - Date.parse(b.timestamp));
  const first = sorted[0] ? Date.parse(sorted[0].timestamp) : 0;
  const last = sorted.at(-1) ? Date.parse(sorted.at(-1)!.timestamp) : 0;
  const coords = sorted.map(p => `${4 + (Date.parse(p.timestamp) - first) / Math.max(1, last - first) * 332},${57 - Math.min(1, Math.max(0, p.percentUsed)) * 50}`).join(" ");
  return <section className="usage-window"><div className="usage-window-header"><div><h3>{w.title}</h3><small title={w.resetAt ? new Date(w.resetAt).toLocaleString() : undefined}>{resetText(w.resetAt)}</small></div>
    <strong style={{ color }}>{used === null ? "—" : `${Math.round(used * 100)}%`}<small>used</small></strong></div>
    <div className="meter" role="progressbar" aria-label={`${w.title} usage`} aria-valuemin={0} aria-valuemax={100} aria-valuenow={used === null ? undefined : used * 100}>
      <div style={{ width: `${(used || 0) * 100}%`, background: color }} /></div>
    {w.used !== null && <p className="footnote">{w.used.toLocaleString()}{w.limit !== null ? ` / ${w.limit.toLocaleString()}` : ""} {w.unit}</p>}
    {sorted.length > 1 && <svg className="history-chart" viewBox="0 0 340 64" role="img" aria-label={`${w.title} usage history`}>
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
    catch (e) { setError(String(e)); }
    finally { setBusy(false); }
  };
  const accounts = snapshot ? state.accounts[snapshot.provider] || [] : [];
  return <div className="popover"><header className="popover-toolbar">
    <nav className="provider-tabs" aria-label="Providers">{state.snapshots.map(s => <button key={s.provider} title={s.displayName}
      aria-label={s.displayName} aria-current={snapshot?.provider === s.provider ? "page" : undefined}
       className={snapshot?.provider === s.provider ? "active" : ""} onClick={() => setSelected(s.provider)}><Icon id={s.provider} /></button>)}</nav>
    <button className="icon-button popover-settings" aria-label="Open settings" title="Open settings" onClick={() => void run("settings")}>⚙</button>
    </header>
    <main className="popover-content">{snapshot ? <>
      <div className="snapshot-heading"><span className="eyebrow">PROVIDER USAGE</span><h1>{snapshot.displayName}</h1>
        <p>{[snapshot.account, snapshot.plan].filter(Boolean).join(" · ") || snapshot.source}</p></div>
      {accounts.length > 1 && <Field label="Account"><select onChange={e => void run("account", snapshot.provider, Number(e.target.value))} defaultValue="" disabled={busy}>
        <option value="" disabled>Switch account…</option>{accounts.map(a => <option key={a.index} value={a.index}>{a.label}</option>)}</select></Field>}
      {snapshot.error ? <div className="inline-warning">{snapshot.error}<p>Check the provider’s configuration or sign in again.</p></div> : <>
        {snapshot.isStale && <p className="inline-warning">Showing stale usage. Try refreshing.</p>}
        {snapshot.windows.map(w => <WindowUsage key={w.id} window={w} alerts={state.usageAlerts}
          points={state.history.filter(p => p.provider === snapshot.provider && p.window === w.id)} />)}
        {!snapshot.windows.length && <p>No usage windows available.</p>}
        {snapshot.credits && <div className="credits">Credits <strong>{snapshot.credits.unlimited ? "Unlimited" : `${snapshot.credits.balance ?? "—"} ${snapshot.credits.unit || ""}`}</strong></div>}
      </>}
    </> : <div className="empty-state"><h2>No usage yet</h2><p>Enable a provider in settings to get started.</p><button className="primary" onClick={() => void run("settings")}>Open settings</button></div>}
      {error && <p role="alert" className="inline-warning">{error}</p>}
    </main><footer className="popover-footer"><small>{snapshot ? `Updated ${new Date(snapshot.updatedAt).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}` : ""}</small>
      <button className="secondary" disabled={busy} onClick={() => void run("refresh")}>{busy ? "Refreshing…" : "↻ Refresh"}</button></footer>
  </div>;
}
