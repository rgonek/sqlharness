import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { fireEvent, render, screen, waitFor, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test, vi } from "vitest"
import type { DimensionValueSummary } from "@/api/types"
import { stats } from "@/test/fixtures"
import { renderApp, stubFetch } from "@/test/render"
import { Matrix } from "./ProfileDimensionsPanel"
import { StatsPage } from "./StatsPage"

test("shows KPIs, charts and top lists, and switches range", async () => {
  const calls = stubFetch({ "/api/stats": stats(), "/api/profiles": profilesResponse() })
  renderApp("/stats")

  expect(await screen.findByText("90%")).toBeInTheDocument()
  expect(screen.getByText(/5 of 6 operations have both estimates; 1 raw only/)).toBeInTheDocument()
  expect(screen.getByText("Operations per day (UTC)")).toBeInTheDocument()
  expect(screen.getByText("aaaaaaaaaaaa")).toBeInTheDocument()
  expect(screen.getByRole("cell", { name: "Orders" })).toBeInTheDocument()
  expect(screen.getByRole("cell", { name: "PAGEIOLATCH_SH" })).toBeInTheDocument()

  await userEvent.click(screen.getByRole("tab", { name: "All time" }))
  await screen.findByText("90%")
  expect(calls.some(call => call.startsWith("/api/stats?") && !call.includes("from="))).toBe(true)
  expect(calls.some(call => call.startsWith("/api/stats?from="))).toBe(true)
})

test("no-profile activity has a distinct scope and drills down without a profile filter", async () => {
  const requests: URL[] = []
  const calls = stubFetch({
    "/api/stats": (url: URL) => {
      if (url.searchParams.get("unprofiled") !== "true") return jsonResponse(stats())
      requests.push(url)
      const literalUnknown = dimensionValue("region", "Unknown", 1, 10)
      const api = dimensionValue("component", "api", 1, 10)
      const response = stats({ profileOperations: [{ profile: null, operations: 1 }],
        profileDimensions: {
          profile: null, profileDefinitionAvailable: false, operations: 1,
          dimensions: [{ name: "region", values: [literalUnknown] }, { name: "component", values: [api] }],
          targets: [{ profile: null, database: "db", engine: "postgres", server: "local", count: 1 }],
          targetCount: 1,
          matrix: { rowDimension: "region", columnDimension: "component",
            cells: [{ row: literalUnknown, column: api, metrics: matrixMetrics(1, 10) }], totals: matrixMetrics(1, 10) },
        } })
      return jsonResponse(response)
    },
    "/api/profiles": profilesResponse(),
    "/api/operations": (url: URL) => {
      calls.push(url.pathname + url.search)
      return jsonResponse({ items: [], nextCursor: null })
    },
  })
  renderApp("/stats")

  await userEvent.selectOptions(await screen.findByRole("combobox", { name: "Statistics profile" }), "unprofiled")
  expect(await screen.findByText(/No profile · database template: definition unavailable/)).toBeInTheDocument()
  expect(await screen.findByRole("button", { name: /region Unknown, component api: 1/ })).toBeInTheDocument()
  expect(screen.getByRole("cell", { name: "postgres" })).toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: /region Unknown, component api: 1/ }))
  expect(await screen.findByText("Filtered operation history")).toBeInTheDocument()
  const drilldown = new URL(`http://local${calls.find(call => call.startsWith("/api/operations?"))}`)
  expect(drilldown.searchParams.get("unprofiled")).toBe("true")
  expect(drilldown.searchParams.has("profile")).toBe(false)
  expect(JSON.parse(drilldown.searchParams.get("dimensions") ?? "{}")).toEqual({ region: "Unknown", component: "api" })
  expect(requests[0].searchParams.get("unprofiled")).toBe("true")
})

test("explains raw-only MCP coverage and keeps missing gain unavailable", async () => {
  stubFetch({
    "/api/stats": stats({ tokens: { raw: 0, emitted: 0, totalOperations: 2, pairedOperations: 0,
      rawOnlyOperations: 2, emittedOnlyOperations: 0, missingBothOperations: 0 } }),
    "/api/profiles": profilesResponse(),
  })
  renderApp("/stats")

  expect(await screen.findByText("Unavailable")).toBeInTheDocument()
  expect(screen.getByText(/0 of 2 operations have both estimates; 2 raw only, 0 emitted only, 0 missing both/)).toBeInTheDocument()
  expect(screen.getByText(/Gain unavailable because no operation has both estimates/)).toBeInTheDocument()
  expect(screen.getByText(/Estimates use output bytes, not actual model usage/)).toBeInTheDocument()
})

test("preserves negative net savings", async () => {
  const tokens = { raw: 100, emitted: 120, totalOperations: 1, pairedOperations: 1,
    rawOnlyOperations: 0, emittedOnlyOperations: 0, missingBothOperations: 0 }
  stubFetch({ "/api/stats": stats({ tokens }), "/api/profiles": profilesResponse() })
  renderApp("/stats")
  expect(await screen.findByText("-20%")).toBeInTheDocument()
  expect(screen.getByText(/1 of 1 operations have both estimates/)).toBeInTheDocument()
})

test("distinguishes empty activity from incomplete estimate coverage", async () => {
  stubFetch({ "/api/stats": stats({ operations: [], statuses: [], profileOperations: [],
    tokens: { raw: 0, emitted: 0, totalOperations: 0, pairedOperations: 0, rawOnlyOperations: 0,
      emittedOnlyOperations: 0, missingBothOperations: 0 } }), "/api/profiles": profilesResponse() })
  renderApp("/stats")
  expect(await screen.findByText("No activity")).toBeInTheDocument()
  expect(screen.getByText(/No operations in this time range/)).toBeInTheDocument()
})

test("selects a profile and shows filtered, searchable dimension matrix totals", async () => {
  const originalWarn = console.warn
  vi.spyOn(console, "warn").mockImplementation((...args: unknown[]) => {
    if (!String(args[0]).startsWith("The width(0) and height(0) of chart")) originalWarn(...args)
  })
  const calls = stubFetch({
    "/api/stats": (url: URL) => jsonResponse(filteredProfileStats(url)),
    "/api/profiles": profilesResponse(),
  })
  renderApp("/stats")

  const profile = await screen.findByRole("combobox", { name: "Statistics profile" })
  expect(await screen.findByText(/database template: \{project\}\.\{component\}/)).toBeInTheDocument()
  expect(profile).toHaveValue("profile:app")
  expect(await screen.findByRole("button", { name: /component api, project alpha: 2/ })).toBeInTheDocument()
  expect(screen.getByText(/Full-scope total: 3 \(3 operations\)/)).toBeInTheDocument()

  await userEvent.type(screen.getByRole("textbox", { name: "Search row and column values" }), "beta")
  expect(screen.getByText(/Visible total: 1 \(1 operations\)/)).toBeInTheDocument()
  expect(screen.getByText(/Full-scope total: 3 \(3 operations\)/)).toBeInTheDocument()

  const projectFilter = screen.getByRole("combobox", { name: "project" })
  await userEvent.selectOptions(projectFilter, "value:alpha")
  expect(await screen.findByText("Active filters:")).toBeInTheDocument()
  expect(calls.some(call => call.includes("dimensions=%7B%22project%22%3A%22alpha%22%7D"))).toBe(true)
  await waitFor(() => expect(screen.queryByRole("button", { name: "Filter component to web" })).not.toBeInTheDocument())
  await userEvent.selectOptions(projectFilter, "value:beta")
  expect(calls.some(call => call.includes("dimensions=%7B%22project%22%3A%22beta%22%7D"))).toBe(true)

  await userEvent.selectOptions(profile, "profile:solo")
  expect(await screen.findByRole("heading", { name: "Breakdown" })).toBeInTheDocument()
  expect(profile).toHaveValue("profile:solo")
  await userEvent.click(screen.getByRole("tab", { name: "All time" }))
  await screen.findByRole("heading", { name: "Breakdown" })
  expect(profile).toHaveValue("profile:solo")

  await userEvent.selectOptions(profile, "profile:empty")
  expect(await screen.findByText("This profile has no configured or recorded dimensions.")).toBeInTheDocument()
  expect(screen.getByRole("heading", { name: "Databases in this scope" })).toBeInTheDocument()
})

test("labels the bounded physical-target list with its full filtered count", async () => {
  const response = stats()
  response.profileDimensions.targets = Array.from({ length: 20 }, (_, index) => ({
    profile: "local", database: `db-${index}`, count: 1, engine: "sqlserver", server: `server-${index}`,
  }))
  response.profileDimensions.targetCount = 23
  stubFetch({ "/api/stats": response, "/api/profiles": profilesResponse() })
  renderApp("/stats")

  expect(await screen.findByText("Showing 20 of 23 matching physical targets (top 20 limit).")).toBeInTheDocument()
  const databaseTable = within(screen.getByRole("region", { name: "Filtered databases" }))
  expect(databaseTable.getAllByRole("row")).toHaveLength(21)
  expect(databaseTable.getByRole("cell", { name: "db-19" })).toBeInTheDocument()
  expect(databaseTable.queryByRole("cell", { name: "db-20" })).not.toBeInTheDocument()
})

test("switching from a profile named unprofiled to no-profile resets matrix controls", async () => {
  const calls = stubFetch({
    "/api/stats": (url: URL) => {
      const unprofiled = url.searchParams.get("unprofiled") === "true"
      const values = Array.from({ length: 30 }, (_, index) => dimensionValue(
        unprofiled ? "region" : "project", `${unprofiled ? "scope" : "profile"}-value-${index}`, 1, 10))
      const column = dimensionValue("component", "api", 30, 300)
      const response = stats({
        profileOperations: [{ profile: "unprofiled", operations: 30 }, { profile: null, operations: 30 }],
        profileDimensions: {
          profile: unprofiled ? null : "unprofiled", profileDefinitionAvailable: !unprofiled, operations: 30,
          dimensions: [{ name: unprofiled ? "region" : "project", values }, { name: "component", values: [column] }],
          targets: [{ profile: unprofiled ? null : "unprofiled", database: "db", engine: "sqlserver", server: "srv", count: 30 }],
          targetCount: 1,
          matrix: {
            rowDimension: unprofiled ? "region" : "project", columnDimension: "component",
            cells: values.map(value => ({ row: value, column, metrics: matrixMetrics(1, 10) })),
            totals: matrixMetrics(30, 300),
          },
        },
      })
      return jsonResponse(response)
    },
    "/api/profiles": {
      status: "valid", message: null, profiles: [{
        name: "unprofiled", engine: "sqlserver", server: "srv", database: "db", auth: "sql", sqlUser: null,
        passwordEnvVar: null, sslMode: null, trustServerCertificate: false, tls: "verify", rootCertificate: null,
        vars: [{ name: "project", rule: ".*" }, { name: "component", rule: ".*" }],
      }],
    },
  })
  renderApp("/stats")

  const profile = await screen.findByRole("combobox", { name: "Statistics profile" })
  await screen.findByText(/Showing 25 of 30 row values/)
  expect(profile).toHaveValue("profile:unprofiled")
  await userEvent.selectOptions(screen.getByRole("combobox", { name: "Metric" }), "failed")
  await userEvent.type(screen.getByRole("textbox", { name: "Search row and column values" }), "profile-value-29")
  await userEvent.selectOptions(screen.getByRole("combobox", { name: "Display limit per axis" }), "10")

  fireEvent.change(profile, { target: { value: "unprofiled" } })

  await waitFor(() => expect(profile).toHaveValue("unprofiled"))
  await waitFor(() => expect(calls.some(call => call.startsWith("/api/stats?") && call.includes("unprofiled=true"))).toBe(true))
  expect(await screen.findByRole("combobox", { name: "region" })).toBeInTheDocument()
  expect(screen.getByRole("combobox", { name: "Metric" })).toHaveValue("operations")
  expect(screen.getByRole("textbox", { name: "Search row and column values" })).toHaveValue("")
  expect(screen.getByRole("combobox", { name: "Display limit per axis" })).toHaveValue("25")
  expect(screen.getByText(/Showing 25 of 30 row values/)).toBeInTheDocument()
})

test("marks search-hidden cross-intersections and excludes them from visible totals", async () => {
  const matchRow = dimensionValue("row", "match-row", 1, 10)
  const beta = dimensionValue("row", "beta", 2, 20)
  const alpha = dimensionValue("column", "alpha", 4, 40)
  const matchColumn = dimensionValue("column", "match-column", 2, 20)
  const cells = [
    { row: matchRow, column: alpha, metrics: matrixMetrics(1, 10) },
    { row: beta, column: matchColumn, metrics: matrixMetrics(2, 20) },
    { row: beta, column: alpha, metrics: matrixMetrics(3, 30) },
  ]
  render(<Matrix cells={cells} totals={matrixMetrics(6, 60)} rowName="row" columnName="column"
    metric="operations" profile="app" range="7d" window={{ from: "2026-10-01T00:00:00.000Z", to: "2026-10-08T00:00:00.000Z" }}
    unprofiled={false} filters={{}} />)

  await userEvent.type(screen.getByRole("textbox", { name: "Search row and column values" }), "match")

  expect(screen.getByLabelText("Operations hidden by matrix search")).toBeInTheDocument()
  expect(screen.getByText(/Visible total: 3 \(3 operations\)/)).toBeInTheDocument()
  expect(screen.getByText(/Full-scope total: 6 \(6 operations\)/)).toBeInTheDocument()
})

test("preserves a non-axis filter in cell scope across a time-range change", async () => {
  const calls = stubFetch({ "/api/stats": (url: URL) => jsonResponse(filteredProfileStats(url)), "/api/profiles": profilesResponse() })
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity } } })
  const onCellSelect = vi.fn()
  render(<QueryClientProvider client={client}><StatsPage onDimensionCellSelect={onCellSelect} /></QueryClientProvider>)

  const profile = await screen.findByRole("combobox", { name: "Statistics profile" })
  expect(await screen.findByRole("combobox", { name: "region" })).toBeInTheDocument()
  await userEvent.selectOptions(screen.getByRole("combobox", { name: "region" }), "value:west")
  const matrixCell = await screen.findByRole("button", { name: /component api, project alpha:/ })
  await userEvent.click(matrixCell)
  const firstScopedUrl = new URL(`http://local${calls.find(call => call.startsWith("/api/stats?") && call.includes("profile=app"))}`)
  expect(onCellSelect).toHaveBeenLastCalledWith({ profile: "app", unprofiled: false, range: "7d",
    from: firstScopedUrl.searchParams.get("from") ?? undefined, to: firstScopedUrl.searchParams.get("to") ?? undefined,
    dimensions: { region: "west", component: "api", project: "alpha" } })
  expect(calls.some(call => call.includes("dimensions=%7B%22region%22%3A%22west%22%7D")
    && call.includes("rowDimension=component") && call.includes("columnDimension=project"))).toBe(true)

  await userEvent.click(screen.getByRole("tab", { name: "All time" }))
  await waitFor(() => expect(profile).toHaveValue("profile:app"))
  await waitFor(() => expect(screen.getByRole("combobox", { name: "region" })).toHaveValue("value:west"))
  await userEvent.click(await screen.findByRole("button", { name: /component api, project alpha:/ }))
  const latestScopedUrl = new URL(`http://local${calls.filter(call => call.startsWith("/api/stats?") && call.includes("profile=app")).at(-1)}`)
  expect(onCellSelect).toHaveBeenLastCalledWith({ profile: "app", unprofiled: false, range: "all",
    from: latestScopedUrl.searchParams.get("from") ?? undefined, to: latestScopedUrl.searchParams.get("to") ?? undefined,
    dimensions: { region: "west", component: "api", project: "alpha" } })
  expect(calls.some(call => !call.includes("from=") && call.includes("dimensions=%7B%22region%22%3A%22west%22%7D")
    && call.includes("rowDimension=component") && call.includes("columnDimension=project"))).toBe(true)
})

test("cell navigation keeps the exact stats window across a minute boundary", async () => {
  let now = new Date("2026-10-09T12:34:59.000Z").getTime()
  vi.spyOn(Date, "now").mockImplementation(() => now)
  try {
    const calls = stubFetch({
      "/api/stats": (url: URL) => jsonResponse(filteredProfileStats(url)),
      "/api/profiles": profilesResponse(),
      "/api/operations": { items: [], nextCursor: null },
    })
    renderApp("/stats")
    const cell = await screen.findByRole("button", { name: /component api, project alpha:/ })
    const statisticsRequest = calls.find(call => call.startsWith("/api/stats?") && call.includes("profile=app"))
    const statsWindow = new URL(`http://local${statisticsRequest}`).searchParams
    expect(statsWindow.get("to")).toBe("2026-10-09T12:34:00.000Z")

    now = new Date("2026-10-09T12:35:01.000Z").getTime()
    fireEvent.click(cell)
    expect(await screen.findByText("Filtered operation history")).toBeInTheDocument()
    const operationsRequest = calls.find(call => call.startsWith("/api/operations?"))
    const operationsWindow = new URL(`http://local${operationsRequest}`).searchParams
    expect(operationsWindow.get("from")).toBe(statsWindow.get("from"))
    expect(operationsWindow.get("to")).toBe(statsWindow.get("to"))
    expect(operationsWindow.get("profile")).toBe("app")
    expect(JSON.parse(operationsWindow.get("dimensions") ?? "{}")).toEqual({ component: "api", project: "alpha" })
  } finally {
    vi.restoreAllMocks()
  }
})

function jsonResponse(value: unknown) {
  return new Response(JSON.stringify(value), { status: 200, headers: { "Content-Type": "application/json" } })
}

function profilesResponse() {
  return {
    status: "valid", message: null, profiles: ["app", "solo", "empty"].map(name => ({
      name, engine: "sqlserver", server: "server-{region}", database: name === "app" ? "{project}.{component}" : "db",
      auth: "sql", sqlUser: null, passwordEnvVar: null, sslMode: null, trustServerCertificate: false,
      tls: "verify", rootCertificate: null,
      vars: name === "app" ? [{ name: "project", rule: ".*" }, { name: "component", rule: ".*" }, { name: "region", rule: ".*" }]
        : name === "solo" ? [{ name: "region", rule: ".*" }] : [],
    })),
  }
}

function profileStats(profile: string | null) {
  const response = stats({ profileOperations: [
    { profile: "app", operations: 3 }, { profile: "solo", operations: 2 }, { profile: "empty", operations: 1 },
  ] })
  if (profile === "solo") {
    response.profileDimensions = {
      profile: "solo", profileDefinitionAvailable: true, operations: 2,
      dimensions: [{ name: "region", values: [dimensionValue("region", "west", 2, 20)] }],
      targets: [{ profile: "solo", database: "db", engine: "sqlserver", server: "server-west", count: 2 }], targetCount: 1, matrix: null,
    }
  } else if (profile === "empty") {
    response.profileDimensions = { profile: "empty", profileDefinitionAvailable: true, operations: 1, dimensions: [],
      targets: [{ profile: "empty", database: "db", engine: "sqlserver", server: "server", count: 1 }], targetCount: 1, matrix: null }
  } else {
    const api = dimensionValue("component", "api", 2, 30, 0, 1)
    const web = dimensionValue("component", "web", 1, null, 1, 0)
    const alpha = dimensionValue("project", "alpha", 2, 30, 0, 1)
    const beta = dimensionValue("project", "beta", 1, null, 1, 0)
    response.profileDimensions = {
      profile: "app", profileDefinitionAvailable: true, operations: 3,
      dimensions: [
        { name: "component", values: [api, web] },
        { name: "project", values: [alpha, beta] },
        { name: "region", values: [dimensionValue("region", "west", 2, 20), dimensionValue("region", "east", 1, 10)] },
      ],
      targets: [{ profile: "app", database: "alpha.api", engine: "sqlserver", server: "server-eu", count: 3 }],
      targetCount: 1,
      matrix: {
        rowDimension: "component", columnDimension: "project",
        cells: [
          { row: api, column: alpha, metrics: { operations: 2, totalDurationMs: 30, durationAvailableOperations: 2, durationUnavailableOperations: 0, failed: 0, rejected: 1 } },
          { row: web, column: beta, metrics: { operations: 1, totalDurationMs: null, durationAvailableOperations: 0, durationUnavailableOperations: 1, failed: 1, rejected: 0 } },
        ],
        totals: { operations: 3, totalDurationMs: 30, durationAvailableOperations: 2, durationUnavailableOperations: 1, failed: 1, rejected: 1 },
      },
    }
  }
  return response
}

function filteredProfileStats(url: URL) {
  const response = profileStats(url.searchParams.get("profile"))
  const filterJson = url.searchParams.get("dimensions")
  if (!filterJson || url.searchParams.get("profile") !== "app") return response
  const filters = JSON.parse(filterJson) as Record<string, string | null>
  const matches = (value: DimensionValueSummary, name: string) => !(name in filters)
    || (filters[name] === null ? value.isUnknown : !value.isUnknown && value.value === filters[name])
  const matrix = response.profileDimensions.matrix
  if (matrix) {
    matrix.cells = matrix.cells.filter(cell => matches(cell.row, matrix.rowDimension) && matches(cell.column, matrix.columnDimension))
    response.profileDimensions.dimensions = response.profileDimensions.dimensions.map(dimension => ({
      ...dimension,
      values: dimension.values.filter(value => dimension.name === matrix.rowDimension
        ? matrix.cells.some(cell => cell.row.value === value.value && cell.row.isUnknown === value.isUnknown)
        : dimension.name === matrix.columnDimension
          ? matrix.cells.some(cell => cell.column.value === value.value && cell.column.isUnknown === value.isUnknown)
          : matches(value, dimension.name)),
    }))
    matrix.totals = {
      operations: matrix.cells.reduce((sum, cell) => sum + cell.metrics.operations, 0),
      totalDurationMs: matrix.cells.reduce<number | null>((sum, cell) => sum === null ? cell.metrics.totalDurationMs : sum + (cell.metrics.totalDurationMs ?? 0), null),
      durationAvailableOperations: matrix.cells.reduce((sum, cell) => sum + cell.metrics.durationAvailableOperations, 0),
      durationUnavailableOperations: matrix.cells.reduce((sum, cell) => sum + cell.metrics.durationUnavailableOperations, 0),
      failed: matrix.cells.reduce((sum, cell) => sum + cell.metrics.failed, 0),
      rejected: matrix.cells.reduce((sum, cell) => sum + cell.metrics.rejected, 0),
    }
  }
  response.profileDimensions.operations = matrix?.totals.operations ?? 0
  response.profileDimensions.targets = response.profileDimensions.targets.map(target => ({ ...target, count: matrix?.totals.operations ?? 0 }))
  return response
}

function dimensionValue(name: string, value: string, operations: number, totalDurationMs: number | null, failed = 0, rejected = 0): DimensionValueSummary {
  return { name, value, isUnknown: false, source: "recorded", operations, percentage: operations / 3 * 100,
    totalDurationMs, durationAvailableOperations: totalDurationMs === null ? 0 : operations,
    durationUnavailableOperations: totalDurationMs === null ? operations : 0, failed, rejected }
}

function matrixMetrics(operations: number, totalDurationMs: number) {
  return { operations, totalDurationMs, durationAvailableOperations: operations, durationUnavailableOperations: 0, failed: 0, rejected: 0 }
}
