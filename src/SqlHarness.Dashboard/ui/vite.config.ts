/// <reference types="vitest/config" />
import path from "node:path"
import tailwindcss from "@tailwindcss/vite"
import react from "@vitejs/plugin-react"
import { defineConfig } from "vite"

// Development only: `npm run dev` proxies /api to a running `sqlharness dashboard`.
// Set SQLHARNESS_DASHBOARD_URL (default http://127.0.0.1:47800) and SQLHARNESS_DASHBOARD_TOKEN
// (the t= value it printed); the token is sent as the session cookie by the proxy only.
const target = process.env.SQLHARNESS_DASHBOARD_URL ?? "http://127.0.0.1:47800"
const token = process.env.SQLHARNESS_DASHBOARD_TOKEN ?? ""

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: { "@": path.resolve(import.meta.dirname, "./src") },
  },
  build: {
    outDir: "dist",
    emptyOutDir: true,
    sourcemap: false,
  },
  server: {
    proxy: {
      "/api": {
        target,
        changeOrigin: true,
        headers: token ? { Cookie: `sqlharness_dashboard=${token}` } : {},
      },
    },
  },
  test: {
    // The Windows gate stalled without a summary under vmThreads after tests were
    // still making progress. The full suite completed with forks/1 (50 files,
    // 260 tests, 170.26s); favor the stable worker lifecycle over parallel speed.
    environment: "happy-dom",
    setupFiles: ["./src/test/setup.ts"],
    css: false,
    restoreMocks: true,
    pool: "forks",
    maxWorkers: 1,
    // StatsPage interactions exceed Vitest's 5s default on the Linux gate.
    testTimeout: 20_000,
  },
})
