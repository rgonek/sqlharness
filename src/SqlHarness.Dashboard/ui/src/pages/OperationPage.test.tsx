import { screen, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test } from "vitest"
import { agentCall, operation, operationDetail, session, variant } from "@/test/fixtures"
import { renderApp, stubFetch } from "@/test/render"

test("compare operation returns to its session with the originating subject and scope", async () => {
  stubFetch({ "/api/operations/10": operationDetail({ operation: operation({ id: 10, sessionId: 7, operation: "compare" }), session: session({ id: 7, sessionKey: "mcp:session" }) }) })
  renderApp("/operations/10?returnSession=7&returnSubject=orders&returnScope=history")

  const back = await screen.findByRole("link", { name: "mcp:session" })
  const target = new URL(back.getAttribute("href")!, "http://dashboard")
  expect(target.pathname).toBe("/sessions/7")
  expect(target.searchParams.get("subject")).toBe("orders")
  expect(target.searchParams.get("scope")).toBe("history")
})

test("compare operation returns to the unassigned group without inventing subject or scope", async () => {
  stubFetch({ "/api/operations/10": operationDetail({ operation: operation({ id: 10, sessionId: 7, operation: "compare" }), session: session({ id: 7, sessionKey: "mcp:session" }) }) })
  renderApp("/operations/10?returnSession=7&returnUnassigned=true")

  const back = await screen.findByRole("link", { name: "mcp:session" })
  const target = new URL(back.getAttribute("href")!, "http://dashboard")
  expect(target.pathname).toBe("/sessions/7")
  expect(target.searchParams.get("unassigned")).toBe("true")
  expect(target.searchParams.has("subject")).toBe(false)
  expect(target.searchParams.has("scope")).toBe(false)
})

test("measure shows facts, KPIs, table IO, waits and the sql-not-stored note", async () => {
  stubFetch({ "/api/operations/10": operationDetail() })
  renderApp("/operations/10")

  expect(await screen.findByRole("heading", { level: 1, name: "Operation #10" })).toBeInTheDocument()
  expect(screen.getByText("acme")).toBeInTheDocument()
  expect(screen.getByText("Output bytes (raw / emitted)")).toBeInTheDocument()
  expect(screen.getByText("Estimated output tokens (raw / emitted)")).toBeInTheDocument()
  expect(screen.getByText("Token estimation method")).toBeInTheDocument()
  const kpis = screen.getByRole("region", { name: "Key metrics" })
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
  stubFetch({ "/api/operations/10": operationDetail({
    operation: operation({ durationMs: 100 }),
    phases: [{ phase: "execute", count: 2, totalMs: 30, maxMs: 20 }],
    events: [
      { offsetMs: 12, phase: "connect", durationMs: 8, outcome: "failed", errorNumber: 18456, clientConnectionId: "550e8400-e29b-41d4-a716-446655440000", droppedCount: null },
      { offsetMs: 20, phase: "other", durationMs: 0, outcome: "truncated", errorNumber: null, clientConnectionId: null, droppedCount: 3 },
    ],
  }) })
  renderApp("/operations/10")

  const timing = await screen.findByRole("region", { name: "Time breakdown" })
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
  stubFetch({ "/api/operations/10": operationDetail({
    operation: operation({ durationMs: 100 }),
    phases: [],
  }) })
  renderApp("/operations/10")

  const timing = await screen.findByRole("region", { name: "Time breakdown" })
  expect(within(timing).getByRole("columnheader", { name: "Phase" })).toBeInTheDocument()
  expect(within(timing).getByText("No phase timing data was recorded for this operation.")).toBeInTheDocument()
  expect(within(timing).queryByRole("cell", { name: "0 ms" })).not.toBeInTheDocument()
})

test("compare shows baseline versus candidate and stored plans with operators", async () => {
  const hash = "B".repeat(64)
  stubFetch({
    "/api/operations/10": operationDetail({
      operation: operation({ operation: "compare" }),
      sqlText: "SELECT 1",
      candidateSqlText: "SELECT 2",
      summary: { kind: "compare", comparison: "Ordered", resultsEquivalent: true, baselineOperators: [], candidateOperators: [] },
      variants: [
        variant({ ordinal: 0, variant: "baseline", plans: [{ repetition: 1, ordinal: 0, hash, stored: true }] }),
        variant({ ordinal: 1, variant: "candidate", elapsedMs: { min: 1, median: 6, max: 9 } }),
      ],
    }),
    // The server writes the statement text as "sql" and omits empty missing-index lists.
    [`/api/plans/${hash}?view=distilled`]: { statements: [{ sql: "SELECT 1 /* plan */", root: { physicalOp: "Clustered Index Scan", objectName: "Orders" } }] },
  })
  renderApp("/operations/10")

  const comparison = await screen.findByRole("region", { name: "Compare detail" })
  expect(within(comparison).getByText("-50%")).toBeInTheDocument()
  expect(screen.getAllByText("Results equivalent").length).toBeGreaterThan(0)
  await userEvent.click(screen.getByRole("tab", { name: "SQL changes" }))
  expect(screen.getByRole("button", { name: "Show full SQL versions" })).toBeInTheDocument()
  await userEvent.click(screen.getByRole("tab", { name: "Plans and IO" }))
  expect(screen.getByRole("link", { name: "Download plan" })).toHaveAttribute("href", `/api/plans/${hash}`)

  await userEvent.click(screen.getByRole("button", { name: "Show operators" }))
  expect(await screen.findByText("Clustered Index Scan")).toBeInTheDocument()
})

test("Operation_without_metrics_or_target_renders", async () => {
  stubFetch({
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
  stubFetch({
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

test("a non-numeric operation id shows not found without calling the api", async () => {
  const calls = stubFetch({})
  renderApp("/operations/abc")
  expect(await screen.findByText("Not found")).toBeInTheDocument()
  expect(calls).toEqual([])
})

test("measure-sets summary shows set stability", async () => {
  stubFetch({ "/api/operations/10": operationDetail({ summary: { kind: "measure-sets", sets: 3, stableSets: 2 } }) })
  renderApp("/operations/10")
  expect(await screen.findByText("2/3 sets stable")).toBeInTheDocument()
  expect(screen.getByText("unstable results")).toBeInTheDocument()
})

test("stable measure-sets summary has no unstable badge", async () => {
  stubFetch({ "/api/operations/10": operationDetail({ summary: { kind: "measure-sets", sets: 2, stableSets: 2 } }) })
  renderApp("/operations/10")
  expect(await screen.findByText("2/2 sets stable")).toBeInTheDocument()
  expect(screen.queryByText("unstable results")).not.toBeInTheDocument()
})

test("compare-matrix summary shows the matrix parameter and equivalent cells", async () => {
  stubFetch({
    "/api/operations/10": operationDetail({
      summary: { kind: "compare-matrix", parameterName: "BatchSize", parameterType: "int", cells: 3, equivalentCells: 2 },
    }),
  })
  renderApp("/operations/10")
  expect(await screen.findByText("parameter: BatchSize (int)")).toBeInTheDocument()
  expect(screen.getByText("2/3 cells equivalent")).toBeInTheDocument()
})

test("agent call card shows the client-supplied labels and is absent without them", async () => {
  stubFetch({
    "/api/operations/10": operationDetail({
      agent: agentCall({ callId: "exec-1", model: "gpt-6-sol", reasoningEffort: "low", sessionId: "s", turnId: "turn-1", turnTrigger: "exec", threadSource: "user" }),
    }),
  })
  renderApp("/operations/10")

  expect(await screen.findByRole("heading", { name: "Agent call" })).toBeInTheDocument()
  expect(screen.getByText("gpt-6-sol")).toBeInTheDocument()
  expect(screen.getByText("low")).toBeInTheDocument()
  expect(screen.getByText("turn-1")).toBeInTheDocument()
  expect(screen.getByText("exec-1")).toBeInTheDocument()
})

test("shows transcript model and effort when the call has no client labels", async () => {
  stubFetch({ "/api/operations/10": operationDetail({
    agent: agentCall({ transcriptModel: "claude-opus-5-5", transcriptReasoningEffort: "high", conversationTitle: "Optimize orders" }),
  }) })
  renderApp("/operations/10")
  expect(await screen.findByText("claude-opus-5-5")).toBeInTheDocument()
  expect(screen.getAllByText("from transcript")).toHaveLength(2)
  expect(screen.getByText("high")).toBeInTheDocument()
  expect(screen.getByText("Optimize orders")).toBeInTheDocument()
})

test("operations without call labels render no agent call card", async () => {
  stubFetch({ "/api/operations/10": operationDetail({ agent: null }) })
  renderApp("/operations/10")

  expect(await screen.findByRole("heading", { level: 1, name: "Operation #10" })).toBeInTheDocument()
  expect(screen.queryByRole("heading", { name: "Agent call" })).not.toBeInTheDocument()
})

test("measure operation shows recorded setup SQL before the measured query", async () => {
  const setup = "CREATE TABLE #data (id int);\nINSERT INTO #data VALUES (1);"
  stubFetch({ "/api/operations/10": operationDetail({
    operation: operation({ operation: "measure" }),
    sqlText: "SELECT id FROM #data",
    setupSqlText: setup,
  }) })
  renderApp("/operations/10")
  const section = await screen.findByRole("region", { name: "Setup SQL" })
  expect(within(section).getByTestId("sql-code")).toHaveTextContent(setup)
  expect(within(section).getByRole("button", { name: "Show formatted" })).toBeInTheDocument()
  expect(within(section).getByRole("button", { name: "Copy" })).toBeInTheDocument()
  expect(within(section).getByText(/same database session/)).toBeInTheDocument()
  expect(screen.getAllByTestId("sql-code")).toHaveLength(2)
})
