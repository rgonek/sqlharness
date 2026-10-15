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
    // Measured on the Windows gate, same worktree, 35 files and 163 tests:
    //   jsdom                     116.6s
    //   happy-dom                  49.7s
    //   happy-dom + vmThreads      22.2s
    // The environment was created once per file and cost 52% (jsdom) or 32%
    // (happy-dom) of the suite; vmThreads reuses one environment per worker while
    // keeping per-file isolation.
    environment: "happy-dom",
    setupFiles: ["./src/test/setup.ts"],
    css: false,
    restoreMocks: true,
    pool: "vmThreads",
    // More workers than this oversubscribes the machine and measured slower
    // (29.6s against 22.2s on eight logical cores).
    maxWorkers: 4,
    // StatsPage interactions exceed Vitest's 5s default on the Linux gate.
    testTimeout: 20_000,
  },
})
