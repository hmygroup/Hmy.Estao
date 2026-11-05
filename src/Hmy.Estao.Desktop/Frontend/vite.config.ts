import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  server: {
    host: process.env.SHOFIA_FRONTEND_DEV_HOST ?? "127.0.0.1",
    port: Number(process.env.SHOFIA_FRONTEND_DEV_PORT ?? 0),
    strictPort: true
  }
});
