import { createWebHostClient, isWebHostAvailable } from "@shofia/webhost-client";
import { registerWindowControls, type WindowControlsElement } from "@shofia/webhost-client/window-controls";
import { createElement } from "react";
import type { DesktopState, SettingsUpdate, Provider } from "./types";
import type { HubCommand, HubState } from "./hubTypes";

const client = isWebHostAvailable() ? createWebHostClient() : null;
registerWindowControls();

// Use WebHost's shared implementation, including maximize/restore state and accessibility.
export function WindowControls() {
  return createElement("shofia-window-controls", {
    ref: (element: WindowControlsElement | null) => { if (element) element.client = client; }
  });
}
client?.connect();
window.addEventListener("pagehide", () => client?.dispose());
import.meta.hot?.dispose(() => client?.dispose());

function request<T>(type: string, payload: unknown): Promise<T> {
  if (!client) return Promise.reject(new Error("Open this window from the Estao desktop application."));
  return client.request<unknown, T>(type, payload);
}

export const getState = () => request<DesktopState>("estao.state", {});
export const getHubState = () => request<HubState>("estao.hub.state", {});
export const hubCommand = (command: HubCommand) => request("estao.hub.command", command);
export const saveSettings = (settings: SettingsUpdate) => request("estao.settings.save", settings);
export const signInProvider = (provider: Provider) => request("estao.provider.signIn", provider);
export const command = (action: string, provider?: string, accountIndex?: number) =>
  request("estao.command", { action, provider, accountIndex });
