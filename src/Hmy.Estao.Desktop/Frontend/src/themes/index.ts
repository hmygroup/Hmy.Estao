import type { CSSProperties } from "react";
import catalog from "./themes.json";

export interface ThemeDefinition {
  id: string; name: string; mode: "light" | "dark"; description: string;
  aliases: string[]; colors: Record<string, string>;
}
export const themes = catalog as ThemeDefinition[];
export const defaultTheme = themes[0];

export function resolveTheme(value?: string | null): ThemeDefinition {
  const id = value?.trim().toLowerCase();
  return themes.find(theme => theme.id === id || theme.aliases.some(alias => alias.toLowerCase() === id)) || defaultTheme;
}

export function themeStyle(theme: ThemeDefinition): CSSProperties {
  return Object.fromEntries(Object.entries(theme.colors).map(([key, value]) => [`--theme-${key}`, value])) as CSSProperties;
}

export function applyTheme(value?: string | null) {
  const theme = resolveTheme(value);
  const root = document.documentElement;
  root.dataset.theme = theme.id;
  root.dataset.themeMode = theme.mode;
  root.style.colorScheme = theme.mode;
  for (const [key, color] of Object.entries(theme.colors)) root.style.setProperty(`--theme-${key}`, color);
}

// Only the saved theme ID is cached, never settings or credentials. A draft
// preview must not become the next window's persisted theme before Save.
const cacheKey = "estao.savedTheme";
export function cachedTheme(): string {
  const initial = (window as Window & { __estaoInitialTheme?: string }).__estaoInitialTheme;
  if (initial) return resolveTheme(initial).id;
  try { return resolveTheme(localStorage.getItem(cacheKey)).id; }
  catch { return defaultTheme.id; }
}
export function cacheTheme(value: string) {
  try { localStorage.setItem(cacheKey, resolveTheme(value).id); }
  catch { /* Storage may be disabled; the native config remains authoritative. */ }
}
