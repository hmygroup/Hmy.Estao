import { useEffect, useRef, useState } from "react";
import { getHubState, hubCommand } from "./nativeHost";
import { defaultSelection, updateSelection, type HubCommand, type HubState, type HubToolReview } from "./hubTypes";

const statuses: Record<string, string> = {
  Ready: "Not configured", Installed: "Installed", PartiallyInstalled: "Some local conflicts",
  Conflict: "Local changes preserved", RecoveryRequired: "Recovery required", Error: "Profile needs attention"
};
const labels = { new: "New", update: "Update", present: "Already present", conflict: "Local conflict" };

export default function HarnessHub() {
  const [state, setState] = useState<HubState>();
  const [error, setError] = useState("");
  const [sending, setSending] = useState(false);
  const [selection, setSelection] = useState<Record<string, string[]>>({});
  const pending = useRef(false);

  useEffect(() => {
    setSelection(state?.review ? defaultSelection(state.review) : {});
  }, [state?.review?.id]);

  useEffect(() => {
    let alive = true;
    const load = async () => {
      if (pending.current) return;
      pending.current = true;
      try {
        const next = await getHubState();
        if (!alive) return;
        setState(next);
        if (next.phase === "idle" && next.tools.length === 0)
          await hubCommand({ action: "check" });
      } catch (e) { if (alive) setError(String(e)); }
      finally { pending.current = false; }
    };
    void load();
    const timer = window.setInterval(() => void load(), 1000);
    return () => { alive = false; window.clearInterval(timer); };
  }, []);

  const run = async (request: HubCommand) => {
    setSending(true); setError("");
    try { await hubCommand(request); setState(await getHubState()); }
    catch (e) { setError(String(e)); }
    finally { setSending(false); }
  };
  const busy = sending || state?.busy;
  const chosen = Object.values(selection).reduce((sum, ids) => sum + ids.length, 0);
  const review = state?.review;

  return <div className="harness-hub">
    <section className="section hub-connection">
      <div className="hub-connection-copy"><h2>HMY shared skills</h2>
        <p>Install reviewed skills from <strong>hmygroup/hmy-prompt-engineer</strong> into your personal tools.</p>
        <span className={`hub-connection-status ${state?.connected ? "connected" : ""}`} role="status">{state?.connection || "Checking GitHub access…"}</span>
      </div>
      <div className="hub-actions">
        <button type="button" className="secondary" onClick={() => void run({ action: "open-repo" })}>Open GitHub repository ↗</button>
        {!state?.connected && <button type="button" className="primary" disabled={busy} onClick={() => void run({ action: "connect" })}>Connect GitHub</button>}
      </div>
    </section>

    {state?.deviceCode && <section className="section hub-device" aria-label="GitHub authorization">
      <h2>Authorize in your browser</h2><p>Enter this code on GitHub. Estao will verify access to the private repository.</p>
      <strong className="hub-device-code">{state.deviceCode.code}</strong>
      <div className="hub-actions"><button type="button" className="secondary" onClick={() => void run({ action: "copy-code" })}>Copy code</button>
        <button type="button" className="secondary" onClick={() => void run({ action: "open-auth" })}>Open GitHub ↗</button></div>
    </section>}

    <div className="hub-section-heading"><h2>Your tools</h2><div className="hub-actions">
      <button type="button" className="secondary" disabled={busy} onClick={() => void run({ action: "check" })}>Check again</button>
      <button type="button" className="primary" disabled={busy || !state?.connected || !state?.tools.some(t => t.detected)}
        onClick={() => void run({ action: "review" })}>Install all…</button>
    </div></div>
    <p className="hub-helper">Install all reviews compatible skills for every detected tool. Missing tools are skipped.</p>
    <div className="hub-tools">{state?.tools.map(tool => <article className="section hub-tool" key={tool.id}>
      <div className="hub-tool-heading"><img className="provider-icon" src={`/providers/${tool.id}.svg`} alt="" /><div><h3>{tool.name}</h3>
        <small>{!tool.detected ? "Not installed" : statuses[tool.status] || tool.status}</small></div></div>
      {tool.error && <p className="hub-helper">{tool.error}</p>}
      {!tool.detected ? <button type="button" className="secondary" onClick={() => void run({ action: "website", harnessId: tool.id })}>Go to website ↗</button>
        : tool.status === "RecoveryRequired" ? <button type="button" className="secondary" disabled={busy} onClick={() => void run({ action: "recover", harnessId: tool.id })}>Recover</button>
        : <button type="button" className="secondary" disabled={busy || !state.connected} onClick={() => void run({ action: "review", harnessId: tool.id })}>
          {tool.status === "Installed" || tool.status === "Conflict" || tool.status === "PartiallyInstalled" ? "Review update" : "Review installation"}</button>}
    </article>)}</div>

    <div className="hub-progress" role="status"><span>{state?.progress || "Checking your tools…"}</span>
      {state?.busy && <button type="button" className="secondary" disabled={sending} onClick={() => void run({ action: "cancel" })}>Cancel</button>}
    </div>
    {(error || state?.error) && <p className="inline-warning" role="alert">{error || state?.error}</p>}

    {review && <section className="hub-review" aria-label="Installation review">
      <div className="hub-section-heading"><h2>Review installation</h2><small title={review.commit}>Snapshot {review.commit.slice(0, 8)}</small></div>
      <p className="hub-helper">Nothing is written until you confirm. Local conflicts are skipped. Scripts and dependencies are never executed or installed.</p>
      <fieldset disabled={busy}>{review.tools.map(tool => <ToolReview key={tool.id} tool={tool} selected={selection[tool.id] || []}
        onSelect={(id, include) => setSelection(s => ({ ...s, [tool.id]: updateSelection(tool, s[tool.id] || [], id, include) }))} />)}</fieldset>
      <div className="hub-review-actions"><span>{chosen} selected across {review.tools.length} {review.tools.length === 1 ? "tool" : "tools"}</span>
        <button type="button" className="secondary" disabled={busy} onClick={() => void run({ action: "discard-review" })}>Dismiss review</button>
        <button type="button" className="primary" disabled={busy || chosen === 0} onClick={() => void run({ action: "install", reviewId: review.id, selection })}>Install selected</button>
      </div>
    </section>}

    {!!state?.results.length && <section className="section hub-results" aria-label="Installation results"><h2>Results</h2>
      {state.results.map((result, index) => <div className="hub-result" key={`${result.id}-${index}`}>
        <div><strong>{result.name}</strong><span className={`hub-badge ${result.outcome === "failed" ? "conflict" : "present"}`}>{result.outcome}</span></div>
        {result.error ? <p>{result.error}</p> : <p>{result.created} new files · {result.updated} updated · {result.unchanged} unchanged · {result.conflicts} preserved</p>}
      </div>)}
    </section>}
  </div>;
}

function ToolReview({ tool, selected, onSelect }: { tool: HubToolReview; selected: string[]; onSelect: (id: string, include: boolean) => void }) {
  const count = (status: string) => tool.artifacts.filter(a => a.status === status).length;
  return <section className="section hub-tool-review"><h3>{tool.name}</h3>
    <p className="hub-review-summary">{tool.artifacts.length} available · {count("new")} new · {count("update")} updates · {count("present")} already present · {count("conflict")} local conflicts</p>
    <small className="hub-destination">Personal skills folder: {tool.destination}</small>
    <div className="hub-artifacts">{tool.artifacts.map(artifact => <article className="hub-artifact" key={artifact.id}>
      <div className="hub-artifact-row"><input type="checkbox" aria-label={`Install ${artifact.id} for ${tool.name}`} checked={selected.includes(artifact.id)}
        disabled={artifact.status === "conflict"} onChange={e => onSelect(artifact.id, e.target.checked)} />
        <div className="hub-artifact-copy"><strong>{artifact.id}</strong><p>{artifact.description}</p></div>
        <span className={`hub-badge ${artifact.status}`}>{labels[artifact.status]}</span></div>
      <details><summary>Details</summary><div className="hub-artifact-details"><p>{artifact.category} · {artifact.fileCount} files · {artifact.source}</p>
        {artifact.requirements.length > 0 && <p>Requirements: {artifact.requirements.join("; ")}</p>}
        {artifact.dependencies.length > 0 && <p>Required skills: {artifact.dependencies.join(", ")}</p>}
        {artifact.files.map(file => <div key={file.path}><code>{file.path}</code><small>{file.action} · {file.reason}</small></div>)}
      </div></details>
    </article>)}</div>
    {tool.exclusions.length > 0 && <details className="hub-exclusions"><summary>Not installed from this repository ({tool.exclusions.length} groups)</summary>
      {tool.exclusions.map(item => <p key={item.source}><strong>{item.source}</strong> · {item.reason}</p>)}
    </details>}
  </section>;
}
