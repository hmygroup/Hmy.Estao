import { useEffect, useRef, useState } from "react";
import { getHubState, hubCommand } from "./nativeHost";
import { defaultSelection, updateSelection, type HubCommand, type HubState, type HubToolReview } from "./hubTypes";
import { t, plural, message, errorText } from "./i18n";

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
      } catch (e) { if (alive) setError(errorText(e)); }
      finally { pending.current = false; }
    };
    void load();
    const timer = window.setInterval(() => void load(), 1000);
    return () => { alive = false; window.clearInterval(timer); };
  }, []);

  const run = async (request: HubCommand) => {
    setSending(true); setError("");
    try { await hubCommand(request); setState(await getHubState()); }
    catch (e) { setError(errorText(e)); }
    finally { setSending(false); }
  };
  const busy = sending || state?.busy;
  const chosen = Object.values(selection).reduce((sum, ids) => sum + ids.length, 0);
  const review = state?.review;
  const intro = t("hub.intro").split("{0}");

  return <div className="harness-hub">
    <section className="section hub-connection">
      <div className="hub-connection-copy"><h2>{t("hub.title")}</h2>
        <p>{intro[0]}<strong>hmygroup/hmy-prompt-engineer</strong>{intro[1]}</p>
        {!state?.connected && <span className="hub-connection-status" role="status">{message(state?.connectionMessage) || t("hub.check_access")}</span>}
      </div>
      <div className="hub-actions">
        <button type="button" className="secondary" onClick={() => void run({ action: "open-repo" })}>{t("hub.open_repo")}</button>
        {!state?.connected && <button type="button" className="primary" disabled={busy} onClick={() => void run({ action: "connect" })}>{t("hub.connect")}</button>}
      </div>
    </section>

    {state?.deviceCode && <section className="section hub-device" aria-label={t("hub.authorization")}>
      <h2>{t("hub.authorize")}</h2><p>{t("hub.enter_code")}</p>
      <strong className="hub-device-code">{state.deviceCode.code}</strong>
      <div className="hub-actions"><button type="button" className="secondary" onClick={() => void run({ action: "copy-code" })}>{t("copy_code")}</button>
        <button type="button" className="secondary" onClick={() => void run({ action: "open-auth" })}>{t("open_github")}</button></div>
    </section>}

    <div className="hub-section-heading"><h2>{t("hub.tools")}</h2><div className="hub-actions">
      <button type="button" className="secondary" disabled={busy} onClick={() => void run({ action: "check" })}>{t("hub.check_again")}</button>
      <button type="button" className="primary" disabled={busy || !state?.connected || !state?.tools.some(t => t.detected)}
        onClick={() => void run({ action: "review" })}>{t("hub.install_all")}</button>
    </div></div>
    <p className="hub-helper">{t("hub.helper")}</p>
    <div className="hub-tools">{state?.tools.map(tool => <article className="section hub-tool" key={tool.id}>
      <div className="hub-tool-heading"><img className="provider-icon" src={`/providers/${tool.id}.svg`} alt="" /><div><h3>{tool.name}</h3>
        <small>{!tool.detected ? t("hub.not_installed") : t(`hub.status.${tool.status}`)}</small></div></div>
      {tool.error && <p className="hub-helper">{message(tool.errorMessage) || t("error.operation")}</p>}
      {!tool.detected ? <button type="button" className="secondary" onClick={() => void run({ action: "website", harnessId: tool.id })}>{t("hub.website")}</button>
        : tool.status === "RecoveryRequired" ? <button type="button" className="secondary" disabled={busy} onClick={() => void run({ action: "recover", harnessId: tool.id })}>{t("hub.recover")}</button>
        : <button type="button" className="secondary" disabled={busy || !state.connected} onClick={() => void run({ action: "review", harnessId: tool.id })}>
          {t(tool.status === "Installed" || tool.status === "Conflict" || tool.status === "PartiallyInstalled" ? "hub.review_update" : "hub.review_install")}</button>}
    </article>)}</div>

    <div className="hub-progress" role="status"><span>{message(state?.progressMessage) || t("hub.check_tools")}</span>
      {state?.busy && <button type="button" className="secondary" disabled={sending} onClick={() => void run({ action: "cancel" })}>{t("cancel")}</button>}
    </div>
    {(error || state?.error) && <p className="inline-warning" role="alert">{error || message(state?.errorMessage) || t("error.operation")}</p>}

    {review && <section className="hub-review" aria-label={t("hub.review_install")}>
      <div className="hub-section-heading"><h2>{t("hub.review_install")}</h2><small title={review.commit}>{t("hub.snapshot", review.commit.slice(0, 8))}</small></div>
      <p className="hub-helper">{t("hub.review_warning")}</p>
      <fieldset disabled={busy}>{review.tools.map(tool => <ToolReview key={tool.id} tool={tool} selected={selection[tool.id] || []}
        onSelect={(id, include) => setSelection(s => ({ ...s, [tool.id]: updateSelection(tool, s[tool.id] || [], id, include) }))} />)}</fieldset>
      <div className="hub-review-actions"><span>{plural("hub.selected", review.tools.length, chosen, review.tools.length)}</span>
        <button type="button" className="secondary" disabled={busy} onClick={() => void run({ action: "discard-review" })}>{t("hub.dismiss")}</button>
        <button type="button" className="primary" disabled={busy || chosen === 0} onClick={() => void run({ action: "install", reviewId: review.id, selection })}>{t("hub.install_selected")}</button>
      </div>
    </section>}

    {!!state?.results.length && <section className="section hub-results" aria-label={t("hub.results")}><h2>{t("hub.results")}</h2>
      {state.results.map((result, index) => <div className="hub-result" key={`${result.id}-${index}`}>
        <div><strong>{result.name}</strong><span className={`hub-badge ${result.outcome === "failed" ? "conflict" : "present"}`}>{t(`hub.status.${result.outcome}`)}</span></div>
        {result.error ? <p>{message(result.errorMessage) || t("error.operation")}</p> : <p>{t("hub.result_counts", plural("hub.new_files", result.created), result.updated, result.unchanged, result.conflicts)}</p>}
      </div>)}
    </section>}
  </div>;
}

function ToolReview({ tool, selected, onSelect }: { tool: HubToolReview; selected: string[]; onSelect: (id: string, include: boolean) => void }) {
  const count = (status: string) => tool.artifacts.filter(a => a.status === status).length;
  return <section className="section hub-tool-review"><h3>{tool.name}</h3>
    <p className="hub-review-summary">{t("hub.review_counts", tool.artifacts.length, count("new"), plural("hub.updates", count("update")), count("present"), plural("hub.conflicts", count("conflict")))}</p>
    <small className="hub-destination">{t("hub.destination", tool.destination)}</small>
    <div className="hub-artifacts">{tool.artifacts.map(artifact => <article className="hub-artifact" key={artifact.id}>
      <div className="hub-artifact-row"><input type="checkbox" aria-label={t("hub.install_for", artifact.id, tool.name)} checked={selected.includes(artifact.id)}
        disabled={artifact.status === "conflict"} onChange={e => onSelect(artifact.id, e.target.checked)} />
        <div className="hub-artifact-copy"><strong>{artifact.id}</strong><p>{artifact.description}</p></div>
        <span className={`hub-badge ${artifact.status}`}>{t(`hub.status.${artifact.status}`)}</span></div>
      <details><summary>{t("details")}</summary><div className="hub-artifact-details"><p>{t("hub.files", artifact.category, plural("files_count", artifact.fileCount), artifact.source)}</p>
        {artifact.requirements.length > 0 && <p>{t("hub.requirements", artifact.requirements.join("; "))}</p>}
        {artifact.dependencies.length > 0 && <p>{t("hub.dependencies", artifact.dependencies.join(", "))}</p>}
        {artifact.files.map(file => <div key={file.path}><code>{file.path}</code><small>{t(`hub.action.${file.action}`)} · {message(file.reasonMessage)}</small></div>)}
      </div></details>
    </article>)}</div>
    {tool.exclusions.length > 0 && <details className="hub-exclusions"><summary>{plural("hub.exclusions", tool.exclusions.length)}</summary>
      {tool.exclusions.map(item => <p key={item.source}><strong>{item.source}</strong> · {item.reason}</p>)}
    </details>}
  </section>;
}
