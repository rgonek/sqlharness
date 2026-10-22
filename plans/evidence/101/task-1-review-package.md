 src/SqlHarness.Core/SqlHarnessConfig.cs            |   4 +-
 src/SqlHarness.Dashboard/ui/src/api/types.ts       |   1 +
 .../ui/src/lib/settingsRules.test.ts               |   2 +-
 .../ui/src/pages/AgentSessionPage.test.tsx         |   2 +-
 .../ui/src/pages/LivePage.test.tsx                 |   2 +-
 .../ui/src/pages/SessionsPage.test.tsx             |   4 +-
 .../ui/src/pages/SettingsMutations.test.tsx        |   2 +-
 .../ui/src/pages/SettingsPage.test.tsx             | 118 ++++++++++++++++++++-
 .../ui/src/pages/SettingsPage.tsx                  |   8 +-
 .../Dashboard/DashboardSettingsTests.cs            |  40 ++++++-
 10 files changed, 173 insertions(+), 10 deletions(-)
diff --git a/src/SqlHarness.Core/SqlHarnessConfig.cs b/src/SqlHarness.Core/SqlHarnessConfig.cs
index 084186a..af1de2d 100644
--- a/src/SqlHarness.Core/SqlHarnessConfig.cs
+++ b/src/SqlHarness.Core/SqlHarnessConfig.cs
@@ -163,20 +163,22 @@ public static class SqlHarnessConfigLoader
         }
 
         if (config is null || !IsComplete(config))
             return Failed("$", "The journal, journal.retention, dashboard, dashboard.agentTranscripts, compare, diagnostics, mutations, connection and execution sections must be objects.");
 
         var errors = new List<SqlHarnessConfigFieldError>();
         if (config.Journal.Retention.MaxAgeDays is < 1 or > 3650)
             errors.Add(new("journal.retention.maxAgeDays", "Must be between 1 and 3650."));
         if (config.Journal.Retention.MaxSizeMb is < 10 or > 102_400)
             errors.Add(new("journal.retention.maxSizeMb", "Must be between 10 and 102400."));
+        if (config.Journal.OperationAnnotations && !config.Journal.Enabled)
+            errors.Add(new("journal.operationAnnotations", "Requires journal.enabled to be true."));
         if (config.Dashboard.Port is < 1024 or > 65535)
             errors.Add(new("dashboard.port", "Must be between 1024 and 65535."));
         if (config.Dashboard.IdleShutdownHours is < 1 or > 168)
             errors.Add(new("dashboard.idleShutdownHours", "Must be between 1 and 168."));
         if (!Enum.GetNames<MutationMode>().Any(name => string.Equals(name, config.Mutations.DefaultMode, StringComparison.OrdinalIgnoreCase)))
             errors.Add(new("mutations.defaultMode", "Must be never, operator or yolo."));
         if (!string.Equals(config.Mutations.Verification, "click", StringComparison.OrdinalIgnoreCase))
             errors.Add(new("mutations.verification", "Unsupported verification; click is the only supported value."));
         if (config.Mutations.ApprovalTimeoutSeconds is < 30 or > 600)
             errors.Add(new("mutations.approvalTimeoutSeconds", "Must be between 30 and 600."));
@@ -190,11 +192,11 @@ public static class SqlHarnessConfigLoader
     private static SqlHarnessConfigParseResult Failed(string field, string message) => new(null, [new(field, message)]);
 
     private static SqlHarnessConfigLoadResult Invalid(string? warning = null) =>
         new(SqlHarnessConfig.Default, SqlHarnessConfigStatus.Invalid, warning ?? InvalidWarning);
 
     // Explicit JSON nulls bypass initializers; treat them as invalid.
     private static bool IsComplete(SqlHarnessConfig config) =>
         config.Journal is not null && config.Journal.Retention is not null && config.Dashboard is not null &&
         config.Dashboard.AgentTranscripts is not null && config.Compare is not null && config.Diagnostics is not null &&
         config.Mutations is not null && config.Connection is not null && config.Execution is not null;
-}
\ No newline at end of file
+}
diff --git a/src/SqlHarness.Dashboard/ui/src/api/types.ts b/src/SqlHarness.Dashboard/ui/src/api/types.ts
index 823511e..9151dc3 100644
--- a/src/SqlHarness.Dashboard/ui/src/api/types.ts
+++ b/src/SqlHarness.Dashboard/ui/src/api/types.ts
@@ -498,20 +498,21 @@ export type MissingIndex = {
   impact: number
 }
 // PlanStatementJsonConverter writes the text as "sql" and omits "missingIndexes" when empty.
 export type DistilledStatement = { sql?: string; root: PlanNode; missingIndexes?: MissingIndex[] }
 export type DistilledPlan = { statements: DistilledStatement[] }
 
 // Mirrors src/SqlHarness.Dashboard/DashboardSettings.cs (JSON camelCase).
 export type JournalSettings = {
   enabled: boolean
   storeSensitive: boolean
+  operationAnnotations: boolean
   retention: { enabled: boolean; maxAgeDays: number; maxSizeMb: number }
 }
 export type DashboardSettings = { autoStart: boolean; port: number; idleShutdownHours: number; safetyNotifications: boolean; agentTranscripts: { enabled: boolean } }
 export type DiagnosticsSettings = { enabled: boolean }
 export type MutationSettings = { defaultMode: "never" | "operator" | "yolo"; verification: string; approvalTimeoutSeconds: number }
 export type ExecutionSettings = {
   defaultTimeoutSeconds: number
 }
 export type Settings = { journal: JournalSettings; dashboard: DashboardSettings; compare: { requireContext: boolean }; diagnostics: DiagnosticsSettings; mutations: MutationSettings; connection: { timeoutSeconds: number }; execution: ExecutionSettings }
 export type SettingsFileStatus = "missing" | "valid" | "invalid"
diff --git a/src/SqlHarness.Dashboard/ui/src/lib/settingsRules.test.ts b/src/SqlHarness.Dashboard/ui/src/lib/settingsRules.test.ts
index e6158dd..1aa2e31 100644
--- a/src/SqlHarness.Dashboard/ui/src/lib/settingsRules.test.ts
+++ b/src/SqlHarness.Dashboard/ui/src/lib/settingsRules.test.ts
@@ -1,16 +1,16 @@
 import { expect, test } from "vitest"
 import type { Settings } from "@/api/types"
 import { isDestructive } from "@/lib/settingsRules"
 
 const make = (enabled: boolean, maxAgeDays = 30, maxSizeMb = 500): Settings => ({
-  journal: { enabled: true, storeSensitive: false, retention: { enabled, maxAgeDays, maxSizeMb } },
+  journal: { enabled: true, storeSensitive: false, operationAnnotations: false, retention: { enabled, maxAgeDays, maxSizeMb } },
   dashboard: { autoStart: false, port: 47800, idleShutdownHours: 8, safetyNotifications: false, agentTranscripts: { enabled: false } },
   compare: { requireContext: true },
   connection: { timeoutSeconds: 30 },
   diagnostics: { enabled: false },
   mutations: { defaultMode: "never", verification: "click", approvalTimeoutSeconds: 120 },
   execution: { defaultTimeoutSeconds: 30 },
 })
 
 test("enabling retention is destructive", () => expect(isDestructive(make(false), make(true))).toBe(true))
 test("lowering max age with retention on is destructive", () => expect(isDestructive(make(true), make(true, 7))).toBe(true))
diff --git a/src/SqlHarness.Dashboard/ui/src/pages/AgentSessionPage.test.tsx b/src/SqlHarness.Dashboard/ui/src/pages/AgentSessionPage.test.tsx
index 1030ee2..69e9e58 100644
--- a/src/SqlHarness.Dashboard/ui/src/pages/AgentSessionPage.test.tsx
+++ b/src/SqlHarness.Dashboard/ui/src/pages/AgentSessionPage.test.tsx
@@ -4,21 +4,21 @@ import userEvent from "@testing-library/user-event"
 import { expect, test } from "vitest"
 import { createQueryClient, useSessionSqlText } from "@/api/queries"
 import { operation, session } from "@/test/fixtures"
 import { renderApp, stubFetch } from "@/test/render"
 
 const summary = {
   key: { agentKind: "codex", conversationId: "conversation-1" }, title: "Optimize orders", conversationTitle: "Optimize orders", ownerTitle: null,
   notePreview: "first note line", starred: false, archived: false, exact: 1, inferred: 1, unassigned: 1,
   lastSeen: "2026-10-07T09:05:00.000Z", children: [{ session: session({ id: 7, sessionKey: "mcp:child", hasCompare: true }), exact: 1, inferred: 1, unassigned: 1 }],
 }
-const settings = { status: "valid", path: "config.json", settings: { journal: { enabled: true, storeSensitive: false, retention: { enabled: false, maxAgeDays: 30, maxSizeMb: 500 } }, dashboard: { autoStart: false, port: 47800, idleShutdownHours: 8, safetyNotifications: false, agentTranscripts: { enabled: true } }, compare: { requireContext: true }, diagnostics: { enabled: false }, mutations: { defaultMode: "never", verification: "click", approvalTimeoutSeconds: 120 }, connection: { timeoutSeconds: 30 } } }
+const settings = { status: "valid", path: "config.json", settings: { journal: { enabled: true, storeSensitive: false, operationAnnotations: false, retention: { enabled: false, maxAgeDays: 30, maxSizeMb: 500 } }, dashboard: { autoStart: false, port: 47800, idleShutdownHours: 8, safetyNotifications: false, agentTranscripts: { enabled: true } }, compare: { requireContext: true }, diagnostics: { enabled: false }, mutations: { defaultMode: "never", verification: "click", approvalTimeoutSeconds: 120 }, connection: { timeoutSeconds: 30 } } }
 
 test("agent detail shows membership counts, markers, annotations, and child optimization links", async () => {
   const patches: unknown[] = []
   const calls = stubFetch({
     "/api/settings": settings,
     "/api/agent-sessions/codex/conversation-1": { agentSession: summary, note: "full owner note", operations: [
       { operation: operation({ id: 11 }), membership: "exact" },
       { operation: operation({ id: 12 }), membership: "inferred" },
       { operation: operation({ id: 13 }), membership: "unassigned" },
     ] },
diff --git a/src/SqlHarness.Dashboard/ui/src/pages/LivePage.test.tsx b/src/SqlHarness.Dashboard/ui/src/pages/LivePage.test.tsx
index 3525585..361c964 100644
--- a/src/SqlHarness.Dashboard/ui/src/pages/LivePage.test.tsx
+++ b/src/SqlHarness.Dashboard/ui/src/pages/LivePage.test.tsx
@@ -215,21 +215,21 @@ test("clearing transcript data removes stale titles and models from the live cac
   const liveOperation = () => operation({ id: 12, status: "running", operation: "watch", agentModel: cleared ? null : "claude-opus-5-5",
     agentModelSource: cleared ? null : "transcript", updatedAt: "2026-10-07T09:00:01.000Z" })
   const liveSession = () => session({ id: 14, sessionKey: "cli:live", running: 1, agentTitle: cleared ? null : "Optimize orders",
     lastSeen: "2026-10-07T09:00:01.000Z" })
   const calls = stubFetch({
     [RECENT]: () => json(page([liveOperation()])),
     [RUNNING]: () => json(page([liveOperation()])),
     [STARRED]: page([]),
     [SESSIONS]: () => json(page([liveSession()])),
     "/api/settings": { status: "valid", path: "settings.json", settings: {
-      journal: { enabled: true, storeSensitive: false, retention: { enabled: false, maxAgeDays: 30, maxSizeMb: 500 } },
+      journal: { enabled: true, storeSensitive: false, operationAnnotations: false, retention: { enabled: false, maxAgeDays: 30, maxSizeMb: 500 } },
       dashboard: { autoStart: false, port: 47800, idleShutdownHours: 8, safetyNotifications: false, agentTranscripts: { enabled: false } }, compare: { requireContext: true },
       connection: { timeoutSeconds: 30 },
       execution: { defaultTimeoutSeconds: 30 },
       diagnostics: { enabled: false },
       mutations: { defaultMode: "never", verification: "click", approvalTimeoutSeconds: 120 },
     } },
     "/api/prices": { source: "embedded", fetchedAt: "2026-10-07T09:00:00Z", origin: "embedded" },
     "/api/transcripts": { enabled: false, matched: 1, unmatched: 0, readErrors: 0, lastCycleAt: null },
     "POST /api/transcripts/clear": () => {
       cleared = true
diff --git a/src/SqlHarness.Dashboard/ui/src/pages/SessionsPage.test.tsx b/src/SqlHarness.Dashboard/ui/src/pages/SessionsPage.test.tsx
index 914eda9..8581f2c 100644
--- a/src/SqlHarness.Dashboard/ui/src/pages/SessionsPage.test.tsx
+++ b/src/SqlHarness.Dashboard/ui/src/pages/SessionsPage.test.tsx
@@ -225,30 +225,30 @@ test("matching off returns immediately to per-session rows even when agent rows
     "/api/sessions": (url: URL) => new Response(JSON.stringify({ items: url.searchParams.get("starred") === "true" ? [] : [session({ id: 2, sessionKey: "mcp:plain" })], nextCursor: null, matching }), { status: 200 }),
     "/api/agent-sessions": (url: URL) => new Response(JSON.stringify({ items: url.searchParams.get("starred") === "true" ? [] : [{
       key: { agentKind: "codex", conversationId: "cached" }, title: "stale agent row", conversationTitle: null, ownerTitle: null, notePreview: null,
       starred: false, archived: false, exact: 0, inferred: 0, unassigned: 0, lastSeen: "2026-10-07T09:05:00.000Z", children: [],
     }], nextCursor: null }), { status: 200 }),
     "/api/agent-sessions/unmatched": { items: [], nextCursor: null, matching: true },
     "/api/prices": { source: "embedded", fetchedAt: "2026-10-07T09:00:00Z", origin: "embedded" },
     "/api/transcripts": { enabled: false, matched: 0, unmatched: 0, readErrors: 0, lastCycleAt: null },
     "/api/settings": () => {
       return new Response(JSON.stringify({ status: "valid", path: "config.json", settings: {
-        journal: { enabled: true, storeSensitive: false, retention: { enabled: false, maxAgeDays: 30, maxSizeMb: 500 } },
+        journal: { enabled: true, storeSensitive: false, operationAnnotations: false, retention: { enabled: false, maxAgeDays: 30, maxSizeMb: 500 } },
         dashboard: { autoStart: false, port: 47800, idleShutdownHours: 8, safetyNotifications: false, agentTranscripts: { enabled } },
         compare: { requireContext: true }, diagnostics: { enabled: false }, mutations: { defaultMode: "never", verification: "click", approvalTimeoutSeconds: 120 }, connection: { timeoutSeconds: 30 }, execution: { defaultTimeoutSeconds: 30 },
       } }), { status: 200 })
     },
     "PUT /api/settings": () => {
       enabled = false
       matching = false
       return new Response(JSON.stringify({ status: "valid", path: "config.json", settings: {
-        journal: { enabled: true, storeSensitive: false, retention: { enabled: false, maxAgeDays: 30, maxSizeMb: 500 } },
+        journal: { enabled: true, storeSensitive: false, operationAnnotations: false, retention: { enabled: false, maxAgeDays: 30, maxSizeMb: 500 } },
         dashboard: { autoStart: false, port: 47800, idleShutdownHours: 8, safetyNotifications: false, agentTranscripts: { enabled } },
         compare: { requireContext: true }, diagnostics: { enabled: false }, mutations: { defaultMode: "never", verification: "click", approvalTimeoutSeconds: 120 }, connection: { timeoutSeconds: 30 }, execution: { defaultTimeoutSeconds: 30 },
       } }), { status: 200 })
     },
   })
   renderApp("/sessions")
   expect(await screen.findByText("stale agent row")).toBeInTheDocument()
   await userEvent.click(screen.getByRole("link", { name: "Settings" }))
   await userEvent.click(await screen.findByRole("switch", { name: "Read agent transcripts" }))
   await waitFor(() => expect(enabled).toBe(false))
diff --git a/src/SqlHarness.Dashboard/ui/src/pages/SettingsMutations.test.tsx b/src/SqlHarness.Dashboard/ui/src/pages/SettingsMutations.test.tsx
index d13bc36..09c592b 100644
--- a/src/SqlHarness.Dashboard/ui/src/pages/SettingsMutations.test.tsx
+++ b/src/SqlHarness.Dashboard/ui/src/pages/SettingsMutations.test.tsx
@@ -1,19 +1,19 @@
 import { fireEvent, screen, waitFor } from "@testing-library/react"
 import userEvent from "@testing-library/user-event"
 import { expect, test, vi } from "vitest"
 import { renderApp, stubFetch } from "@/test/render"
 import * as sound from "@/mutations/sound"
 
 const initial = {
   status: "valid", path: "config.json", settings: {
-    journal: { enabled: true, storeSensitive: false, retention: { enabled: false, maxAgeDays: 30, maxSizeMb: 500 } },
+    journal: { enabled: true, storeSensitive: false, operationAnnotations: false, retention: { enabled: false, maxAgeDays: 30, maxSizeMb: 500 } },
     dashboard: { autoStart: false, port: 47800, idleShutdownHours: 8, safetyNotifications: false, agentTranscripts: { enabled: false } },
     compare: { requireContext: true }, diagnostics: { enabled: false },
     connection: { timeoutSeconds: 30 },
     execution: { defaultTimeoutSeconds: 30 },
     mutations: { defaultMode: "never", verification: "click", approvalTimeoutSeconds: 120 },
   },
 }
 
 function routes(onPut?: (body: unknown) => Response) {
   const puts: unknown[] = []
diff --git a/src/SqlHarness.Dashboard/ui/src/pages/SettingsPage.test.tsx b/src/SqlHarness.Dashboard/ui/src/pages/SettingsPage.test.tsx
index dca4395..26f5735 100644
--- a/src/SqlHarness.Dashboard/ui/src/pages/SettingsPage.test.tsx
+++ b/src/SqlHarness.Dashboard/ui/src/pages/SettingsPage.test.tsx
@@ -1,21 +1,21 @@
 import { screen, waitFor, within } from "@testing-library/react"
 import userEvent from "@testing-library/user-event"
 import { expect, test } from "vitest"
 import type { SettingsResponse } from "@/api/types"
 import { renderApp, stubFetch } from "@/test/render"
 
 const response = (over: Partial<SettingsResponse> = {}): SettingsResponse => ({
   status: "valid",
   path: "C:/Users/me/.sqlharness/config.json",
   settings: {
-    journal: { enabled: true, storeSensitive: false, retention: { enabled: false, maxAgeDays: 30, maxSizeMb: 500 } },
+    journal: { enabled: true, storeSensitive: false, operationAnnotations: false, retention: { enabled: false, maxAgeDays: 30, maxSizeMb: 500 } },
     dashboard: { autoStart: false, port: 47800, idleShutdownHours: 8, safetyNotifications: false, agentTranscripts: { enabled: false } },
     compare: { requireContext: true },
     diagnostics: { enabled: false },
     mutations: { defaultMode: "never", verification: "click", approvalTimeoutSeconds: 120 },
     connection: { timeoutSeconds: 30 },
     execution: { defaultTimeoutSeconds: 30 },
   },
   ...over,
 })
 
@@ -145,20 +145,136 @@ test("enabling storeSensitive asks for confirmation, then saves", async () => {
 })
 
 test("disabling storeSensitive needs no confirmation", async () => {
   stubSettings(response({ settings: { ...response().settings, journal: { ...response().settings.journal, storeSensitive: true } } }))
   renderApp("/settings")
 
   await userEvent.click(await screen.findByRole("switch", { name: "Store sensitive data" }))
   expect(screen.queryByRole("dialog")).not.toBeInTheDocument()
 })
 
+test("operation annotations default off and show process restart guidance", async () => {
+  const puts = stubSettings(response())
+  renderApp("/settings")
+
+  const toggle = await screen.findByRole("switch", { name: "Agent operation annotations" })
+  expect(toggle).not.toBeChecked()
+  expect(screen.getByText("Allows agents to label operations and mark them for keeping. Requires the activity journal. Restart the MCP server and reconnect the client after changing this setting. Keep markers do not prevent retention.")).toBeInTheDocument()
+  expect(puts).toHaveLength(0)
+})
+
+test("operation annotations load on and can be turned off without changing journal data", async () => {
+  const initial = response({ settings: { ...response().settings, journal: { ...response().settings.journal, operationAnnotations: true } } })
+  const puts = stubSettings(initial)
+  renderApp("/settings")
+
+  const toggle = await screen.findByRole("switch", { name: "Agent operation annotations" })
+  expect(toggle).toBeChecked()
+  await userEvent.click(toggle)
+  await waitFor(() => expect(puts).toHaveLength(1))
+  expect((puts[0].body as SettingsResponse["settings"]).journal).toEqual({ ...initial.settings.journal, operationAnnotations: false })
+})
+
+test("enabling operation annotations with the journal on saves the setting", async () => {
+  const puts = stubSettings(response())
+  renderApp("/settings")
+
+  await userEvent.click(await screen.findByRole("switch", { name: "Agent operation annotations" }))
+  await waitFor(() => expect(puts).toHaveLength(1))
+  expect((puts[0].body as SettingsResponse["settings"]).journal).toMatchObject({ enabled: true, operationAnnotations: true })
+})
+
+test("enabling operation annotations with the journal off enables both in one save", async () => {
+  const initial = response({ settings: { ...response().settings, journal: { ...response().settings.journal, enabled: false } } })
+  const puts = stubSettings(initial)
+  renderApp("/settings")
+
+  await userEvent.click(await screen.findByRole("switch", { name: "Agent operation annotations" }))
+  await waitFor(() => expect(puts).toHaveLength(1))
+  expect((puts[0].body as SettingsResponse["settings"]).journal.enabled).toBe(true)
+  expect((puts[0].body as SettingsResponse["settings"]).journal.operationAnnotations).toBe(true)
+})
+
+test("disabling the journal also disables operation annotations in one save", async () => {
+  const initial = response({ settings: { ...response().settings, journal: { ...response().settings.journal, operationAnnotations: true } } })
+  const puts = stubSettings(initial)
+  renderApp("/settings")
+
+  await userEvent.click(await screen.findByRole("switch", { name: "Journal enabled" }))
+  await waitFor(() => expect(puts).toHaveLength(1))
+  expect((puts[0].body as SettingsResponse["settings"]).journal.enabled).toBe(false)
+  expect((puts[0].body as SettingsResponse["settings"]).journal.operationAnnotations).toBe(false)
+  expect(screen.getByText("Disabling the journal also disables agent annotations.")).toBeInTheDocument()
+})
+
+test("re-enabling the journal alone leaves operation annotations off", async () => {
+  const initial = response({ settings: { ...response().settings, journal: { ...response().settings.journal, enabled: false, operationAnnotations: false } } })
+  const puts = stubSettings(initial)
+  renderApp("/settings")
+
+  await userEvent.click(await screen.findByRole("switch", { name: "Journal enabled" }))
+  await waitFor(() => expect(puts).toHaveLength(1))
+  expect((puts[0].body as SettingsResponse["settings"]).journal).toMatchObject({ enabled: true, operationAnnotations: false })
+})
+
+test("another settings card preserves enabled operation annotations", async () => {
+  const initial = response({ settings: { ...response().settings, journal: { ...response().settings.journal, operationAnnotations: true } } })
+  const puts = stubSettings(initial)
+  renderApp("/settings")
+
+  await userEvent.click(await screen.findByRole("switch", { name: "Safety notifications" }))
+  await waitFor(() => expect(puts).toHaveLength(1))
+  expect((puts[0].body as SettingsResponse["settings"]).journal.operationAnnotations).toBe(true)
+  expect((puts[0].body as SettingsResponse["settings"]).dashboard.safetyNotifications).toBe(true)
+})
+
+test("rapid operation annotation and journal changes save valid queued snapshots", async () => {
+  const initial = response({ settings: { ...response().settings, journal: { ...response().settings.journal, enabled: false } } })
+  const puts: { body: unknown; search: string }[] = []
+  let releaseFirst: (() => void) | undefined
+  stubSettingsPageFetch({
+    "/api/settings": initial,
+    "PUT /api/settings": async (_url: URL, init?: RequestInit) => {
+      const body = JSON.parse(String(init?.body))
+      puts.push({ body, search: "" })
+      if (puts.length === 1) await new Promise<void>(resolve => { releaseFirst = resolve })
+      return new Response(JSON.stringify({ ...initial, settings: body }), { status: 200 })
+    },
+  })
+  const user = userEvent.setup()
+  renderApp("/settings")
+
+  await user.click(await screen.findByRole("switch", { name: "Agent operation annotations" }))
+  await user.click(screen.getByRole("switch", { name: "Journal enabled" }))
+  await waitFor(() => expect(puts).toHaveLength(1))
+  releaseFirst?.()
+  await waitFor(() => expect(puts).toHaveLength(2))
+  expect(puts.map(put => (put.body as SettingsResponse["settings"]).journal)).toEqual([
+    { ...initial.settings.journal, enabled: true, operationAnnotations: true },
+    { ...initial.settings.journal, enabled: false, operationAnnotations: false },
+  ])
+})
+
+test("rejected operation annotation saves keep the server value and explain restart guidance", async () => {
+  stubSettingsPageFetch({
+    "/api/settings": response(),
+    "PUT /api/settings": () => new Response(JSON.stringify({ error: "Settings write unavailable" }), { status: 503 }),
+  })
+  renderApp("/settings")
+
+  const toggle = await screen.findByRole("switch", { name: "Agent operation annotations" })
+  await userEvent.click(toggle)
+  expect(await screen.findByRole("alert")).toHaveTextContent(/Not saved:/)
+  expect(toggle).not.toBeChecked()
+  expect(screen.getByText(/Restart the MCP server and reconnect the client/)).toBeInTheDocument()
+})
+
 test("Toggle_saves_immediately", async () => {
   const puts = stubSettings(response())
   renderApp("/settings")
 
   await userEvent.click(await screen.findByRole("switch", { name: "Start with mcp serve" }))
   await waitFor(() => expect(puts).toHaveLength(1))
   expect((puts[0].body as Put).dashboard.autoStart).toBe(true)
   expect((puts[0].body as { mutations: unknown }).mutations).toEqual(response().settings.mutations)
   expect(await screen.findByText("Saved")).toBeInTheDocument()
   expect(screen.queryByRole("button", { name: "Save" })).not.toBeInTheDocument()
diff --git a/src/SqlHarness.Dashboard/ui/src/pages/SettingsPage.tsx b/src/SqlHarness.Dashboard/ui/src/pages/SettingsPage.tsx
index 943dd75..d577c46 100644
--- a/src/SqlHarness.Dashboard/ui/src/pages/SettingsPage.tsx
+++ b/src/SqlHarness.Dashboard/ui/src/pages/SettingsPage.tsx
@@ -299,27 +299,33 @@ export function SettingsPage() {
             error={fieldErrors["execution.defaultTimeoutSeconds"]} onCommit={value => setExecution({ defaultTimeoutSeconds: value })} />
           <StatusLine status={status.execution} />
         </CardContent>
       </Card>
       <Card>
         <CardHeader>
           <CardTitle>Activity journal</CardTitle>
           <CardDescription>What SQLHarness records about each operation.</CardDescription>
         </CardHeader>
         <CardContent className="grid gap-4">
-          <Toggle id="journal-enabled" label="Journal enabled" checked={journal.enabled} onChange={value => setJournal({ enabled: value })} />
+          <Toggle id="journal-enabled" label="Journal enabled" checked={journal.enabled} onChange={value => setJournal(value ? { enabled: true } : { enabled: false, operationAnnotations: false })} />
           <Toggle
             id="journal-sensitive"
             label="Store sensitive data"
             checked={journal.storeSensitive}
             onChange={value => (value ? setConfirmingSensitive(true) : setJournal({ storeSensitive: false }))}
           />
+          <div className="grid gap-1">
+            <Toggle id="journal-operation-annotations" label="Agent operation annotations" checked={journal.operationAnnotations}
+              onChange={value => setJournal(value ? { enabled: true, operationAnnotations: true } : { operationAnnotations: false })} />
+            <p className="text-sm text-muted-foreground">Allows agents to label operations and mark them for keeping. Requires the activity journal. Restart the MCP server and reconnect the client after changing this setting. Keep markers do not prevent retention.</p>
+            <p className="text-sm text-muted-foreground">Disabling the journal also disables agent annotations.</p>
+          </div>
           <Toggle id="retention-enabled" label="Retention enabled" checked={journal.retention.enabled} onChange={value => setRetention({ enabled: value })} />
           <NumberField id="retention-age" label="Maximum age (days)" value={journal.retention.maxAgeDays} min={1} max={3650} error={fieldErrors["journal.retention.maxAgeDays"]} onCommit={value => setRetention({ maxAgeDays: value })} />
           <NumberField id="retention-size" label="Maximum size (MB)" value={journal.retention.maxSizeMb} min={10} max={102400} error={fieldErrors["journal.retention.maxSizeMb"]} onCommit={value => setRetention({ maxSizeMb: value })} />
           <StatusLine status={status.journal} />
         </CardContent>
       </Card>
       <Card>
         <CardHeader>
           <CardTitle>Dashboard</CardTitle>
         </CardHeader>
diff --git a/tests/SqlHarness.Tests/Dashboard/DashboardSettingsTests.cs b/tests/SqlHarness.Tests/Dashboard/DashboardSettingsTests.cs
index 803a8e7..645eebc 100644
--- a/tests/SqlHarness.Tests/Dashboard/DashboardSettingsTests.cs
+++ b/tests/SqlHarness.Tests/Dashboard/DashboardSettingsTests.cs
@@ -37,20 +37,21 @@ public sealed class DashboardSettingsTests
     {
         using var home = new TempHome();
         await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
         var (_, client) = dashboard;
 
         var body = await Json(await client.GetAsync("/api/settings"));
 
         Assert.Equal("missing", body.GetProperty("status").GetString());
         Assert.Equal(ConfigPath(home), body.GetProperty("path").GetString());
         Assert.False(body.GetProperty("settings").GetProperty("journal").GetProperty("storeSensitive").GetBoolean());
+        Assert.False(body.GetProperty("settings").GetProperty("journal").GetProperty("operationAnnotations").GetBoolean());
         Assert.Equal(47800, body.GetProperty("settings").GetProperty("dashboard").GetProperty("port").GetInt32());
         Assert.False(body.GetProperty("settings").GetProperty("diagnostics").GetProperty("enabled").GetBoolean());
         Assert.False(body.GetProperty("settings").GetProperty("dashboard").GetProperty("safetyNotifications").GetBoolean());
     }
 
     [Fact]
     public async Task Get_reports_invalid_file_without_its_contents()
     {
         using var home = new TempHome();
         File.WriteAllText(ConfigPath(home), "{ \"SQLH_SECRET_MARKER\": ");
@@ -95,20 +96,57 @@ public sealed class DashboardSettingsTests
         var body = ValidBody.Replace("\"diagnostics\": { \"enabled\": true }", "\"diagnostics\": { \"enabled\": true }, \"execution\": { \"defaultTimeoutSeconds\": 75 }");
 
         var response = await client.SendAsync(Put(server, body));
 
         Assert.Equal(HttpStatusCode.OK, response.StatusCode);
         Assert.Equal(75, SqlHarnessConfigLoader.Load(ConfigPath(home)).Config.Execution.DefaultTimeoutSeconds);
         Assert.Equal(75, (await Json(await client.GetAsync("/api/settings"))).GetProperty("settings")
             .GetProperty("execution").GetProperty("defaultTimeoutSeconds").GetInt32());
     }
 
+    [Theory]
+    [InlineData(false)]
+    [InlineData(true)]
+    public async Task Put_persists_operation_annotations_and_get_reads_it_back(bool enabled)
+    {
+        using var home = new TempHome();
+        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
+        var (server, client) = dashboard;
+        var body = ValidBody.Replace("\"enabled\": true, \"storeSensitive\": true", $"\"enabled\": true, \"storeSensitive\": true, \"operationAnnotations\": {enabled.ToString().ToLowerInvariant()}");
+
+        var response = await client.SendAsync(Put(server, body));
+
+        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
+        var settings = (await Json(await client.GetAsync("/api/settings"))).GetProperty("settings");
+        Assert.Equal(enabled, settings.GetProperty("journal").GetProperty("operationAnnotations").GetBoolean());
+        Assert.True(settings.GetProperty("dashboard").GetProperty("autoStart").GetBoolean());
+        Assert.Equal("operator", settings.GetProperty("mutations").GetProperty("defaultMode").GetString());
+    }
+
+    [Fact]
+    public async Task Put_with_operation_annotations_without_journal_preserves_the_existing_file()
+    {
+        using var home = new TempHome();
+        const string original = """{ "journal": { "enabled": true, "operationAnnotations": false } }""";
+        File.WriteAllText(ConfigPath(home), original);
+        await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
+        var (server, client) = dashboard;
+        var invalid = ValidBody.Replace("\"enabled\": true, \"storeSensitive\": true", "\"enabled\": false, \"storeSensitive\": true, \"operationAnnotations\": true");
+
+        var response = await client.SendAsync(Put(server, invalid));
+
+        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
+        var error = Assert.Single((await Json(response)).GetProperty("errors").EnumerateArray());
+        Assert.Equal("journal.operationAnnotations", error.GetProperty("field").GetString());
+        Assert.Equal(original, File.ReadAllText(ConfigPath(home)));
+    }
+
     [Theory]
     [InlineData(0)]
     [InlineData(301)]
     public async Task Put_rejects_execution_timeout_outside_bounds(int timeoutSeconds)
     {
         using var home = new TempHome();
         await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
         var (server, client) = dashboard;
         var body = ValidBody.Replace("\"diagnostics\": { \"enabled\": true }", $"\"diagnostics\": {{ \"enabled\": true }}, \"execution\": {{ \"defaultTimeoutSeconds\": {timeoutSeconds} }}");
 
@@ -252,11 +290,11 @@ public sealed class DashboardSettingsTests
     public async Task Put_without_origin_header_is_allowed()
     {
         using var home = new TempHome();
         await using var dashboard = await DashboardServerTests.StartAuthenticated(home);
         var (server, client) = dashboard;
         var request = Put(server, ValidBody);
         request.Headers.Remove("Origin");
 
         Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(request)).StatusCode);
     }
-}
\ No newline at end of file
+}
