import en from "./locales/en.json";
import es from "./locales/es.json";
import fr from "./locales/fr.json";
import { appName } from "./branding";

export interface UserMessage { code: string; arguments: unknown[] }
const catalogs: Record<string, Record<string, string>> = { en, es, fr };
export function resolveLanguage(value?: string, system = navigator.language): string {
  const requested = (!value || value === "system" ? system : value).toLowerCase().split(/[-_]/)[0];
  return requested in catalogs ? requested : "en";
}
function savedLanguage() { try { return localStorage.getItem("estao.language") || "system"; } catch { return "system"; } }
let language = resolveLanguage(savedLanguage());
export const currentLanguage = () => language;
export function setLanguage(value: string) {
  language = resolveLanguage(value);
  document.documentElement.lang = language;
  try { localStorage.setItem("estao.language", value); } catch { /* Native settings remain authoritative. */ }
}
export function t(key: string, ...args: unknown[]): string {
  const template = catalogs[language][key] ?? en[key as keyof typeof en] ?? catalogs[language]["error.operation"];
  return template.replace(/\{(appName|\d+)\}/g, (token, index) => {
    if (index === "appName") return appName;
    const value = args[Number(index)];
    return value == null ? token : typeof value === "number" ? new Intl.NumberFormat(language).format(value) : String(value);
  });
}
export const plural = (key: string, count: number, ...args: unknown[]) => {
  const variant = `${key}_${new Intl.PluralRules(language).select(count)}`;
  // French also has a "many" category for millions; use the plural form unless explicitly provided.
  return t(variant in catalogs[language] || variant in en ? variant : `${key}_other`, ...(args.length ? args : [count]));
};
export function message(value?: UserMessage | null): string {
  if (!value) return "";
  const args = value.arguments.map(v => v && typeof v === "object" && "code" in v ? message(v as UserMessage) : v);
  return value.code === "message.list" ? args.join("\n") : t(value.code, ...args);
}
export const number = (value: number) => new Intl.NumberFormat(language).format(value);
export const percent = (value: number) => new Intl.NumberFormat(language, { style: "percent", maximumFractionDigits: 0 }).format(value);
export const dateTime = (value: string) => new Date(value).toLocaleString(language);
export const time = (value: string) => new Date(value).toLocaleTimeString(language, { hour: "2-digit", minute: "2-digit" });
export function windowTitle(id: string) { return t(`window.${id}` in en ? `window.${id}` : "window.other"); }
export function sourceName(id: string) { return `source.${id}` in en ? t(`source.${id}`) : id; }
export function errorText(error: unknown) {
  // Raw exceptions may contain untranslated server messages or credentials. Never show them as UI copy.
  return error && typeof error === "object" && "userMessage" in error ? message((error as { userMessage: UserMessage }).userMessage) : t("error.operation");
}
