import { useMemo, useState } from "react"
import { useStats, type StatsRange } from "@/api/queries"
import type { DimensionAggregate, DimensionMatrixCell, DimensionStat, DimensionValueSummary, TargetStat } from "@/api/types"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { formatDuration, formatNumber, formatPercent } from "@/lib/format"

export type DimensionCellScope = {
  profile: string | null
  range: StatsRange
  dimensions: Record<string, string | null>
}

type Metric = "operations" | "duration" | "failed" | "rejected"
type Axes = { row: string; column: string }

const metricLabels: Record<Metric, string> = {
  operations: "Operations",
  duration: "Total execution duration",
  failed: "Failed operations",
  rejected: "Rejected operations",
}

function dimensionLabel(value: DimensionValueSummary) {
  return value.isUnknown ? "Unknown (missing)" : value.value
}

function identityKey(value: Pick<DimensionValueSummary, "isUnknown" | "value">) {
  return JSON.stringify([value.isUnknown, value.value])
}

function metricValue(metrics: DimensionAggregate, metric: Metric): number | null {
  if (metric === "duration") return metrics.totalDurationMs
  return metrics[metric]
}

function formattedMetric(metrics: DimensionAggregate, metric: Metric): string {
  if (metrics.operations === 0) return "—"
  const value = metricValue(metrics, metric)
  if (value === null) return "Unavailable"
  return metric === "duration" ? formatDuration(value) : formatNumber(value)
}

function matrixMetric(metrics: DimensionAggregate, metric: Metric): string {
  const value = formattedMetric(metrics, metric)
  return metric === "duration" && metrics.durationUnavailableOperations > 0 && metrics.operations > 0
    ? `${value} (${formatNumber(metrics.durationUnavailableOperations)} unavailable)`
    : value
}

function valueOption(value: DimensionValueSummary) {
  return value.isUnknown ? "missing:" : `value:${value.value}`
}

function parseValueOption(value: string): string | null {
  if (value.startsWith("missing:")) return null
  return value.slice("value:".length)
}

function DimensionFilter({
  dimension,
  selected,
  onSelect,
}: {
  dimension: DimensionStat
  selected: string | null | undefined
  onSelect: (value: string | null | undefined) => void
}) {
  return (
    <div className="grid min-w-40 flex-1 gap-1.5">
      <Label htmlFor={`dimension-filter-${dimension.name}`}>{dimension.name}</Label>
      <select
        id={`dimension-filter-${dimension.name}`}
        className="h-8 min-w-0 rounded-lg border border-input bg-background px-2 text-sm focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring active:bg-muted"
        value={selected === undefined ? "" : selected === null ? "missing:" : `value:${selected}`}
        onChange={event => onSelect(event.target.value === "" ? undefined : parseValueOption(event.target.value))}
      >
        <option value="">All values</option>
        {dimension.values.map(value => (
          <option key={identityKey(value)} value={valueOption(value)}>{dimensionLabel(value)}</option>
        ))}
      </select>
    </div>
  )
}

function DimensionBreakdown({
  dimension,
  onFilter,
}: {
  dimension: DimensionStat
  onFilter: (value: string | null) => void
}) {
  return (
    <div className="overflow-x-auto">
      <Table>
        <TableHeader>
          <TableRow>
            <TableHead>{dimension.name}</TableHead>
            <TableHead className="text-right">Operations</TableHead>
            <TableHead className="text-right">Share</TableHead>
            <TableHead className="text-right">Total duration</TableHead>
            <TableHead className="text-right">Failed</TableHead>
            <TableHead className="text-right">Rejected</TableHead>
          </TableRow>
        </TableHeader>
        <TableBody>
          {dimension.values.map(value => (
            <TableRow key={identityKey(value)}>
              <TableCell>
                <Button
                  type="button"
                  variant="link"
                  className="h-auto min-h-8 max-w-72 justify-start whitespace-normal text-left"
                  aria-label={`Filter ${dimension.name} to ${dimensionLabel(value)}`}
                  onClick={() => onFilter(value.isUnknown ? null : value.value)}
                >
                  <span className="break-all">{dimensionLabel(value)}</span>
                </Button>
              </TableCell>
              <TableCell className="text-right">{formatNumber(value.operations ?? 0)}</TableCell>
              <TableCell className="text-right">{formatPercent((value.percentage ?? 0) / 100)}</TableCell>
              <TableCell className="text-right">
                {value.totalDurationMs === null ? "Unavailable" : formatDuration(value.totalDurationMs)}
                {value.durationUnavailableOperations ? ` (${formatNumber(value.durationUnavailableOperations)} unavailable)` : ""}
              </TableCell>
              <TableCell className="text-right">{formatNumber(value.failed ?? 0)}</TableCell>
              <TableCell className="text-right">{formatNumber(value.rejected ?? 0)}</TableCell>
            </TableRow>
          ))}
          {dimension.values.length === 0 && <TableRow><TableCell colSpan={6} className="text-muted-foreground">No operations in this scope.</TableCell></TableRow>}
        </TableBody>
      </Table>
    </div>
  )
}

function aggregateCells(cells: DimensionMatrixCell[]): DimensionAggregate {
  const durations = cells.map(cell => cell.metrics.totalDurationMs).filter((value): value is number => value !== null)
  return {
    operations: cells.reduce((sum, cell) => sum + cell.metrics.operations, 0),
    totalDurationMs: durations.length ? durations.reduce((sum, value) => sum + value, 0) : null,
    durationAvailableOperations: cells.reduce((sum, cell) => sum + cell.metrics.durationAvailableOperations, 0),
    durationUnavailableOperations: cells.reduce((sum, cell) => sum + cell.metrics.durationUnavailableOperations, 0),
    failed: cells.reduce((sum, cell) => sum + cell.metrics.failed, 0),
    rejected: cells.reduce((sum, cell) => sum + cell.metrics.rejected, 0),
  }
}

function Matrix({
  cells,
  totals,
  rowName,
  columnName,
  metric,
  profile,
  range,
  filters,
  onCellSelect,
}: {
  cells: DimensionMatrixCell[]
  totals: DimensionAggregate
  rowName: string
  columnName: string
  metric: Metric
  profile: string | null
  range: StatsRange
  filters: Record<string, string | null>
  onCellSelect?: (scope: DimensionCellScope) => void
}) {
  const [search, setSearch] = useState("")
  const [limit, setLimit] = useState(25)
  const match = search.trim().toLocaleLowerCase()
  const matchingCells = useMemo(() => match
    ? cells.filter(cell => dimensionLabel(cell.row).toLocaleLowerCase().includes(match)
      || dimensionLabel(cell.column).toLocaleLowerCase().includes(match))
    : cells, [cells, match])
  const rows = useMemo(() => [...new Map(matchingCells.map(cell => [identityKey(cell.row), cell.row])).values()], [matchingCells])
  const columns = useMemo(() => [...new Map(matchingCells.map(cell => [identityKey(cell.column), cell.column])).values()], [matchingCells])
  const visibleRows = rows.slice(0, limit)
  const visibleColumns = columns.slice(0, limit)
  const visibleCells = matchingCells.filter(cell => visibleRows.some(row => identityKey(row) === identityKey(cell.row))
    && visibleColumns.some(column => identityKey(column) === identityKey(cell.column)))
  const lookup = new Map(cells.map(cell => [`${identityKey(cell.row)}|${identityKey(cell.column)}`, cell]))
  const max = Math.max(0, ...cells.map(cell => metricValue(cell.metrics, metric) ?? 0))
  const visibleMetrics = aggregateCells(visibleCells)
  const rowTotals = new Map(visibleRows.map(row => [identityKey(row), aggregateCells(visibleCells.filter(cell => identityKey(cell.row) === identityKey(row)))]))
  const columnTotals = new Map(visibleColumns.map(column => [identityKey(column), aggregateCells(visibleCells.filter(cell => identityKey(cell.column) === identityKey(column)))]))

  function cellClick(cell: DimensionMatrixCell) {
    if (!onCellSelect) return
    const dimensions = { ...filters, [rowName]: cell.row.isUnknown ? null : cell.row.value,
      [columnName]: cell.column.isUnknown ? null : cell.column.value }
    onCellSelect({ profile, range, dimensions })
  }

  return (
    <div className="grid gap-3">
      <div className="flex flex-wrap items-end gap-3">
        <div className="grid min-w-52 flex-1 gap-1.5">
          <Label htmlFor="matrix-search">Search row and column values</Label>
          <Input id="matrix-search" value={search} onChange={event => setSearch(event.target.value)} placeholder="Filter matrix values" />
        </div>
        <div className="grid min-w-36 gap-1.5">
          <Label htmlFor="matrix-limit">Display limit per axis</Label>
          <select id="matrix-limit" className="h-8 rounded-lg border border-input bg-background px-2 text-sm focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring active:bg-muted" value={limit} onChange={event => setLimit(Number(event.target.value))}>
            {[10, 25, 50, 100].map(value => <option key={value} value={value}>{value}</option>)}
          </select>
        </div>
      </div>
      <p className="text-sm text-muted-foreground" aria-live="polite">
        Showing {formatNumber(visibleRows.length)} of {formatNumber(rows.length)} row values and {formatNumber(visibleColumns.length)} of {formatNumber(columns.length)} column values. Visible total: {matrixMetric(visibleMetrics, metric)} ({formatNumber(visibleMetrics.operations)} operations). Full-scope total: {matrixMetric(totals, metric)} ({formatNumber(totals.operations)} operations).
      </p>
      <div className="max-w-full overflow-auto rounded-md border" tabIndex={0} role="region" aria-label={`${rowName} by ${columnName} matrix`}>
        <table className="min-w-full border-separate border-spacing-0 text-sm">
          <thead>
            <tr>
              <th scope="col" className="sticky start-0 z-10 min-w-36 bg-background p-2 text-start">{rowName} / {columnName}</th>
              {visibleColumns.map(column => <th scope="col" key={identityKey(column)} className="min-w-28 p-2 text-end">{dimensionLabel(column)}</th>)}
              <th scope="col" className="min-w-28 p-2 text-end">Visible total</th>
            </tr>
          </thead>
          <tbody>
            {visibleRows.map(row => (
              <tr key={identityKey(row)}>
                <th scope="row" className="sticky start-0 z-10 border-t bg-background p-2 text-start">{dimensionLabel(row)}</th>
                {visibleColumns.map(column => {
                  const cell = lookup.get(`${identityKey(row)}|${identityKey(column)}`)
                  const value = cell ? metricValue(cell.metrics, metric) : null
                  const shade = value !== null && max > 0 ? Math.round(22 * value / max) : 0
                  return <td key={identityKey(column)} className="border-t p-1 text-end">
                    {cell ? <button
                      type="button"
                      className="min-h-9 min-w-20 rounded px-2 py-1 text-end focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring active:scale-[0.98] disabled:cursor-default"
                      style={{ backgroundColor: `color-mix(in oklch, var(--chart-1) ${shade}%, transparent)` }}
                      aria-label={`${rowName} ${dimensionLabel(row)}, ${columnName} ${dimensionLabel(column)}: ${matrixMetric(cell.metrics, metric)}; ${formatNumber(cell.metrics.operations)} operations, ${formatNumber(cell.metrics.failed)} failed, ${formatNumber(cell.metrics.rejected)} rejected${cell.metrics.durationUnavailableOperations ? `, ${formatNumber(cell.metrics.durationUnavailableOperations)} durations unavailable` : ""}`}
                      disabled={!onCellSelect}
                      onClick={() => cellClick(cell)}
                    >{matrixMetric(cell.metrics, metric)}</button> : <span className="inline-block min-w-20 px-2 py-1 text-muted-foreground" aria-label="No operations">—</span>}
                  </td>
                })}
                <td className="border-t p-2 text-end font-medium">{matrixMetric(rowTotals.get(identityKey(row))!, metric)}</td>
              </tr>
            ))}
            <tr>
              <th scope="row" className="sticky start-0 z-10 border-t bg-background p-2 text-start font-medium">Visible total</th>
            {visibleColumns.map(column => <td key={identityKey(column)} className="border-t p-2 text-end font-medium">{matrixMetric(columnTotals.get(identityKey(column))!, metric)}</td>)}
              <td className="border-t p-2 text-end font-medium">{matrixMetric(visibleMetrics, metric)}</td>
            </tr>
          </tbody>
        </table>
      </div>
      {cells.some(cell => cell.metrics.durationUnavailableOperations > 0) && metric === "duration" && <p className="text-sm text-muted-foreground">Duration totals include only journaled durations. Cell labels identify unavailable durations.</p>}
    </div>
  )
}

export function ProfileDimensionsPanel({
  range,
  profile,
  profileName,
  databaseTemplate,
  profileDefinitionAvailable,
  dimensions,
  filterOptions,
  targets,
  onCellSelect,
}: {
  range: StatsRange
  profile: string | null
  profileName: string
  databaseTemplate: string | undefined
  profileDefinitionAvailable: boolean
  dimensions: DimensionStat[]
  filterOptions?: DimensionStat[]
  targets: TargetStat[]
  onCellSelect?: (scope: DimensionCellScope) => void
}) {
  const [filters, setFilters] = useState<Record<string, string | null>>({})
  const [axisChoice, setAxisChoice] = useState<Axes | null>(null)
  const [metric, setMetric] = useState<Metric>("operations")
  const names = dimensions.map(dimension => dimension.name)
  const defaultAxes = { row: names[0] ?? "", column: names.find(name => name !== names[0]) ?? "" }
  const axes = axisChoice && names.includes(axisChoice.row) && names.includes(axisChoice.column) && axisChoice.row !== axisChoice.column
    ? axisChoice
    : defaultAxes
  const scopedFilters = Object.fromEntries(Object.entries(filters).filter(([name]) => names.includes(name)))
  const query = useStats(range, profile, scopedFilters, axes.row || undefined, axes.column || undefined)
  const scope = query.data?.profileDimensions
  const displayedDimensions = scope?.dimensions ?? dimensions
  const displayedTargets = scope?.targets ?? targets
  const activeFilters = Object.entries(scopedFilters)
  const dimensionNames = displayedDimensions.map(dimension => dimension.name)
  const dimensionFilterOptions = filterOptions ?? dimensions

  function setDimensionFilter(name: string, value: string | null | undefined) {
    setFilters(current => {
      const next = { ...current }
      if (value === undefined) delete next[name]
      else next[name] = value
      return next
    })
  }

  function chooseAxes(next: Axes) {
    setAxisChoice(next)
  }

  const matrix = scope?.matrix

  return (
    <Card>
      <CardHeader>
        <CardTitle>Statistics targets</CardTitle>
        <p className="break-all text-sm text-muted-foreground">
          {profileName} · database template: {databaseTemplate ?? (profileDefinitionAvailable ? "(not specified)" : "definition unavailable")}
        </p>
      </CardHeader>
      <CardContent className="grid gap-5">
        {dimensionNames.length > 0 && <section className="grid gap-3" aria-label="Dimension filters">
          <div className="flex flex-wrap items-end gap-3">
            {dimensionFilterOptions.map(dimension => <DimensionFilter key={dimension.name} dimension={dimension} selected={scopedFilters[dimension.name]} onSelect={value => setDimensionFilter(dimension.name, value)} />)}
          </div>
          {activeFilters.length > 0 && <div className="flex flex-wrap items-center gap-2" aria-label="Active filters">
            <span className="text-sm">Active filters:</span>
            {activeFilters.map(([name, value]) => <span key={name} className="rounded-md bg-secondary px-2 py-1 text-sm">{name} = {value === null ? "Unknown (missing)" : value}</span>)}
            <Button type="button" variant="outline" size="sm" onClick={() => setFilters({})}>Clear filters</Button>
          </div>}
        </section>}

        {dimensionNames.length === 1 && <section className="grid gap-2">
          <h2 className="text-base font-semibold">Breakdown</h2>
          <DimensionBreakdown dimension={displayedDimensions[0]} onFilter={value => setDimensionFilter(displayedDimensions[0].name, value)} />
        </section>}

        {dimensionNames.length > 1 && <>
          <section className="grid gap-3" aria-label="Dimension matrix controls">
            <div className="flex flex-wrap items-end gap-3">
              <div className="grid min-w-40 flex-1 gap-1.5">
                <Label htmlFor="matrix-row">Rows</Label>
                <select id="matrix-row" className="h-8 rounded-lg border border-input bg-background px-2 text-sm focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring active:bg-muted" value={axes.row} onChange={event => {
                  const row = event.target.value
                  chooseAxes({ row, column: axes.column === row ? names.find(name => name !== row) ?? "" : axes.column })
                }}>{names.map(name => <option key={name} value={name}>{name}</option>)}</select>
              </div>
              <div className="grid min-w-40 flex-1 gap-1.5">
                <Label htmlFor="matrix-column">Columns</Label>
                <select id="matrix-column" className="h-8 rounded-lg border border-input bg-background px-2 text-sm focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring active:bg-muted" value={axes.column} onChange={event => {
                  const column = event.target.value
                  chooseAxes({ row: axes.row === column ? names.find(name => name !== column) ?? "" : axes.row, column })
                }}>{names.map(name => <option key={name} value={name} disabled={name === axes.row}>{name}</option>)}</select>
              </div>
              <div className="grid min-w-48 flex-1 gap-1.5">
                <Label htmlFor="matrix-metric">Metric</Label>
                <select id="matrix-metric" className="h-8 rounded-lg border border-input bg-background px-2 text-sm focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring active:bg-muted" value={metric} onChange={event => setMetric(event.target.value as Metric)}>
                  {(Object.entries(metricLabels) as [Metric, string][]).map(([value, label]) => <option key={value} value={value}>{label}</option>)}
                </select>
              </div>
            </div>
            {matrix && <Matrix cells={matrix.cells} totals={matrix.totals} rowName={matrix.rowDimension} columnName={matrix.columnDimension} metric={metric}
              profile={profile} range={range} filters={scopedFilters} onCellSelect={onCellSelect} />}
          </section>
          <section className="grid gap-4">
            <h2 className="text-base font-semibold">Dimension breakdowns</h2>
            {displayedDimensions.map(dimension => <details key={dimension.name} open className="min-w-0 rounded-md border p-3">
              <summary className="cursor-pointer font-medium focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring">{dimension.name}: {formatNumber(dimension.values.length)} values</summary>
              <div className="mt-3"><DimensionBreakdown dimension={dimension} onFilter={value => setDimensionFilter(dimension.name, value)} /></div>
            </details>)}
          </section>
        </>}

        {dimensionNames.length === 0 && <p className="text-sm text-muted-foreground">{profileDefinitionAvailable ? "This profile has no configured or recorded dimensions." : "No dimension definitions or recorded values are available for this profile."}</p>}

        <section className="grid gap-2" aria-label="Filtered databases">
          <h2 className="text-base font-semibold">Databases in this scope</h2>
          <div className="overflow-x-auto">
            <Table>
              <TableHeader><TableRow><TableHead>Engine</TableHead><TableHead>Server</TableHead><TableHead>Database</TableHead><TableHead className="text-right">Operations</TableHead></TableRow></TableHeader>
              <TableBody>
                {displayedTargets.map((target, index) => <TableRow key={`${target.engine ?? ""}/${target.server ?? ""}/${target.database ?? ""}/${index}`}>
                  <TableCell>{target.engine ?? "—"}</TableCell><TableCell className="max-w-72 break-all">{target.server ?? "—"}</TableCell><TableCell className="max-w-72 break-all">{target.database ?? "—"}</TableCell><TableCell className="text-right">{formatNumber(target.count)}</TableCell>
                </TableRow>)}
                {displayedTargets.length === 0 && <TableRow><TableCell colSpan={4} className="text-muted-foreground">No databases in this scope.</TableCell></TableRow>}
              </TableBody>
            </Table>
          </div>
          <p className="text-xs text-muted-foreground">Database names are shown with their engine and server because names alone do not identify a target.</p>
        </section>
        {query.isFetching && <p className="text-xs text-muted-foreground" aria-live="polite">Updating dimension statistics…</p>}
      </CardContent>
    </Card>
  )
}
