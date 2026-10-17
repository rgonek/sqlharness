import type { PostgresBuffers, TableIoRow } from "@/api/types"
import type { ComparePair } from "@/lib/compare"
import { formatChange } from "@/lib/compare"
import { tableIoDelta, type TableIoDeltaRow } from "@/lib/tableIoDelta"
import { formatNumber } from "@/lib/format"
import { Badge } from "@/components/ui/badge"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"

function nullableTotal(rows: TableIoRow[], field: "physicalReads" | "readAheadReads" | "lobLogicalReads" | "lobPhysicalReads" | "lobReadAheadReads"): number | null {
  const values = rows.map(row => row[field]).filter((value): value is number => value !== null)
  return values.length === 0 ? null : values.reduce((total, value) => total + value, 0)
}

function MoreRows({ rows }: { rows: TableIoDeltaRow[] }) {
  const total = (side: "baseline" | "candidate", field: "physicalReads" | "readAheadReads" | "lobLogicalReads" | "lobPhysicalReads" | "lobReadAheadReads") =>
    nullableTotal(rows.flatMap(row => row[side] ? [row[side]!] : []), field)
  return <details className="mt-2 text-sm">
    <summary className="cursor-pointer text-muted-foreground">More</summary>
    <Table>
      <TableHeader><TableRow><TableHead>Table</TableHead><TableHead className="text-right">Physical B</TableHead><TableHead className="text-right">C</TableHead><TableHead className="text-right">Read-ahead B</TableHead><TableHead className="text-right">C</TableHead><TableHead className="text-right">LOB logical B</TableHead><TableHead className="text-right">C</TableHead><TableHead className="text-right">LOB physical B</TableHead><TableHead className="text-right">C</TableHead><TableHead className="text-right">LOB read-ahead B</TableHead><TableHead className="text-right">C</TableHead></TableRow></TableHeader>
      <TableBody>
        {rows.map(row => <TableRow key={row.table}><TableCell className="font-mono">{row.table}</TableCell>
          <TableCell className="text-right">{formatNumber(row.baseline?.physicalReads)}</TableCell><TableCell className="text-right">{formatNumber(row.candidate?.physicalReads)}</TableCell>
          <TableCell className="text-right">{formatNumber(row.baseline?.readAheadReads)}</TableCell><TableCell className="text-right">{formatNumber(row.candidate?.readAheadReads)}</TableCell>
          <TableCell className="text-right">{formatNumber(row.baseline?.lobLogicalReads)}</TableCell><TableCell className="text-right">{formatNumber(row.candidate?.lobLogicalReads)}</TableCell>
          <TableCell className="text-right">{formatNumber(row.baseline?.lobPhysicalReads)}</TableCell><TableCell className="text-right">{formatNumber(row.candidate?.lobPhysicalReads)}</TableCell>
          <TableCell className="text-right">{formatNumber(row.baseline?.lobReadAheadReads)}</TableCell><TableCell className="text-right">{formatNumber(row.candidate?.lobReadAheadReads)}</TableCell>
        </TableRow>)}
        <TableRow className="font-bold"><TableCell>Total</TableCell>
          <TableCell className="text-right">{formatNumber(total("baseline", "physicalReads"))}</TableCell><TableCell className="text-right">{formatNumber(total("candidate", "physicalReads"))}</TableCell>
          <TableCell className="text-right">{formatNumber(total("baseline", "readAheadReads"))}</TableCell><TableCell className="text-right">{formatNumber(total("candidate", "readAheadReads"))}</TableCell>
          <TableCell className="text-right">{formatNumber(total("baseline", "lobLogicalReads"))}</TableCell><TableCell className="text-right">{formatNumber(total("candidate", "lobLogicalReads"))}</TableCell>
          <TableCell className="text-right">{formatNumber(total("baseline", "lobPhysicalReads"))}</TableCell><TableCell className="text-right">{formatNumber(total("candidate", "lobPhysicalReads"))}</TableCell>
          <TableCell className="text-right">{formatNumber(total("baseline", "lobReadAheadReads"))}</TableCell><TableCell className="text-right">{formatNumber(total("candidate", "lobReadAheadReads"))}</TableCell>
        </TableRow>
      </TableBody>
    </Table>
  </details>
}

function SqlServerTable({ pair }: { pair: ComparePair }) {
  const comparison = tableIoDelta(pair.baseline.tableIo, pair.candidate.tableIo)
  if (comparison.rows.length === 0) return <p className="text-sm text-muted-foreground">No table IO recorded for this pair.</p>
  return <div className="space-y-2">
    <p className="text-sm text-muted-foreground">Headline: median of run totals (data + LOB)</p>
    <Table aria-label={`${pair.label} per-table IO`}>
      <TableHeader><TableRow><TableHead>Table</TableHead><TableHead className="text-right">Logical B</TableHead><TableHead className="text-right">C</TableHead><TableHead className="text-right">LOB logical B</TableHead><TableHead className="text-right">C</TableHead><TableHead className="text-right">Total B</TableHead><TableHead className="text-right">C</TableHead><TableHead className="text-right">Scans B</TableHead><TableHead className="text-right">C</TableHead><TableHead className="text-right">Change</TableHead></TableRow></TableHeader>
      <TableBody>
        {comparison.rows.map(row => <TableRow key={row.table}>
          <TableCell className="font-mono">{row.table} {row.presence !== "both" && <Badge variant="outline">only in {row.presence === "baseline-only" ? "baseline" : "candidate"}</Badge>}</TableCell>
          <TableCell className="text-right">{formatNumber(row.baseline?.logicalReads)}</TableCell><TableCell className="text-right">{formatNumber(row.candidate?.logicalReads)}</TableCell>
          <TableCell className="text-right">{formatNumber(row.baseline?.lobLogicalReads)}</TableCell><TableCell className="text-right">{formatNumber(row.candidate?.lobLogicalReads)}</TableCell>
          <TableCell className="text-right">{formatNumber(row.baseline?.totalLogicalReads)}</TableCell><TableCell className="text-right">{formatNumber(row.candidate?.totalLogicalReads)}</TableCell>
          <TableCell className="text-right">{formatNumber(row.baseline?.scans ?? row.baseline?.scanCount)}</TableCell><TableCell className="text-right">{formatNumber(row.candidate?.scans ?? row.candidate?.scanCount)}</TableCell>
          <TableCell className="text-right">{formatChange(row.logicalDelta)}</TableCell>
        </TableRow>)}
        <TableRow className="font-bold"><TableCell>Total</TableCell><TableCell className="text-right">{formatNumber(comparison.rows.reduce((total, row) => total + (row.baseline?.logicalReads ?? 0), 0))}</TableCell><TableCell className="text-right">{formatNumber(comparison.rows.reduce((total, row) => total + (row.candidate?.logicalReads ?? 0), 0))}</TableCell><TableCell className="text-right">{formatNumber(nullableTotal(comparison.rows.flatMap(row => row.baseline ? [row.baseline] : []), "lobLogicalReads"))}</TableCell><TableCell className="text-right">{formatNumber(nullableTotal(comparison.rows.flatMap(row => row.candidate ? [row.candidate] : []), "lobLogicalReads"))}</TableCell><TableCell className="text-right">{formatNumber(comparison.total.baseline)}</TableCell><TableCell className="text-right">{formatNumber(comparison.total.candidate)}</TableCell><TableCell className="text-right">{formatNumber(comparison.rows.reduce((total, row) => total + (row.baseline?.scans ?? row.baseline?.scanCount ?? 0), 0))}</TableCell><TableCell className="text-right">{formatNumber(comparison.rows.reduce((total, row) => total + (row.candidate?.scans ?? row.candidate?.scanCount ?? 0), 0))}</TableCell><TableCell className="text-right">{formatChange(comparison.total.delta)}</TableCell></TableRow>
      </TableBody>
    </Table>
    <MoreRows rows={comparison.rows} />
  </div>
}

const bufferFields: Array<{ label: string; key: keyof PostgresBuffers }> = [
  { label: "Shared hit", key: "sharedHit" },
  { label: "Shared read", key: "sharedRead" },
  { label: "Temp read", key: "tempRead" },
  { label: "Temp written", key: "tempWritten" },
]

function PostgresTable({ pair }: { pair: ComparePair }) {
  const baseline = pair.baseline.postgres
  const candidate = pair.candidate.postgres
  if (!baseline && !candidate) return <p className="text-sm text-muted-foreground">No PostgreSQL buffers recorded for this pair.</p>
  return <Table aria-label={`Buffers ${pair.label}`}>
    <TableHeader><TableRow><TableHead>Buffers</TableHead><TableHead className="text-right">Baseline</TableHead><TableHead className="text-right">Candidate</TableHead><TableHead className="text-right">Change</TableHead></TableRow></TableHeader>
    <TableBody>{bufferFields.map(field => {
      const b = baseline?.[field.key] ?? null
      const c = candidate?.[field.key] ?? null
      const delta = b === null || c === null || b === 0 ? null : (c - b) / b
      return <TableRow key={field.key}><TableCell>{field.label}</TableCell><TableCell className="text-right">{formatNumber(b)}</TableCell><TableCell className="text-right">{formatNumber(c)}</TableCell><TableCell className="text-right">{formatChange(delta)}</TableCell></TableRow>
    })}</TableBody>
  </Table>
}

export function TableIoComparison({ pairs, engine }: { pairs: ComparePair[]; engine: string | null }) {
  if (pairs.length === 0) return null
  const postgres = engine === "postgres"
  return <Card>
    <CardHeader><CardTitle>Table IO: baseline vs candidate</CardTitle><p className="text-sm text-muted-foreground">Median of run totals (data + LOB)</p></CardHeader>
    <CardContent className="space-y-4">
      {pairs.map(pair => <section key={pair.label} aria-label={`${pair.label} table IO`} className="space-y-2">
        {pairs.length > 1 && <h3 className="font-medium">{pair.label}</h3>}
        {postgres ? <PostgresTable pair={pair} /> : <SqlServerTable pair={pair} />}
      </section>)}
    </CardContent>
  </Card>
}
