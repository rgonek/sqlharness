import { useState } from "react"
import { useDistilledPlan } from "@/api/queries"
import type { PlanLinkRow, VariantDetail } from "@/api/types"
import { ErrorState } from "@/components/ErrorState"
import { Kpi } from "@/components/Kpi"
import { PlanTree } from "@/components/PlanTree"
import { Badge } from "@/components/ui/badge"
import { Button, buttonVariants } from "@/components/ui/button"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import { Skeleton } from "@/components/ui/skeleton"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { formatKb, formatNumber, shortHash } from "@/lib/format"

const spread = (value: { min: number; median: number; max: number } | null, unit = "") =>
  value ? `${formatNumber(value.median)}${unit}` : "—"
const range = (value: { min: number; max: number } | null) => (value ? `${formatNumber(value.min)}–${formatNumber(value.max)}` : undefined)

function StoredPlan({ link, engine }: { link: PlanLinkRow; engine: string | null }) {
  const [open, setOpen] = useState(false)
  const distilled = useDistilledPlan(link.hash, open)
  return (
    <div className="space-y-2">
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-mono text-sm">{shortHash(link.hash)}</span>
        <a className={buttonVariants({ variant: "outline", size: "sm" })} href={`/api/plans/${link.hash}`} download>
          Download plan
        </a>
        <Button variant="ghost" size="sm" onClick={() => setOpen(!open)}>
          {open ? "Hide operators" : "Show operators"}
        </Button>
      </div>
      {open && (distilled.error ? <ErrorState error={distilled.error} /> : distilled.isPending
        ? <Skeleton className="h-24 w-full" />
        : <PlanTree plan={distilled.data} engine={engine} />)}
    </div>
  )
}

export function VariantPanel({ variant, engine }: { variant: VariantDetail; engine: string | null }) {
  const plans = [...new Map(variant.plans.map(link => [link.hash, link])).values()]
  return (
    <div className="space-y-4">
      <section aria-label="Key metrics" className="grid grid-cols-2 gap-4 md:grid-cols-4">
        <Kpi label="Elapsed (median)" value={spread(variant.elapsedMs, " ms")} hint={range(variant.elapsedMs)} />
        <Kpi label="CPU (median)" value={spread(variant.cpuMs, " ms")} hint={range(variant.cpuMs)} />
        <Kpi label="Logical reads (median)" value={spread(variant.logicalReads)} hint={range(variant.logicalReads)} />
        <Kpi label="Memory grant used / granted" value={`${formatKb(variant.grantMaxUsedKb)} / ${formatKb(variant.grantGrantedKb)}`} />
        <Kpi label="DOP" value={formatNumber(variant.dop)} />
        <Kpi label="Spills" value={String(variant.spillCount)} />
        <Kpi label="Compile" value={variant.compileTimeMs === null ? "—" : `${variant.compileTimeMs} ms`} />
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
            <CardTitle>Table IO (median per run)</CardTitle>
          </CardHeader>
          <CardContent className="overflow-x-auto">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Table</TableHead>
                  <TableHead className="text-right">Logical</TableHead>
                  <TableHead className="text-right">Scans</TableHead>
                  <TableHead className="text-right">Physical</TableHead>
                  <TableHead className="text-right">Read-ahead</TableHead>
                  <TableHead className="text-right">LOB logical</TableHead>
                  <TableHead className="text-right">Cold runs</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {variant.tableIo.map(row => (
                  <TableRow key={row.table}>
                    <TableCell className="font-mono">{row.table}</TableCell>
                    <TableCell className="text-right">{formatNumber(row.logicalReads)}</TableCell>
                    <TableCell className="text-right">{formatNumber(row.scanCount)}</TableCell>
                    <TableCell className="text-right">{formatNumber(row.physicalReads)}</TableCell>
                    <TableCell className="text-right">{formatNumber(row.readAheadReads)}</TableCell>
                    <TableCell className="text-right">{formatNumber(row.lobLogicalReads)}</TableCell>
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
      {variant.postgres && (
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
      {plans.length > 0 && (
        <Card>
          <CardHeader>
            <CardTitle>Plans</CardTitle>
          </CardHeader>
          <CardContent className="space-y-4">
            {plans.map(link =>
              link.stored ? (
                <StoredPlan key={link.hash} link={link} engine={engine} />
              ) : (
                <div key={link.hash} className="text-sm text-muted-foreground">
                  <span className="font-mono">{shortHash(link.hash)}</span> — plan not stored (journal.storeSensitive is off)
                </div>
              ),
            )}
          </CardContent>
        </Card>
      )}
    </div>
  )
}
