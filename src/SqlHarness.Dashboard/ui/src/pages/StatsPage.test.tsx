import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { render, screen, waitFor } from "@testing-library/react"
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
  expect(screen.getByText("Operations per day (UTC)")).toBeInTheDocument()
  expect(screen.getByText("aaaaaaaaaaaa")).toBeInTheDocument()
  expect(screen.getByRole("cell", { name: "Orders" })).toBeInTheDocument()
  expect(screen.getByRole("cell", { name: "PAGEIOLATCH_SH" })).toBeInTheDocument()

  await userEvent.click(screen.getByRole("tab", { name: "All time" }))
  await screen.findByText("90%")
  expect(calls.some(call => call === "/api/stats")).toBe(true)
  expect(calls.some(call => call.startsWith("/api/stats?from="))).toBe(true)
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
    metric="operations" profile="app" range="7d" filters={{}} />)

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
  expect(onCellSelect).toHaveBeenLastCalledWith({ profile: "app", range: "7d", dimensions: { region: "west", component: "api", project: "alpha" } })
  expect(calls.some(call => call.includes("dimensions=%7B%22region%22%3A%22west%22%7D")
    && call.includes("rowDimension=component") && call.includes("columnDimension=project"))).toBe(true)

  await userEvent.click(screen.getByRole("tab", { name: "All time" }))
  await waitFor(() => expect(profile).toHaveValue("profile:app"))
  await waitFor(() => expect(screen.getByRole("combobox", { name: "region" })).toHaveValue("value:west"))
  await userEvent.click(await screen.findByRole("button", { name: /component api, project alpha:/ }))
  expect(onCellSelect).toHaveBeenLastCalledWith({ profile: "app", range: "all", dimensions: { region: "west", component: "api", project: "alpha" } })
  expect(calls.some(call => !call.includes("from=") && call.includes("dimensions=%7B%22region%22%3A%22west%22%7D")
    && call.includes("rowDimension=component") && call.includes("columnDimension=project"))).toBe(true)
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
      targets: [{ profile: "solo", database: "db", engine: "sqlserver", server: "server-west", count: 2 }], matrix: null,
    }
  } else if (profile === "empty") {
    response.profileDimensions = { profile: "empty", profileDefinitionAvailable: true, operations: 1, dimensions: [],
      targets: [{ profile: "empty", database: "db", engine: "sqlserver", server: "server", count: 1 }], matrix: null }
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
