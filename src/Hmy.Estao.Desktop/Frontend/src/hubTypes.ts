import type { UserMessage } from "./i18n";
export interface HubTool { id: string; name: string; detected: boolean; status: string; error: string | null; errorMessage?: UserMessage }
export interface HubFile { path: string; action: string; reason: string; reasonMessage?: UserMessage }
export interface HubArtifact {
  id: string; description: string; category: string; status: "new" | "update" | "present" | "conflict";
  fileCount: number; source: string; requirements: string[]; dependencies: string[]; files: HubFile[];
}
export interface HubToolReview {
  id: string; name: string; destination: string; artifacts: HubArtifact[];
  exclusions: { source: string; classification: string; reason: string; fileCount: number }[];
}
export interface HubReview { id: string; commit: string; tools: HubToolReview[] }
export interface HubResult { id: string; name: string; outcome: string; created: number; updated: number; unchanged: number; conflicts: number; error: string | null; errorMessage?: UserMessage }
export interface HubState {
  skills?: HubSkill[] | null; libraryCommit?: string | null; libraryError?: string | null; libraryLoaded?: boolean;
  catalogCategories?: Record<string, Record<string, string>> | null; catalogDefaultLanguage?: string; catalogError?: string | null;
  authorizationChecked?: boolean; checkingAuthorization?: boolean;
  progressMessage?: UserMessage; connectionMessage?: UserMessage; errorMessage?: UserMessage;
  busy: boolean; phase: string; progress: string; connected: boolean; connection: string;
  tools: HubTool[]; review: HubReview | null; results: HubResult[];
  deviceCode: { code: string; verificationUrl: string } | null; error: string | null;
}
export interface HubCommand { action: string; harnessId?: string; reviewId?: string; selection?: Record<string, string[]>; skillId?: string }

export interface HubSkillOccurrence { toolId: string; path: string; shared: boolean; hash: string | null; error?: string | null }
export interface HubSkill {
  id: string; name: string; description: string; inLibrary: boolean; installable: boolean;
  compatibleTools: string[]; occurrences: HubSkillOccurrence[]; source?: string | null; issue?: string | null;
  curated?: boolean; itemType?: "skill" | "instruction" | "file" | string; descriptions?: Record<string, string> | null;
  categories?: string[] | null; defaultLanguage?: string;
}
export type SkillView = "installed" | "library" | "other";
export type SkillStatus = "all" | "missing" | "attention" | "installable";
export type CatalogItemType = "all" | "skill" | "instruction" | "file";
export function detectedTools(tools: HubTool[]): HubTool[] { return tools.filter(tool => tool.detected); }
export function isCurated(skill: HubSkill): boolean { return skill.curated ?? skill.inLibrary; }
export function localizedDescription(skill: HubSkill, language: string): string {
  return skill.descriptions?.[language] || skill.descriptions?.[skill.defaultLanguage || "en"] || skill.description || "";
}
export function localizedCategory(category: string, catalogCategories: HubState["catalogCategories"], language: string, defaultLanguage = "en"): string {
  const labels = catalogCategories?.[category];
  return labels?.[language] || labels?.[defaultLanguage] || labels?.["en"] || category;
}
export function skillCategories(skill: HubSkill[]): string[] {
  return [...new Set(skill.flatMap(item => item.categories ?? []))].sort((a, b) => a.localeCompare(b));
}
export function itemType(skill: HubSkill): string { return skill.itemType || "skill"; }
export function skillToolStatus(skill: HubSkill, tool: HubTool): "present" | "missing" | "unknown" | "unsupported" {
  const occurrences = skill.occurrences.filter(item => item.toolId === tool.id);
  if (occurrences.some(item => item.error)) return "unknown";
  if (occurrences.length > 0) return "present";
  if (tool.error) return "unknown";
  if (isCurated(skill) && skill.compatibleTools?.length && !skill.compatibleTools.includes(tool.id)) return "unsupported";
  return "missing";
}
export function filterSkills(skills: HubSkill[], tools: HubTool[], view: SkillView, query: string, toolId: string, status: SkillStatus,
  language = "en", category = "", type: CatalogItemType = "all", catalogCategories?: HubState["catalogCategories"], catalogDefaultLanguage = "en"): HubSkill[] {
  const visible = detectedTools(tools);
  const selected = visible.filter(tool => !toolId || tool.id === toolId);
  const text = query.trim().toLocaleLowerCase();
  return skills.filter(skill => {
    const installed = skill.occurrences.some(item => visible.some(tool => tool.id === item.toolId));
    if (view === "installed" ? (!installed || itemType(skill) !== "skill")
      : view === "library" ? !isCurated(skill) : (isCurated(skill) || itemType(skill) !== "skill")) return false;
    if (view !== "installed" && type !== "all" && itemType(skill) !== type) return false;
    if (category === "__uncategorized" && (skill.categories ?? []).length > 0) return false;
    if (category && category !== "__uncategorized" && !(skill.categories ?? []).includes(category)) return false;
    if (text && !`${skill.name} ${skill.id} ${localizedDescription(skill, language)} ${(skill.categories ?? []).map(value => localizedCategory(value, catalogCategories, language, catalogDefaultLanguage)).join(" ")}`.toLocaleLowerCase(language).includes(text)) return false;
    if (toolId && itemType(skill) !== "skill") return false;
    if (itemType(skill) === "skill" && toolId && !selected.some(tool => view === "installed"
      ? skill.occurrences.some(item => item.toolId === tool.id)
      : skill.compatibleTools.includes(tool.id) || skill.occurrences.some(item => item.toolId === tool.id))) return false;
    if (status === "installable" && (itemType(skill) !== "skill" || !skill.installable || !selected.some(tool => skill.compatibleTools.includes(tool.id)))) return false;
    if (status === "attention") {
      if (itemType(skill) !== "skill") return false;
      const local = skill.occurrences.filter(item => selected.some(tool => tool.id === item.toolId));
      const differing = local.some(item => item.hash && skill.occurrences.some(other => other.hash && other.hash !== item.hash));
      const issue = skill.issue && (!toolId || differing || (isCurated(skill) && !skill.installable));
      if (!issue && !selected.some(tool => skillToolStatus(skill, tool) === "unknown")) return false;
    }
    if (status === "missing" && (itemType(skill) !== "skill" || !selected.some(tool => skillToolStatus(skill, tool) === "missing"))) return false;
    return true;
  }).sort((a, b) => a.name.localeCompare(b.name) || a.id.localeCompare(b.id));
}

export function showHubConnect(state?: Pick<HubState, "connected" | "authorizationChecked" | "checkingAuthorization">): boolean {
  return state?.authorizationChecked === true && !state.checkingAuthorization && !state.connected;
}

export function defaultSelection(review: HubReview): Record<string, string[]> {
  return Object.fromEntries(review.tools.map(tool => {
    const chosen = new Set(tool.artifacts.filter(a => a.status !== "conflict").map(a => a.id));
    let changed: boolean;
    do {
      changed = false;
      for (const artifact of tool.artifacts)
        if (chosen.has(artifact.id) && artifact.dependencies.some(id => !chosen.has(id))) { chosen.delete(artifact.id); changed = true; }
    } while (changed);
    return [tool.id, [...chosen]];
  }));
}

export function updateSelection(tool: HubToolReview, selected: string[], id: string, include: boolean): string[] {
  const chosen = new Set(selected);
  const available = new Map(tool.artifacts.map(a => [a.id, a]));
  if (include) {
    const visit = (key: string): boolean => {
      const artifact = available.get(key);
      if (!artifact || artifact.status === "conflict") return false;
      if (chosen.has(key)) return true;
      chosen.add(key);
      return artifact.dependencies.every(visit);
    };
    if (!visit(id)) return selected;
  } else {
    chosen.delete(id);
    let changed: boolean;
    do {
      changed = false;
      for (const artifact of tool.artifacts)
        if (chosen.has(artifact.id) && artifact.dependencies.some(key => !chosen.has(key))) { chosen.delete(artifact.id); changed = true; }
    } while (changed);
  }
  return [...chosen];
}
