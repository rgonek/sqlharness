import { Fragment, useState } from "react"
import { ChevronRight } from "lucide-react"
import type { OptimizationComparison } from "@/api/types"
import { Button } from "@/components/ui/button"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import type { IoExtraMetric, IoExtras } from "@/lib/ioComparison"
import { HiddenWorkFlag } from "./HiddenWorkFlag"

type IoRow = OptimizationComparison["tables"][number]
const number = (value: number | null) => value === null ? "—" : value.toLocaleString()

function Pair({ before, after, change }: { before: number | null; after: number | null; change?: string }) {
  return <div className="text-right tabular-nums"><div><span className="text-muted-foreground">{number(before)} → </span>{number(after)}</div>{change && <div className="text-xs text-muted-foreground">{change}</div>}</div>
}

export function IoComparison({ comparison, byStatement, onViewChange, extras }: { comparison: OptimizationComparison; byStatement: boolean; onViewChange: (view: "whole" | "statements") => void; extras?: IoExtras }) {
  const [scans, setScans] = useState(false)
  const [columns, setColumns] = useState<IoExtraMetric[]>([])
  const availableColumns: { key: IoExtraMetric; label: string }[] = [{ key: "physicalReads", label: "Physical" }, { key: "readAheadReads", label: "Read-ahead" }, { key: "lobLogicalReads", label: "LOB logical" }, { key: "lobPhysicalReads", label: "LOB physical" }, { key: "lobReadAheadReads", label: "LOB read-ahead" }]
  const extraCells = (table?: string) => columns.map(key => <TableCell key={key}><Pair before={!byStatement && table ? extras?.before[table]?.[key] ?? null : null} after={!byStatement && table ? extras?.after[table]?.[key] ?? null : null} /></TableCell>)
  const [closed, setClosed] = useState<string[]>([])
  const rows = comparison.statements ?? []
  const renderRow = (row: IoRow, key: string) => <TableRow key={key}><TableHead scope="row" className="whitespace-normal pl-8 font-mono text-xs">{row.table}</TableHead>{scans && <TableCell><Pair before={row.scansBefore} after={row.scansAfter} /></TableCell>}<TableCell><Pair before={row.readsBefore} after={row.readsAfter} change={row.change} /></TableCell>{extraCells(row.table)}</TableRow>
  return <section aria-label="IO comparison" className="space-y-3">
    <div className="flex flex-wrap items-center gap-2">
      <div className="flex rounded-lg bg-muted p-1"><Button size="sm" variant={byStatement ? "secondary" : "ghost"} aria-pressed={byStatement} onClick={() => onViewChange("statements")}>By statement</Button><Button size="sm" variant={byStatement ? "ghost" : "secondary"} aria-pressed={!byStatement} onClick={() => onViewChange("whole")}>Whole batch</Button></div>
      <span className="ml-2 text-xs text-muted-foreground">Columns</span><Button size="sm" variant={scans ? "secondary" : "outline"} aria-pressed={scans} onClick={() => setScans(!scans)}>Scans</Button>
      <details className="relative"><summary className="cursor-pointer rounded-md border px-2 py-1 text-xs">More columns</summary><div className="absolute left-0 z-10 mt-2 grid w-48 gap-2 rounded-lg border bg-popover p-3 shadow-md">{availableColumns.map(column => <label key={column.key} className="flex items-center gap-2 text-xs"><input type="checkbox" checked={columns.includes(column.key)} onChange={() => setColumns(current => current.includes(column.key) ? current.filter(item => item !== column.key) : [...current, column.key])} />{column.label}</label>)}</div></details>
      <span className="ml-auto text-xs text-muted-foreground">{comparison.a} → {comparison.b}</span>
    </div>
    {comparison.tableIoUnavailable ? <p className="text-sm text-muted-foreground">per-table IO not available for this engine</p> : <Table aria-label="IO before and after">
      <TableHeader><TableRow><TableHead>{byStatement ? "Statement / table" : "Table"}</TableHead>{scans && <TableHead className="text-right">Scans</TableHead>}<TableHead className="text-right">Logical reads</TableHead>{columns.map(key => <TableHead key={key} className="text-right">{availableColumns.find(column => column.key === key)?.label}</TableHead>)}</TableRow></TableHeader>
      <TableBody>{byStatement ? rows.map(row => {
        const key = `${row.key}:${row.occurrence}`
        const open = !closed.includes(key)
        return <Fragment key={key}><TableRow className="bg-muted/40"><TableHead scope="row" className="whitespace-normal">
          <button type="button" className="flex w-full items-start gap-2 text-left font-mono text-xs" aria-expanded={open} onClick={() => setClosed(current => open ? [...current, key] : current.filter(item => item !== key))}><ChevronRight className={`size-4 shrink-0 transition-transform ${open ? "rotate-90" : ""}`} /><span className="whitespace-pre-wrap break-words">{row.text}</span></button>
          <div className="mt-1 flex flex-wrap gap-2 pl-6 text-xs font-normal text-muted-foreground"><span>{row.status}</span><span>attribution: {row.attribution}</span>{row.planLinkBefore && <a href={row.planLinkBefore} className="underline">Plan before</a>}{row.planLinkAfter && <a href={row.planLinkAfter} className="underline">Plan after</a>}{row.hiddenWork.map((item, index) => <HiddenWorkFlag key={index} item={item} />)}</div>
        </TableHead>{scans && <TableCell><Pair before={row.scansBefore} after={row.scansAfter} /></TableCell>}<TableCell><Pair before={row.readsBefore} after={row.readsAfter} change={row.change} /></TableCell>{extraCells()}</TableRow>{open && row.objects.map((item, index) => renderRow(item, `${key}:${index}`))}</Fragment>
      }) : comparison.tables.map((row, index) => renderRow(row, `${row.table}:${index}`))}
        <TableRow className="bg-muted/40 font-medium"><TableHead scope="row">Total (whole batch)</TableHead>{scans && <TableCell className="text-right">—</TableCell>}<TableCell><Pair before={comparison.wholeBatch?.before ?? null} after={comparison.wholeBatch?.after ?? null} /></TableCell>{extraCells()}</TableRow>
      </TableBody>
    </Table>}
    {byStatement && columns.length > 0 && <p className="text-xs text-muted-foreground">Additional counters are retained for the whole batch; per-statement values are unavailable.</p>}
    {comparison.collapsed && <p className="text-xs text-muted-foreground">{comparison.collapsed.label}</p>}
    <p className="text-xs text-muted-foreground">IO from recorded STATISTICS IO / BUFFERS. Unavailable measurements are shown as —.</p>
  </section>
}
