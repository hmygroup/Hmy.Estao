import { createWebHostClient, isWebHostAvailable } from "@shofia/webhost-client";
import { registerWindowControls, type WindowControlsElement } from "@shofia/webhost-client/window-controls";
import { createElement, useLayoutEffect, useRef } from "react";
import type { DesktopState, SettingsUpdate, Provider } from "./types";
import type { HubCommand, HubState } from "./hubTypes";
import { t } from "./i18n";

const client = isWebHostAvailable() ? createWebHostClient() : null;
registerWindowControls();

// Use WebHost's shared implementation, including maximize/restore state and accessibility.
export function WindowControls() {
  const element = useRef<WindowControlsElement | null>(null);
  useLayoutEffect(() => {
    const root = element.current?.shadowRoot;
    if (!root) return;
    const labels = () => {
      const buttons = root.querySelectorAll("button");
      buttons.forEach((button, index) => {
        const restored = buttons[1]?.querySelector("path")?.getAttribute("d")?.includes("M3 3");
        const label = t(["window.minimize", restored ? "window.restore" : "window.maximize", "window.close"][index]);
        if (button.title !== label) button.title = label;
        if (button.getAttribute("aria-label") !== label) button.setAttribute("aria-label", label);
      });
    };
    labels();
    const observer = new MutationObserver(labels);
    observer.observe(root, { attributes: true, subtree: true, attributeFilter: ["d", "title", "aria-label"] });
    return () => observer.disconnect();
  });
  return createElement("shofia-window-controls", {
    ref: (value: WindowControlsElement | null) => { element.current = value; if (value) value.client = client; }
  });
}
client?.connect();
window.addEventListener("pagehide", () => client?.dispose());
import.meta.hot?.dispose(() => client?.dispose());

async function request<T>(type: string, payload: unknown): Promise<T> {
  if (!client) return Promise.reject(Object.assign(new Error(t("host.required")), { userMessage: { code: "host.required", arguments: [] } }));
  const result = await client.request<unknown, T>(type, payload);
  if (result && typeof result === "object" && "error" in result && result.error && typeof result.error === "object" && "code" in result.error)
    throw Object.assign(new Error("Native operation failed"), { userMessage: result.error });
  return result;
}

export const getState = () => request<DesktopState>("estao.state", {});
export const getHubState = () => request<HubState>("estao.hub.state", {});
export const hubCommand = (command: HubCommand) => request("estao.hub.command", command);
export const saveSettings = (settings: SettingsUpdate) => request("estao.settings.save", settings);
export const signInProvider = (provider: Provider) => request("estao.provider.signIn", provider);
export const command = (action: string, provider?: string, accountIndex?: number) =>
  request("estao.command", { action, provider, accountIndex });
