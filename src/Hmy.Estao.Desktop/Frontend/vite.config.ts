import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import branding from "./src/branding.json" with { type: "json" };

export default defineConfig({
  plugins: [react(), { name: "branding-title", transformIndexHtml: html => html.replace("%APP_TITLE%", branding.displayName.replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[c]!)) }],
  server: {
    host: process.env.SHOFIA_FRONTEND_DEV_HOST ?? "127.0.0.1",
    port: Number(process.env.SHOFIA_FRONTEND_DEV_PORT ?? 0),
    strictPort: true
  }
});
