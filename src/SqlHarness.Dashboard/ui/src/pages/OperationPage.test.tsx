import { screen, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test } from "vitest"
import { agentCall, operation, operationDetail, session, variant } from "@/test/fixtures"
import { renderApp, stubFetch } from "@/test/render"

const defaultOptimization = {
  a: "v0", b: "v1", basis: "MeasuredTogether", operationId: 10, equivalenceLabel: "results equivalent (direct)",
  cases: [{ id: "0", label: "Case 1 of 1", outcome: "same" }], selectedCase: "all", aggregate: "sum", coverageLabel: "1 of 1 cases",
  mixed: null, tables: [], collapsed: null, elapsed: { beforeMs: 12, afterMs: 9, policyVerdict: null, differentRun: false },
  beforeAction: { operationId: 10, side: "baseline", caseIds: ["0"] }, afterAction: { operationId: 10, side: "candidate", caseIds: ["0"] },
}
const stubOperationFetch = (routes: Record<string, unknown | ((url: URL, init?: RequestInit) => Response)>) => stubFetch({
  "/api/operations/10/repro": { text: null, completeness: "Unavailable", label: "Script not available — enable storeSensitive" },
  "/api/operations/10/optimization?case=all&aggregate=sum": defaultOptimization,
  "/api/operations/10/optimization": defaultOptimization,
  ...routes,
})

test("compare operation returns to its session with the originating subject and scope", async () => {
  stubOperationFetch({ "/api/operations/10": operationDetail({ operation: operation({ id: 10, sessionId: 7, operation: "compare" }), session: session({ id: 7, sessionKey: "mcp:session" }) }) })
  renderApp("/operations/10?returnSession=7&returnSubject=orders&returnScope=history")

  const back = await screen.findByRole("link", { name: "Back to mcp:session" })
  const target = new URL(back.getAttribute("href")!, "http://dashboard")
  expect(target.pathname).toBe("/sessions/7")
  expect(target.searchParams.get("subject")).toBe("orders")
  expect(target.searchParams.get("scope")).toBe("history")
})

test("aggregate measure from a different MCP child returns to the typed agent session", async () => {
  stubOperationFetch({ "/api/operations/22": operationDetail({ operation: operation({ id: 22, sessionId: 8, operation: "measure" }), session: session({ id: 8, sessionKey: "mcp:second-child" }) }) })
  renderApp("/operations/22?returnSession=7&returnAgentKind=claude&returnAgentId=conversation-1&returnSubject=orders&returnScope=whole&returnA=v1&returnB=v2")

  const back = await screen.findByRole("link", { name: "Back to agent session" })
  const target = new URL(back.getAttribute("href")!, "http://dashboard")
  expect(target.pathname).toBe("/agent-sessions/claude/conversation-1")
  expect(Object.fromEntries(target.searchParams)).toMatchObject({ subject: "orders", scope: "whole", a: "v1", b: "v2" })
})

test("compare operation restores every optimization search selection on return", async () => {
  stubOperationFetch({ "/api/operations/10": operationDetail({ operation: operation({ id: 10, sessionId: 7, operation: "compare" }), session: session({ id: 7, sessionKey: "mcp:session" }) }) })
  renderApp("/operations/10?returnSession=7&returnSubject=orders&returnDb=shop&returnGroup=fp-2&returnScope=fragment%3Acustomer&returnA=v1&returnB=v2&returnCase=customer-9001&returnAgg=mean")
  const back = await screen.findByRole("link", { name: "Back to mcp:session" })
  const target = new URL(back.getAttribute("href")!, "http://dashboard")
  expect(Object.fromEntries(target.searchParams)).toMatchObject({ subject: "orders", db: "shop", group: "fp-2", scope: "fragment:customer", a: "v1", b: "v2", case: "customer-9001", agg: "mean" })
})

test("compare operation returns to the unassigned group without inventing subject or scope", async () => {
  stubOperationFetch({ "/api/operations/10": operationDetail({ operation: operation({ id: 10, sessionId: 7, operation: "compare" }), session: session({ id: 7, sessionKey: "mcp:session" }) }) })
  renderApp("/operations/10?returnSession=7&returnUnassigned=true")

  const back = await screen.findByRole("link", { name: "Back to mcp:session" })
  const target = new URL(back.getAttribute("href")!, "http://dashboard")
  expect(target.pathname).toBe("/sessions/7")
  expect(target.searchParams.get("unassigned")).toBe("true")
  expect(target.searchParams.has("subject")).toBe(false)
  expect(target.searchParams.has("scope")).toBe(false)
})

test("compare opens on the Optimization panel and carries selected case and aggregate in its URL", async () => {
  const comparison = { ...defaultOptimization, b: "v3", selectedCase: "0" }
  const calls = stubOperationFetch({
    "/api/operations/10": operationDetail({ operation: operation({ id: 10, sessionId: 7, operation: "compare", database: "shop" }), session: session({ id: 7, title: "Tuning orders" }), compareSubject: "orders", compareScope: "whole", baselineVersion: "v0", candidateVersion: "v3", vars: { tenant: "acme" }, phases: [{ phase: "execute", count: 1, totalMs: 12, maxMs: 12 }], variants: [
      variant({ ordinal: 0, variant: "baseline", caseId: "0", matrixCell: 0 }), variant({ ordinal: 1, variant: "candidate", caseId: "0", matrixCell: 0 }),
      variant({ ordinal: 2, variant: "baseline", caseId: "1", caseLabel: "@tenant = N'other'", matrixCell: 1 }), variant({ ordinal: 3, variant: "candidate", caseId: "1", caseLabel: "@tenant = N'other'", matrixCell: 1 }),
    ], compareCases: [{ index: 0 }, { index: 1 }] }),
    "/api/operations/10/optimization?case=0&aggregate=sum": comparison,
    "/api/operations/10/optimization?case=1&aggregate=sum": { ...comparison, selectedCase: "1" },
  })
  const { router, container } = renderApp("/operations/10?returnSession=7&returnSubject=orders&returnDb=shop&returnGroup=fp&returnScope=whole&returnA=v0&returnB=v3&returnCase=0&returnAgg=sum&case=0&agg=sum")
  expect(router.state.location.search).toMatchObject({ case: "0", agg: "sum" })

  expect(await screen.findByRole("heading", { level: 1 })).toHaveTextContent("orders")
  expect(screen.getByText("whole query")).toBeInTheDocument()
  expect(screen.getByText("v0 → v3")).toBeInTheDocument()
  expect(screen.getByText("shop")).toBeInTheDocument()
  expect(await screen.findByRole("region", { name: "Version comparison" })).toBeInTheDocument()
  expect(screen.getByText("SQL diff & conditions").closest("details")).not.toHaveAttribute("open")
  expect(screen.getAllByText("Plans").find(element => element.tagName.toLowerCase() === "summary")).not.toHaveAttribute("open")
  expect(screen.getByText("Per-case & per-run details").closest("details")).not.toHaveAttribute("open")
  const diagnostics = screen.getByText("Run diagnostics").closest("details")!
  expect(diagnostics).not.toHaveAttribute("open")
  expect(screen.getByRole("region", { name: "Time breakdown" }).closest("details")).toBe(diagnostics)
  expect(container.querySelectorAll('[aria-label="Variables"] > *')).toHaveLength(1)
  const requestedCase = (value: string) => calls.some(call => {
    const url = new URL(call, "http://dashboard")
    return url.pathname === "/api/operations/10/optimization" && url.searchParams.get("case") === value && url.searchParams.get("aggregate") === "sum"
  })
  expect(calls.filter(call => call.includes("optimization"))).toEqual(["/api/operations/10/optimization?case=0&aggregate=sum&view=changed"])
  await userEvent.selectOptions(screen.getByRole("combobox", { name: "Case" }), "1")
  await screen.findByText("@tenant = N'other'")
  expect(requestedCase("1")).toBe(true)
  expect(router.state.location.search).toMatchObject({ returnSubject: "orders", returnAgg: "sum", case: "1", agg: "sum" })
})

test("measure shows facts, KPIs, table IO, waits and the sql-not-stored note", async () => {
  stubOperationFetch({ "/api/operations/10": operationDetail() })
  renderApp("/operations/10")

  expect(await screen.findByRole("heading", { level: 1, name: "Operation #10" })).toBeInTheDocument()
  expect(screen.getByText("acme")).toBeInTheDocument()
  expect(screen.getByText("Output bytes (raw / emitted)")).toBeInTheDocument()
  expect(screen.getByText("Estimated output tokens (raw / emitted)")).toBeInTheDocument()
  expect(screen.getByText("Token estimation method")).toBeInTheDocument()
  expect(screen.getByText("Run diagnostics").closest("details")).not.toHaveAttribute("open")
  const kpis = screen.getByRole("region", { name: "Key metrics" })
  expect(kpis.compareDocumentPosition(screen.getByText("Run diagnostics")) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
  // Elapsed median (with its 10–14 range) and compile time are both 12 ms in the fixture.
  expect(within(kpis).getAllByText("12 ms")).toHaveLength(2)
  expect(within(kpis).getByText("10–14")).toBeInTheDocument()
  expect(within(kpis).getByText("512 KB / 8.0 MB")).toBeInTheDocument()
  expect(screen.getByRole("cell", { name: "Orders" })).toBeInTheDocument()
  expect(screen.getByRole("cell", { name: "PAGEIOLATCH_SH" })).toBeInTheDocument()
  expect(screen.getByText("SQL text is not stored (journal.storeSensitive is off).")).toBeInTheDocument()
  expect(screen.queryByRole("link", { name: "Download plan" })).not.toBeInTheDocument()
})

test("operation details show phase timing shares and expandable connection diagnostics", async () => {
  stubOperationFetch({ "/api/operations/10": operationDetail({
    operation: operation({ durationMs: 100 }),
    phases: [{ phase: "execute", count: 2, totalMs: 30, maxMs: 20 }],
    events: [
      { offsetMs: 12, phase: "connect", durationMs: 8, outcome: "failed", errorNumber: 18456, clientConnectionId: "550e8400-e29b-41d4-a716-446655440000", droppedCount: null },
      { offsetMs: 20, phase: "other", durationMs: 0, outcome: "truncated", errorNumber: null, clientConnectionId: null, droppedCount: 3 },
    ],
  }) })
  renderApp("/operations/10")

  const timing = await screen.findByRole("region", { name: "Time breakdown" })
  const diagnostics = timing.closest("details")!
  expect(diagnostics).not.toHaveAttribute("open")
  await userEvent.click(within(diagnostics).getByText("Run diagnostics"))
  expect(within(timing).getByRole("columnheader", { name: "Phase" })).toBeInTheDocument()
  expect(within(timing).getByRole("cell", { name: "execute" })).toBeInTheDocument()
  expect(within(timing).getByRole("cell", { name: "30 ms" })).toBeInTheDocument()
  expect(within(timing).getByRole("cell", { name: "30%" })).toBeInTheDocument()

  const events = screen.getByText("Connection events").closest("details")!
  expect(events).not.toHaveAttribute("open")
  await userEvent.click(screen.getByText("Connection events"))
  expect(await screen.findByText("550e8400-e29b-41d4-a716-446655440000")).toBeInTheDocument()
  expect(screen.getByText("Error number: 18456")).toBeInTheDocument()
  expect(screen.getByText("Offset 12 ms")).toBeInTheDocument()
  expect(screen.getByText("Duration 8 ms")).toBeInTheDocument()
  expect(screen.getByText("Outcome: failed")).toBeInTheDocument()
  expect(screen.getByText("3 diagnostic events omitted")).toBeInTheDocument()
})

test("legacy operation details show an empty time breakdown without zero-valued phases", async () => {
  stubOperationFetch({ "/api/operations/10": operationDetail({
    operation: operation({ durationMs: 100 }),
    phases: [],
  }) })
  renderApp("/operations/10")

  const timing = await screen.findByRole("region", { name: "Time breakdown" })
  await userEvent.click(within(timing.closest("details")!).getByText("Run diagnostics"))
  expect(within(timing).getByRole("columnheader", { name: "Phase" })).toBeInTheDocument()
  expect(within(timing).getByText("No phase timing data was recorded for this operation.")).toBeInTheDocument()
  expect(within(timing).queryByRole("cell", { name: "0 ms" })).not.toBeInTheDocument()
})

test("compare shows baseline versus candidate and stored plans with operators", async () => {
  const hash = "B".repeat(64)
  stubOperationFetch({
    "/api/operations/10": operationDetail({
      operation: operation({ operation: "compare" }),
      baselineVersion: "v0", candidateVersion: "v1",
      sqlText: "SELECT 1",
      candidateSqlText: "SELECT 2",
      summary: { kind: "compare", comparison: "Ordered", resultsEquivalent: true, baselineOperators: [], candidateOperators: [] },
      variants: [
        variant({ ordinal: 0, variant: "baseline", plans: [{ repetition: 1, ordinal: 0, hash, stored: true }] }),
        variant({ ordinal: 1, variant: "candidate", elapsedMs: { min: 1, median: 6, max: 9 } }),
      ],
    }),
    // The server writes the statement text as "sql" and omits empty missing-index lists.
    "/api/operations/10/plans": { operationId: 10, state: "plans not stored", occurrences: [] },
  })
  renderApp("/operations/10")

  await screen.findByRole("region", { name: "Compare detail" })
  expect(screen.getByText("v0 → v1")).toBeInTheDocument()
  await userEvent.click(screen.getByText("SQL diff & conditions"))
  expect(screen.getByRole("button", { name: "Copy diff" })).toBeInTheDocument()
  await userEvent.click(screen.getAllByText("Plans").find(element => element.tagName.toLowerCase() === "summary")!)
  expect(within(screen.getByRole("region", { name: "Selected variant plan" })).getAllByRole("region", { name: "Key metrics" })).toHaveLength(2)
})

test("Operation_without_metrics_or_target_renders", async () => {
  stubOperationFetch({
    "/api/operations/10": operationDetail({
      operation: operation({ operation: "gain", profile: null, engine: null, server: null, database: null, sqlHash: null, durationMs: null, exitCode: null }),
      session: session(), vars: null, rawTokens: null, emittedTokens: null, artifactDirectory: null, summary: null, variants: [],
    }),
  })
  renderApp("/operations/10")
  expect(await screen.findByRole("heading", { level: 1, name: "Operation #10" })).toBeInTheDocument()
  expect(screen.queryByRole("region", { name: "Key metrics" })).not.toBeInTheDocument()
})

test("failed operation with a variant that has no metrics renders", async () => {
  stubOperationFetch({
    "/api/operations/10": operationDetail({
      operation: operation({ operation: "measure", status: "failed", exitCode: 5, errorKind: "SqlExecution", finishedAt: null, rowsReturned: null }),
      summary: null,
      variants: [variant({
        elapsedMs: null, cpuMs: null, logicalReads: null, grantRequestedKb: null, grantGrantedKb: null, grantMaxUsedKb: null,
        dop: null, compileTimeMs: null, compileCpuMs: null, spillCount: 0, hasWarnings: false, hasImplicitConversion: false,
        missingIndexCount: 0, waits: null, postgres: null, tableIo: [], plans: [],
      })],
    }),
  })
  renderApp("/operations/10")
  expect(await screen.findByRole("heading", { level: 1, name: "Operation #10" })).toBeInTheDocument()
  expect(screen.getByText("5 (SqlExecution)")).toBeInTheDocument()
  expect(within(screen.getByRole("region", { name: "Key metrics" })).getByText("— / —")).toBeInTheDocument()
})

test("a non-numeric operation id shows not found without fetching operation data", async () => {
  const calls = stubFetch({})
  renderApp("/operations/abc")
  expect(await screen.findByText("Not found")).toBeInTheDocument()
  expect(calls).toContain("/api/profiles")
  expect(calls).toContain("/api/settings")
  expect(calls).not.toContain("/api/operations/abc")
})

test("measure-sets summary shows set stability", async () => {
  stubOperationFetch({ "/api/operations/10": operationDetail({ summary: { kind: "measure-sets", sets: 3, stableSets: 2 } }) })
  renderApp("/operations/10")
  expect(await screen.findByText("2/3 sets stable")).toBeInTheDocument()
  expect(screen.getByText("unstable results")).toBeInTheDocument()
})

test("stable measure-sets summary has no unstable badge", async () => {
  stubOperationFetch({ "/api/operations/10": operationDetail({ summary: { kind: "measure-sets", sets: 2, stableSets: 2 } }) })
  renderApp("/operations/10")
  expect(await screen.findByText("2/2 sets stable")).toBeInTheDocument()
  expect(screen.queryByText("unstable results")).not.toBeInTheDocument()
})

test("compare-matrix summary shows the matrix parameter and equivalent cells", async () => {
  stubOperationFetch({
    "/api/operations/10": operationDetail({
      summary: { kind: "compare-matrix", parameterName: "BatchSize", parameterType: "int", cells: 3, equivalentCells: 2 },
    }),
  })
  renderApp("/operations/10")
  expect(await screen.findByText("parameter: BatchSize (int)")).toBeInTheDocument()
  expect(screen.getByText("2/3 cells equivalent")).toBeInTheDocument()
})

test("agent call card shows the client-supplied labels and is absent without them", async () => {
  stubOperationFetch({
    "/api/operations/10": operationDetail({
      agent: agentCall({ callId: "exec-1", model: "gpt-6-sol", reasoningEffort: "low", sessionId: "s", turnId: "turn-1", turnTrigger: "exec", threadSource: "user" }),
    }),
  })
  renderApp("/operations/10")

  await userEvent.click(await screen.findByText("Run diagnostics"))
  expect(await screen.findByRole("heading", { name: "Agent call" })).toBeInTheDocument()
  expect(screen.getByText("gpt-6-sol")).toBeInTheDocument()
  expect(screen.getByText("low")).toBeInTheDocument()
  expect(screen.getByText("turn-1")).toBeInTheDocument()
  expect(screen.getByText("exec-1")).toBeInTheDocument()
})

test("shows transcript model and effort when the call has no client labels", async () => {
  stubOperationFetch({ "/api/operations/10": operationDetail({
    agent: agentCall({ transcriptModel: "claude-opus-5-5", transcriptReasoningEffort: "high", conversationTitle: "Optimize orders" }),
  }) })
  renderApp("/operations/10")
  await userEvent.click(await screen.findByText("Run diagnostics"))
  expect(await screen.findByText("claude-opus-5-5")).toBeInTheDocument()
  expect(screen.getAllByText("from transcript")).toHaveLength(2)
  expect(screen.getByText("high")).toBeInTheDocument()
  expect(screen.getByText("Optimize orders")).toBeInTheDocument()
})

test("operations without call labels render no agent call card", async () => {
  stubOperationFetch({ "/api/operations/10": operationDetail({ agent: null }) })
  renderApp("/operations/10")

  expect(await screen.findByRole("heading", { level: 1, name: "Operation #10" })).toBeInTheDocument()
  expect(screen.queryByRole("heading", { name: "Agent call" })).not.toBeInTheDocument()
})

test("measure operation shows recorded setup SQL before the measured query", async () => {
  const setup = "CREATE TABLE #data (id int);\nINSERT INTO #data VALUES (1);"
  stubOperationFetch({ "/api/operations/10": operationDetail({
    operation: operation({ operation: "measure" }),
    sqlText: "SELECT id FROM #data",
    setupSqlText: setup,
  }) })
  renderApp("/operations/10")
  const section = await screen.findByRole("region", { name: "Setup SQL" })
  expect(within(section).getByTestId("sql-code").textContent).toBe(setup)
  expect(within(section).getByRole("button", { name: "Show formatted" })).toBeInTheDocument()
  expect(within(section).getByRole("button", { name: "Copy" })).toBeInTheDocument()
  expect(within(section).getByText(/same database session/)).toBeInTheDocument()
  expect(screen.getAllByTestId("sql-code")).toHaveLength(2)
})

test("measure attribution shows single-run after values, hidden work, plan access, and PostgreSQL availability", async () => {
  stubOperationFetch({ "/api/operations/10": operationDetail({
    operation: operation({ operation: "measure", engine: "PostgreSql" }),
    statements: [{ key: "q", occurrence: 0, text: "SELECT 1", status: "measured",
      readsBefore: null, readsAfter: 42, scansBefore: null, scansAfter: 2, executions: 3, change: "", attribution: "confirmed",
      hiddenWork: [{ kind: "multi-statement-tvf", name: "dbo.Expand", executions: 7, sharePercent: null }],
      objects: [{ table: "dbo.Orders", scansBefore: null, scansAfter: 2, readsBefore: null, readsAfter: 42, change: "" }],
      planHashBefore: null, planHashAfter: "shape-only", planLinkBefore: null,
      planLinkAfter: "/operations/10?planCase=s:0&planRepetition=2&planDocument=0&planStatement=0", udfElapsedMilliseconds: null }],
  }) })
  renderApp("/operations/10")

  const rows = await screen.findByRole("region", { name: "Statement IO" })
  expect(within(rows).getAllByText("42")).toHaveLength(2)
  expect(within(rows).queryByText("before", { exact: true })).not.toBeInTheDocument()
  expect(within(rows).getByText("multi-statement function: internal reads not visible (7 executions)")).toBeInTheDocument()
  expect(within(rows).getByRole("link", { name: "Plan after" })).toHaveAttribute("href", expect.stringContaining("planStatement=0"))
  expect(within(rows).getByText("per-statement IO not available for this engine")).toBeInTheDocument()
})

test.each([
  { parameterSet: "", query: "planParameterSet=", expectedTab: "1" },
  { parameterSet: null, query: "planParameterSetNull=true", expectedTab: "0" },
])("plan deep link selects the exact parameter set ($query)", async ({ parameterSet, query, expectedTab }) => {
  const hash = "A".repeat(64)
  const calls = stubOperationFetch({
    "/api/operations/10": operationDetail({
      operation: operation({ id: 10, operation: "measure" }),
      variants: [variant({ ordinal: 0, variant: "measure", parameterSet: null, caseId: "0" }),
        variant({ ordinal: 1, variant: "measure", parameterSet: "", caseId: "0" })],
    }),
    "/api/operations/10/plans": { operationId: 10, state: "stored", occurrences: [{ variant: "measure", parameterSet,
      caseId: "0", caseLabel: "Case 1 of 1", repetition: 1, documentOrdinal: 0, statementIndex: 0,
      spanStart: null, spanLength: null, statementId: null, statementCompId: null, documentSha256: hash,
      shapeHash: "shape", format: "showplan-xml", matchKind: "exact", omitted: false, stored: true }] },
    "/api/operations/10/source-map?variant=measure&parameterSet=&repetition=1&caseId=0": {
      operationId: 10, state: "stored", engine: "sqlserver", variant: "measure", parameterSet: "", caseId: "0",
      caseLabel: "Case 1 of 1", repetition: 1, executedText: "SELECT 1", algorithmVersion: 1, links: [], omittedDocuments: [], errors: [],
    },
    "/api/operations/10/source-map?variant=measure&repetition=1&caseId=0": {
      operationId: 10, state: "stored", engine: "sqlserver", variant: "measure", parameterSet: null, caseId: "0",
      caseLabel: "Case 1 of 1", repetition: 1, executedText: "SELECT 1", algorithmVersion: 1, links: [], omittedDocuments: [], errors: [],
    },
  })
  const { router } = renderApp(`/operations/10?planCase=s%3A0&planVariant=measure&${query}&planRepetition=1&planDocument=0&planStatement=0`)
  expect(router.state.location.search).toMatchObject(parameterSet === ""
    ? { planParameterSet: "", planCase: "0", planVariant: "measure" }
    : { planParameterSetNull: true, planCase: "0", planVariant: "measure" })

  const tabs = await screen.findAllByRole("tab", { name: "measure" })
  expect(tabs.findIndex(tab => tab.getAttribute("data-active") !== null)).toBe(Number(expectedTab))
  await screen.findByRole("region", { name: "Plans" })
  expect(calls).toContain(parameterSet === ""
    ? "/api/operations/10/source-map?variant=measure&parameterSet=&repetition=1&caseId=0"
    : "/api/operations/10/source-map?variant=measure&repetition=1&caseId=0")
})
