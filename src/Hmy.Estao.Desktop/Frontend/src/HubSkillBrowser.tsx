import { useState } from "react";
import { detectedTools, filterSkills, isCurated, itemType, localizedCategory, localizedDescription, showHubConnect, skillCategories, skillToolStatus, type CatalogItemType, type HubCommand, type HubSkill, type HubState, type HubTool, type SkillStatus, type SkillView } from "./hubTypes";
import { currentLanguage, t, plural, message } from "./i18n";

export default function HubSkillBrowser({ state, busy, run }: { state?: HubState; busy: boolean; run: (command: HubCommand) => Promise<void> }) {
  const [view, setView] = useState<SkillView>("installed");
  const [query, setQuery] = useState("");
  const [toolId, setToolId] = useState("");
  const [status, setStatus] = useState<SkillStatus>("all");
  const [category, setCategory] = useState("");
  const [type, setType] = useState<CatalogItemType>("all");
  const [selectedId, setSelectedId] = useState<string>();
  const language = currentLanguage();
  const tools = detectedTools(state?.tools ?? []);
  const effectiveTool = tools.some(tool => tool.id === toolId) ? toolId : "";
  const catalogCategories = state?.catalogCategories;
  const categories = skillCategories(filterSkills(state?.skills ?? [], tools, view, "", "", "all", language));
  const effectiveCategory = category && category !== "__uncategorized" && !categories.includes(category) ? "" : category;
  const skills = filterSkills(state?.skills ?? [], tools, view, query, effectiveTool, status, language, effectiveCategory, type, catalogCategories, state?.catalogDefaultLanguage);
  const selected = skills.find(skill => skill.id === selectedId) ?? skills[0];
  const changeView = (next: SkillView) => { setView(next); setSelectedId(undefined); setCategory(""); setType("all"); };
  const catalogFilter = view === "library";
  const progress = message(state?.progressMessage) || t("hub.working");
  const completedMessage = !busy && (state?.progressMessage?.code === "hub.export_complete" || state?.progressMessage?.code === "hub.uninstall_complete" || state?.progressMessage?.code === "hub.cancelled") ? progress : "";

  return <section className="hub-browser" aria-label={t("hub.browser")}>
    <div className="hub-browser-heading">
      <div className="hub-view-switch" role="group" aria-label={t("hub.views")}>
        <button type="button" aria-pressed={view === "installed"} onClick={() => changeView("installed")}>{t("hub.view.installed")}</button>
        <button type="button" aria-pressed={view === "library"} onClick={() => changeView("library")}>{t("hub.view.library")}</button>
        <button type="button" aria-pressed={view === "other"} onClick={() => changeView("other")}>{t("hub.view.other")}</button>
      </div>
      <div className="hub-browser-actions"><small>{plural("hub.item_count", skills.length)}</small>
        <span className="hub-activity-slot">
          {busy && <span className="hub-activity" role="status" title={progress}>
            <span className="hub-spinner" aria-hidden="true" /><span className="hub-visually-hidden">{progress}</span>
          </span>}
          {!busy && <button type="button" className="secondary hub-refresh" aria-label={t("hub.refresh")} title={t("hub.refresh")} onClick={() => void run({ action: "check" })}>
            <svg viewBox="0 0 16 16" aria-hidden="true"><path d="M13 6a5.2 5.2 0 1 0 .1 4M13 2v4H9" /></svg>
          </button>}
          {state?.busy && <button type="button" className="secondary hub-cancel" aria-label={t("cancel")} title={t("cancel")} onClick={() => void run({ action: "cancel" })}>
            <svg viewBox="0 0 16 16" aria-hidden="true"><path d="m4 4 8 8m0-8-8 8" /></svg>
          </button>}
        </span>
      </div>
    </div>
    {completedMessage && <p className="hub-operation-result" role="status">{completedMessage}</p>}
    <div className="hub-filters">
      <label className="hub-search">{t("hub.search")}<input type="search" value={query} onChange={event => setQuery(event.target.value)} placeholder={t("hub.search_placeholder")} /></label>
      <label>{t("hub.tool_filter")}<select aria-label={t("hub.tool_filter")} value={effectiveTool} onChange={event => setToolId(event.target.value)}>
        <option value="">{t("hub.all_tools")}</option>{tools.map(tool => <option key={tool.id} value={tool.id}>{tool.name}</option>)}
      </select></label>
      <label>{t("hub.status_filter")}<select aria-label={t("hub.status_filter")} value={status} onChange={event => setStatus(event.target.value as SkillStatus)}>
        {["all", "missing", "attention", "installable"].map(value => <option key={value} value={value}>{t(`hub.filter.${value}`)}</option>)}
      </select></label>
      {catalogFilter && <>
        <label>{t("hub.type_filter")}<select aria-label={t("hub.type_filter")} value={type} onChange={event => setType(event.target.value as CatalogItemType)}>
          {(["all", "skill", "instruction", "file"] as CatalogItemType[]).map(value => <option key={value} value={value}>{t(`hub.type.${value}`)}</option>)}
        </select></label>
      </>}
      <div className={`hub-category-filter${catalogFilter ? " with-repository" : ""}`}><label>{t("hub.category_filter")}<select aria-label={t("hub.category_filter")} value={effectiveCategory} onChange={event => setCategory(event.target.value)}>
          <option value="">{t("hub.category_all")}</option>{categories.map(value => <option key={value} value={value}>{localizedCategory(value, catalogCategories, language, state?.catalogDefaultLanguage)}</option>)}
          <option value="__uncategorized">{t("hub.category_uncategorized")}</option>
      </select></label>
      {catalogFilter && <button type="button" className="secondary hub-repository-button" onClick={() => void run({ action: "open-repo" })}>{t("hub.open_repo")}</button>}
      </div>
    </div>
    {catalogFilter && showHubConnect(state) && <div className="hub-auth-notice" role="status">
      <span>{message(state?.connectionMessage) || t("hub.connect_required")}</span>
      <button type="button" className="primary" disabled={busy} onClick={() => void run({ action: "connect" })}>{t("hub.connect")}</button>
    </div>}
    {view === "library" && state?.catalogError && <p className="inline-warning" role="alert">{t("hub.catalog_failed")} <span className="hub-diagnostic">{state.catalogError}</span></p>}
    {view !== "installed" && state?.libraryError && <p className="inline-warning" role="alert">{t("hub.library_failed")} <span className="hub-diagnostic">{state.libraryError}</span></p>}
    {view === "library" && !busy && !state?.libraryLoaded && !state?.libraryError && <p className="hub-browser-notice" role="status">{t(state?.connected ? "hub.library_unavailable" : "hub.library_connect")}</p>}
    {!tools.length && state && !busy && <p className="hub-browser-notice">{t("hub.no_detected_tools")}</p>}
    <div className="hub-skill-layout">
      <div className="hub-skill-list" aria-label={t("hub.skill_list")}>
        {!skills.length ? busy ? <div className="hub-empty" aria-hidden="true" /> : <p className="hub-empty" role="status">{t(query || effectiveTool || status !== "all" || effectiveCategory || type !== "all" ? "hub.no_matches" : view === "installed" ? "hub.no_installed_skills" : view === "library" ? state?.catalogError ? "hub.no_catalog_items" : state?.libraryLoaded ? "hub.no_catalog_items" : "hub.library_unavailable" : "hub.no_other_items")}</p>
          : skills.map(skill => <button type="button" className={`hub-skill-row${selected?.id === skill.id ? " selected" : ""}`} key={skill.id} aria-pressed={selected?.id === skill.id} onClick={() => setSelectedId(skill.id)}>
            <span className="hub-skill-title"><strong title={skill.name}>{skill.name}</strong>
              <span className="hub-skill-origin" title={isCurated(skill) ? t("hub.origin_hmy") : skill.inLibrary ? t("hub.origin_repo") : undefined}>· {t(isCurated(skill) ? "hub.hmy_badge" : skill.inLibrary ? "hub.repo_skill" : "hub.local_source")}</span>
              {itemType(skill) !== "skill" && <span className="hub-badge">{t(`hub.type.${itemType(skill)}`)}</span>}{skill.issue && <span className="hub-badge conflict">{t("hub.attention")}</span>}</span>
            <span className="hub-skill-description">{localizedDescription(skill, language) || t("hub.no_description")}</span>
            {itemType(skill) === "skill" && <span className="hub-tool-indicators">{tools.map(tool => <ToolIndicator key={tool.id} skill={skill} tool={tool} />)}</span>}
          </button>)}
      </div>
      {selected && <SkillDetails key={selected.id} skill={selected} tools={tools} connected={state?.connected === true} busy={busy} run={run} language={language} catalogCategories={catalogCategories} catalogDefaultLanguage={state?.catalogDefaultLanguage} />}
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

function SkillDetails({ skill, tools, connected, busy, run, language, catalogCategories, catalogDefaultLanguage }: { skill: HubSkill; tools: HubTool[]; connected: boolean; busy: boolean; run: (command: HubCommand) => Promise<void>; language: string; catalogCategories?: HubState["catalogCategories"]; catalogDefaultLanguage?: string }) {
  const [target, setTarget] = useState("");
  const isSkill = itemType(skill) === "skill";
  const compatible = tools.filter(tool => skill.compatibleTools.includes(tool.id));
  const effectiveTarget = compatible.some(tool => tool.id === target) ? target : "";
  const description = localizedDescription(skill, language);
  return <section className="hub-skill-details" aria-label={t("hub.skill_details", skill.name)}>
    <div className="hub-skill-details-content">
    <div className="hub-skill-details-heading">
      <h3>{skill.name}</h3>
      <div className="hub-item-categories">{skill.categories?.length ? skill.categories.map(value => <span className="hub-badge" key={value}>{localizedCategory(value, catalogCategories, language, catalogDefaultLanguage)}</span>) : <small>{t("hub.category_uncategorized")}</small>}
        {!isSkill && <span className="hub-badge">{t(`hub.type.${itemType(skill)}`)}</span>}
      </div>
    </div>
    <p>{description || t("hub.no_description")}</p>
    {!isCurated(skill) && skill.inLibrary && <small>{t("hub.origin_repo")}</small>}
    {skill.issue && <p className="inline-warning">{t("hub.attention")} <span className="hub-diagnostic">{skill.issue}</span></p>}
    {isSkill && <div className="hub-skill-locations">{tools.map(tool => {
      const status = skillToolStatus(skill, tool);
      const occurrences = skill.occurrences.filter(item => item.toolId === tool.id);
      return <div key={tool.id} className="hub-skill-location">
        <div><ToolIndicator skill={skill} tool={tool} /><strong>{tool.name}</strong></div>
        {status !== "present" && <small>{t(`hub.tool_state.${status}`, tool.name)}</small>}
        {tool.error && <p className="hub-diagnostic">{tool.error}</p>}
        {occurrences.map(item => <div key={item.path} className="hub-occurrence">{t("hub.installed_location")} <code>{item.path}</code>{item.error && <p className="hub-diagnostic">{item.error}</p>}</div>)}
      </div>;
    })}</div>}
    </div>
    <div className="hub-skill-details-footer">
    {isSkill && skill.inLibrary && skill.installable && <div className="hub-skill-install">
      <label>{t("hub.install_target")}<select aria-label={t("hub.install_target")} value={effectiveTarget} onChange={event => setTarget(event.target.value)} disabled={busy || !compatible.length}>
        <option value="">{t("hub.all_compatible_tools")}</option>{compatible.map(tool => <option key={tool.id} value={tool.id}>{tool.name}</option>)}
      </select></label>
    </div>}
    {isSkill ? <>
      {skill.inLibrary && !skill.installable && <p className="hub-helper">{t("hub.not_installable")}</p>}
      <div className="hub-skill-actions">
        {skill.inLibrary && skill.installable && <button type="button" className="primary" disabled={busy || !connected || !compatible.length} onClick={() => void run({ action: "install-skill", skillId: skill.id, ...(effectiveTarget ? { harnessId: effectiveTarget } : {}) })}>{t(skill.occurrences.length > 0 ? "hub.update_skill" : "hub.install_skill")}</button>}
        {isSkill && skill.occurrences.length > 0 && <UninstallSkill skill={skill} busy={busy} run={run} />}
      </div>
    </> : <>
      <div className="hub-skill-actions">
        <button type="button" className="secondary" disabled={busy || !connected} onClick={() => void run({ action: "export-item", skillId: skill.id })}>{t("hub.export_item")}</button>
        {itemType(skill) === "instruction" && <button type="button" className="primary" disabled={busy || !connected || !skill.installable || !tools.length} onClick={() => void run({ action: "install-instruction", skillId: skill.id })}>{t("hub.install_instruction")}</button>}
      </div>
      <p className="hub-helper">{t(itemType(skill) === "instruction" ? "hub.instruction_install_hint" : "hub.export_hint")}</p>
    </>}
    </div>
  </section>;
}

function UninstallSkill({ skill, busy, run }: { skill: HubSkill; busy: boolean; run: (command: HubCommand) => Promise<void> }) {
  const canUninstall = skill.occurrences.every(item => item.hash && !item.error);
  return <button type="button" className="secondary" disabled={busy || !canUninstall} onClick={() => void run({ action: "uninstall-skill", skillId: skill.id })}>{t("hub.uninstall_skill")}</button>;
}
