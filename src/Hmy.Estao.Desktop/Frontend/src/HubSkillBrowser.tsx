import { useState } from "react";
import { detectedTools, filterSkills, skillToolStatus, type HubCommand, type HubSkill, type HubState, type HubTool, type SkillStatus, type SkillView } from "./hubTypes";
import { t, plural } from "./i18n";

export default function HubSkillBrowser({ state, busy, run }: { state?: HubState; busy: boolean; run: (command: HubCommand) => Promise<void> }) {
  const [view, setView] = useState<SkillView>("installed");
  const [query, setQuery] = useState("");
  const [toolId, setToolId] = useState("");
  const [status, setStatus] = useState<SkillStatus>("all");
  const [selectedId, setSelectedId] = useState<string>();
  const tools = detectedTools(state?.tools ?? []);
  const effectiveTool = tools.some(tool => tool.id === toolId) ? toolId : "";
  const skills = filterSkills(state?.skills ?? [], tools, view, query, effectiveTool, status);
  const selected = skills.find(skill => skill.id === selectedId) ?? skills[0];
  const changeView = (next: SkillView) => { setView(next); setSelectedId(undefined); };

  return <section className="hub-browser" aria-label={t("hub.browser")}>
    <div className="hub-browser-heading">
      <div className="hub-view-switch" role="group" aria-label={t("hub.views")}>
        <button type="button" aria-pressed={view === "installed"} onClick={() => changeView("installed")}>{t("hub.view.installed")}</button>
        <button type="button" aria-pressed={view === "library"} onClick={() => changeView("library")}>{t("hub.view.library")}</button>
      </div>
      <small>{plural("hub.skill_count", skills.length)}</small>
    </div>
    <div className="hub-filters">
      <label className="hub-search">{t("hub.search")}<input type="search" value={query} onChange={event => setQuery(event.target.value)} placeholder={t("hub.search_placeholder")} /></label>
      <label>{t("hub.tool_filter")}<select value={effectiveTool} onChange={event => setToolId(event.target.value)}>
        <option value="">{t("hub.all_tools")}</option>{tools.map(tool => <option key={tool.id} value={tool.id}>{tool.name}</option>)}
      </select></label>
      <label>{t("hub.status_filter")}<select value={status} onChange={event => setStatus(event.target.value as SkillStatus)}>
        {["all", "missing", "attention", "installable"].map(value => <option key={value} value={value}>{t(`hub.filter.${value}`)}</option>)}
      </select></label>
    </div>
    <p className="hub-helper">{t("hub.scope_hint")}</p>
    {view === "library" && !state?.libraryLoaded && !state?.libraryError && <p className="hub-browser-notice" role="status">{t(state?.busy ? "hub.library_loading" : state?.connected ? "hub.library_unavailable" : "hub.library_connect")}</p>}
    {view === "library" && state?.libraryError && <p className="inline-warning" role="alert">{t("hub.library_failed")} <span className="hub-diagnostic">{state.libraryError}</span></p>}
    {!tools.length && state && !state.busy && <p className="hub-browser-notice">{t("hub.no_detected_tools")}</p>}
    <div className="hub-skill-layout">
      <div className="hub-skill-list" aria-label={t("hub.skill_list")}>
        {!skills.length ? <p className="hub-empty" role="status">{t(!state || (state.busy && state.skills == null) ? "hub.inventory_loading" : query || effectiveTool || status !== "all" ? "hub.no_matches" : view === "installed" ? "hub.no_installed_skills" : state?.libraryLoaded ? "hub.no_library_skills" : "hub.library_unavailable")}</p>
          : skills.map(skill => <button type="button" className={`hub-skill-row${selected?.id === skill.id ? " selected" : ""}`} key={skill.id} aria-pressed={selected?.id === skill.id} onClick={() => setSelectedId(skill.id)}>
            <span className="hub-skill-title"><strong>{skill.name}</strong>{skill.inLibrary && <span className="hub-badge">{t("hub.hmy_badge")}</span>}{skill.issue && <span className="hub-badge conflict">{t("hub.attention")}</span>}</span>
            <span className="hub-skill-description">{skill.description || t("hub.no_description")}</span>
            <span className="hub-tool-indicators">{tools.map(tool => <ToolIndicator key={tool.id} skill={skill} tool={tool} />)}</span>
          </button>)}
      </div>
      {selected && <SkillDetails key={selected.id} skill={selected} tools={tools} connected={state?.connected === true} busy={busy} run={run} commit={state?.libraryCommit} />}
    </div>
    <p className="hub-legend">{t("hub.logo_legend")}</p>
  </section>;
}

function ToolIndicator({ skill, tool }: { skill: HubSkill; tool: HubTool }) {
  const status = skillToolStatus(skill, tool);
  return <span className={`hub-tool-indicator ${status}`} title={t(`hub.tool_state.${status}`, tool.name)}>
    <img className="provider-icon" src={`/providers/${tool.id}.svg`} alt={t(`hub.tool_state.${status}`, tool.name)} />
    {status === "present" && <svg viewBox="0 0 12 12" aria-hidden="true"><path d="m2 6 2.5 2.5L10 3" /></svg>}
    {status === "unknown" && <svg viewBox="0 0 12 12" aria-hidden="true"><path d="M6 2v5m0 2v1" /></svg>}
    {status === "unsupported" && <svg viewBox="0 0 12 12" aria-hidden="true"><path d="m2 2 8 8" /></svg>}
  </span>;
}

function SkillDetails({ skill, tools, connected, busy, run, commit }: { skill: HubSkill; tools: HubTool[]; connected: boolean; busy: boolean; run: (command: HubCommand) => Promise<void>; commit?: string | null }) {
  const [target, setTarget] = useState("");
  const compatible = tools.filter(tool => skill.compatibleTools.includes(tool.id));
  const effectiveTarget = compatible.some(tool => tool.id === target) ? target : "";
  return <section className="hub-skill-details" aria-label={t("hub.skill_details", skill.name)}>
    <h3>{skill.name}</h3>
    <p>{skill.description || t("hub.no_description")}</p>
    <small>{t(skill.inLibrary ? "hub.origin_hmy" : "hub.origin_local")}</small>
    {skill.source && <code className="hub-source">{skill.source}</code>}
    {skill.inLibrary && commit && <small title={commit}>{t("hub.snapshot", commit.slice(0, 8))}</small>}
    {skill.issue && <p className="inline-warning">{t("hub.attention")} <span className="hub-diagnostic">{skill.issue}</span></p>}
    <div className="hub-skill-locations">{tools.map(tool => {
      const status = skillToolStatus(skill, tool);
      const occurrences = skill.occurrences.filter(item => item.toolId === tool.id);
      return <div key={tool.id} className="hub-skill-location">
        <div><ToolIndicator skill={skill} tool={tool} /><strong>{tool.name}</strong></div>
        <small>{t(`hub.tool_state.${status}`, tool.name)}</small>
        {tool.error && <p className="hub-diagnostic">{tool.error}</p>}
        {occurrences.map(item => <div key={item.path} className="hub-occurrence"><code>{item.path}</code>{item.shared && <small>{t("hub.shared_folder")}</small>}{item.error && <p className="hub-diagnostic">{item.error}</p>}</div>)}
      </div>;
    })}</div>
    {skill.inLibrary && skill.installable ? <div className="hub-skill-install">
      <label>{t("hub.install_target")}<select value={effectiveTarget} onChange={event => setTarget(event.target.value)} disabled={busy || !compatible.length}>
        <option value="">{t("hub.all_compatible_tools")}</option>{compatible.map(tool => <option key={tool.id} value={tool.id}>{tool.name}</option>)}
      </select></label>
      <button type="button" className="primary" disabled={busy || !connected || !compatible.length} onClick={() => void run({ action: "review", skillId: skill.id, ...(effectiveTarget ? { harnessId: effectiveTarget } : {}) })}>{t("hub.review_skill")}</button>
      <p className="hub-helper">{t("hub.review_skill_hint")}</p>
    </div> : <p className="hub-helper">{t(skill.inLibrary ? "hub.not_approved" : "hub.local_read_only")}</p>}
  </section>;
}
