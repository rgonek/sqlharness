import React from "react"
import { createRoot } from "react-dom/client"
import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { createMemoryHistory, RouterProvider } from "@tanstack/react-router"
import "../../../src/SqlHarness.Dashboard/ui/src/index.css"
import { createAppRouter } from "../../../src/SqlHarness.Dashboard/ui/src/router"
import { stats } from "../../../src/SqlHarness.Dashboard/ui/src/test/fixtures"

const profileName = "Production Profile with a deliberately long retained label for narrow screens"
const initialSettings = { status: "valid", path: "C:/fixture/config.json", settings: {
  journal: { enabled: true, storeSensitive: false, retention: { enabled: false, maxAgeDays: 30, maxSizeMb: 500 } },
  dashboard: { autoStart: false, port: 47800, idleShutdownHours: 8, safetyNotifications: false, agentTranscripts: { enabled: false } },
  compare: { requireContext: true }, diagnostics: { enabled: false },
  mutations: { defaultMode: "never", verification: "click", approvalTimeoutSeconds: 120 }, connection: { timeoutSeconds: 30 },
} }
const profile = { name: profileName, engine: "sqlserver", server: "server-{region}", database: "{project}.{component}", auth: "sql", sqlUser: null,
  passwordEnvVar: null, sslMode: null, trustServerCertificate: false, tls: "verify", rootCertificate: null,
  vars: [{ name: "project", rule: ".*" }, { name: "component", rule: ".*" }, { name: "region", rule: ".*" }] }
const statsData = stats({ profileOperations: [{ profile: profileName, operations: 6 }], profileDimensions: { ...stats().profileDimensions!, profile: profileName } })
globalThis.fetch = (async (input: RequestInfo | URL, init?: RequestInit) => {
  const url = new URL(String(input), location.href)
  const data = url.pathname === "/api/settings" ? initialSettings
    : url.pathname === "/api/prices" ? { source: "fixture", fetchedAt: "2026-10-20T00:00:00Z", origin: "embedded" }
    : url.pathname === "/api/transcripts" ? { enabled: false, matched: 0, unmatched: 0, readErrors: 0, lastCycleAt: null, coverage: [] }
    : url.pathname === "/api/profiles" ? { status: "valid", message: null, profiles: [profile] }
    : url.pathname === "/api/stats" ? statsData : null
  if (data === null) return new Response(JSON.stringify({ message: `unmapped fixture API ${url.pathname}` }), { status: 404, headers: { "Content-Type": "application/json" } })
  const value = init?.method === "PUT" && url.pathname === "/api/settings" ? { ...initialSettings, settings: JSON.parse(String(init.body)) } : data
  return new Response(JSON.stringify(value), { status: 200, headers: { "Content-Type": "application/json" } })
}) as typeof fetch
const initialPath = new URLSearchParams(location.search).get("page") === "stats" ? "/stats" : "/settings"
const router = createAppRouter(createMemoryHistory({ initialEntries: [initialPath] }))
const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
createRoot(document.getElementById("app")!).render(<QueryClientProvider client={queryClient}><RouterProvider router={router} /></QueryClientProvider>)
