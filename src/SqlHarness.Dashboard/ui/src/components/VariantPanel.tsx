import type { VariantDetail } from "@/api/types"
import { Kpi } from "@/components/Kpi"
import { PlanWorkbench } from "@/components/plan/PlanWorkbench"
import { Badge } from "@/components/ui/badge"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { formatKb, formatNumber } from "@/lib/format"
import { tableIoDelta } from "@/lib/tableIoDelta"

const spread = (value: { min: number; median: number; max: number } | null, unit = "") =>
  value ? `${formatNumber(value.median)}${unit}` : "—"
const range = (value: { min: number; max: number } | null) => (value ? `${formatNumber(value.min)}–${formatNumber(value.max)}` : undefined)

export function VariantPanel({ operationId, variant, engine, compareMetrics = false }: { operationId: number; variant: VariantDetail; engine: string | null; compareMetrics?: boolean }) {
  const sqlServer = !compareMetrics || engine?.toLowerCase() === "sqlserver" || engine?.toLowerCase() === "sql-server"
  const tableIoRows = tableIoDelta(variant.tableIo, []).rows.flatMap(row => row.baseline ? [row.baseline] : [])
  return (
    <div className="space-y-4">
      <section aria-label="Key metrics" className="grid grid-cols-2 gap-4 md:grid-cols-4">
        <Kpi label="Elapsed (median)" value={spread(variant.elapsedMs, " ms")} hint={range(variant.elapsedMs)} />
        <Kpi label="CPU (median)" value={spread(variant.cpuMs, " ms")} hint={range(variant.cpuMs)} />
        {sqlServer && <Kpi label="Logical reads (median)" value={spread(variant.logicalReads)} hint={range(variant.logicalReads)} />}
        {sqlServer && <Kpi label="Memory grant used / granted" value={`${formatKb(variant.grantMaxUsedKb)} / ${formatKb(variant.grantGrantedKb)}`} />}
        {sqlServer && <Kpi label="DOP" value={formatNumber(variant.dop)} />}
        {sqlServer && <Kpi label="Spills" value={String(variant.spillCount)} />}
        {sqlServer && <Kpi label="Compile" value={variant.compileTimeMs === null ? "—" : `${variant.compileTimeMs} ms`} />}
        <Kpi label="Runs" value={String(variant.runs)} />
      </section>
      <div className="flex flex-wrap gap-1">
        {variant.hasWarnings && <Badge variant="outline">plan warnings</Badge>}
        {variant.hasImplicitConversion && <Badge variant="outline">implicit conversion</Badge>}
        {variant.missingIndexCount > 0 && <Badge variant="outline">{variant.missingIndexCount} missing index</Badge>}
      </div>
      {variant.tableIo.length > 0 && (
        <Card>
          <CardHeader>
            <CardTitle>Table IO</CardTitle>
            <p className="text-sm text-muted-foreground">{variant.totalLogicalReadsLabel}</p>
          </CardHeader>
          <CardContent className="overflow-x-auto">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Table</TableHead>
                  <TableHead className="text-right">Logical</TableHead>
                  <TableHead className="text-right">Scans</TableHead>
                  <TableHead className="text-right">LOB logical</TableHead>
                  <TableHead className="text-right">Total</TableHead>
                  <TableHead className="text-right">Cold runs</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {tableIoRows.map(row => (
                  <TableRow key={row.table}>
                    <TableCell className="font-mono">{row.table}</TableCell>
                    <TableCell className="text-right">{formatNumber(row.logicalReads)}</TableCell>
                    <TableCell className="text-right">{formatNumber(row.scanCount)}</TableCell>
                    <TableCell className="text-right">{formatNumber(row.lobLogicalReads)}</TableCell>
                    <TableCell className="text-right">{formatNumber(row.totalLogicalReads)}</TableCell>
                    <TableCell className="text-right">{row.coldRuns > 0 ? <Badge variant="outline">{row.coldRuns}</Badge> : "0"}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </CardContent>
        </Card>
      )}
      {Array.isArray(variant.waits) && variant.waits.length > 0 && (
        <Card>
          <CardHeader>
            <CardTitle>Waits (average per run)</CardTitle>
          </CardHeader>
          <CardContent className="overflow-x-auto">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Wait type</TableHead>
                  <TableHead className="text-right">Time (ms)</TableHead>
                  <TableHead className="text-right">Count</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {variant.waits.map(wait => (
                  <TableRow key={wait.waitType}>
                    <TableCell className="font-mono">{wait.waitType}</TableCell>
                    <TableCell className="text-right">{formatNumber(Math.round(wait.averageWaitMs))}</TableCell>
                    <TableCell className="text-right">{formatNumber(Math.round(wait.averageWaitCount))}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </CardContent>
        </Card>
      )}
      {variant.postgres && (!compareMetrics || engine?.toLowerCase() === "postgres" || engine?.toLowerCase() === "postgresql") && (
        <Card>
          <CardHeader>
            <CardTitle>Buffers (median per run)</CardTitle>
          </CardHeader>
          <CardContent className="grid grid-cols-2 gap-4 md:grid-cols-6">
            <Kpi label="Shared hit" value={formatNumber(variant.postgres.sharedHit)} />
            <Kpi label="Shared read" value={formatNumber(variant.postgres.sharedRead)} />
            <Kpi label="Shared dirtied" value={formatNumber(variant.postgres.sharedDirtied)} />
            <Kpi label="Shared written" value={formatNumber(variant.postgres.sharedWritten)} />
            <Kpi label="Temp read" value={formatNumber(variant.postgres.tempRead)} />
            <Kpi label="Temp written" value={formatNumber(variant.postgres.tempWritten)} />
          </CardContent>
        </Card>
      )}
      <PlanWorkbench operationId={operationId} variant={variant} engine={engine} />
    </div>
  )
}
