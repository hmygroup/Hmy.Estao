export interface HubTool { id: string; name: string; detected: boolean; status: string; error: string | null }
export interface HubFile { path: string; action: string; reason: string }
export interface HubArtifact {
  id: string; description: string; category: string; status: "new" | "update" | "present" | "conflict";
  fileCount: number; source: string; requirements: string[]; dependencies: string[]; files: HubFile[];
}
export interface HubToolReview {
  id: string; name: string; destination: string; artifacts: HubArtifact[];
  exclusions: { source: string; classification: string; reason: string; fileCount: number }[];
}
export interface HubReview { id: string; commit: string; tools: HubToolReview[] }
export interface HubResult { id: string; name: string; outcome: string; created: number; updated: number; unchanged: number; conflicts: number; error: string | null }
export interface HubState {
  busy: boolean; phase: string; progress: string; connected: boolean; connection: string;
  tools: HubTool[]; review: HubReview | null; results: HubResult[];
  deviceCode: { code: string; verificationUrl: string } | null; error: string | null;
}
export interface HubCommand { action: string; harnessId?: string; reviewId?: string; selection?: Record<string, string[]> }

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
