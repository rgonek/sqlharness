import { ComparisonPending } from "./ComparisonPending"
import { SegmentedRadioGroup } from "@/components/SegmentedRadioGroup"
import { Checkbox } from "@/components/ui/checkbox"
import { Popover, PopoverTrigger, PopoverContent } from "@/components/ui/popover"
import { Field, FieldLabel, FieldSet, FieldLegend, FieldGroup } from "@/components/ui/field"
import { Fragment, useState } from "react"
import type { OptimizationComparison } from "@/api/types"
import { Button } from "@/components/ui/button"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import type { IoExtraMetric, IoExtras } from "@/lib/ioComparison"
import { HiddenWorkFlag } from "./HiddenWorkFlag"
import { HugeiconsIcon } from "@hugeicons/react";
import { ArrowRight01Icon } from "@hugeicons/core-free-icons";

type IoRow = OptimizationComparison["tables"][number]
const number = (value: number | null) => value == null ? "—" : value.toLocaleString("en-US")

function Pair({ before, after, change }: { before: number | null; after: number | null; change?: string }) {
  return <div className="io-pair text-right tabular-nums"><div><span className="text-muted-foreground">{number(before)} → </span>{number(after)}</div>{change && <div className="text-xs text-muted-foreground" data-change={change}> {change}</div>}</div>
}

export function IoComparison({ pending = false, comparison, byStatement, onViewChange, extras }: { pending?: boolean; comparison: OptimizationComparison; byStatement: boolean; onViewChange: (view: "whole" | "statements") => void; extras?: IoExtras }) {
  const [scans, setScans] = useState(true)
  const [columns, setColumns] = useState<IoExtraMetric[]>([])
  const availableColumns: { key: IoExtraMetric; label: string }[] = [{ key: "physicalReads", label: "Physical" }, { key: "readAheadReads", label: "Read-ahead" }, { key: "lobLogicalReads", label: "LOB logical" }, { key: "lobPhysicalReads", label: "LOB physical" }, { key: "lobReadAheadReads", label: "LOB read-ahead" }]
  const extraCells = (table?: string) => columns.map(key => <TableCell key={key}><Pair before={!byStatement && table ? extras?.before[table]?.[key] ?? null : null} after={!byStatement && table ? extras?.after[table]?.[key] ?? null : null} /></TableCell>)
  const [closed, setClosed] = useState<string[]>([])
  const rows = comparison.statements ?? []
  const renderRow = (row: IoRow, key: string) => <TableRow key={key}><TableHead scope="row" className="whitespace-normal pl-8 font-mono text-[13px]">{row.table}</TableHead>{scans && <TableCell><Pair before={row.scansBefore} after={row.scansAfter} /></TableCell>}<TableCell><Pair before={row.readsBefore} after={row.readsAfter} change={row.change} /></TableCell>{extraCells(row.table)}</TableRow>
  return <section aria-label="IO comparison" className="io-comparison space-y-3">
    <div className="flex flex-wrap items-center gap-2">
      <SegmentedRadioGroup label="IO view" value={byStatement ? "statements" : "whole"} options={[{ value: "statements", label: "By statement" }, { value: "whole", label: "Whole batch" }]} onValueChange={value => { if (value === "statements" || value === "whole") onViewChange(value) }} />
      <span className="ml-2 text-xs text-muted-foreground">Columns</span><Button size="sm" variant={scans ? "secondary" : "outline"} aria-pressed={scans} onClick={() => setScans(!scans)}>Scans</Button>
      <Popover><PopoverTrigger render={<Button variant="outline" size="sm" />}>More columns</PopoverTrigger><PopoverContent align="start"><FieldSet><FieldLegend>Columns</FieldLegend><FieldGroup>{availableColumns.map(column => <Field key={column.key} orientation="horizontal"><FieldLabel><Checkbox checked={columns.includes(column.key)} onCheckedChange={() => setColumns(current => current.includes(column.key) ? current.filter(item => item !== column.key) : [...current, column.key])} />{column.label}</FieldLabel></Field>)}</FieldGroup></FieldSet></PopoverContent></Popover>
      <span className="ml-auto text-xs text-muted-foreground">{comparison.a} → {comparison.b}</span>
    </div>
    <ComparisonPending pending={pending} announce>{comparison.tableIoUnavailable ? <p className="text-sm text-muted-foreground">per-table IO not available for this engine</p> : <Table className="io-table" aria-label="IO before and after">
      <TableHeader><TableRow><TableHead>{byStatement ? "Statement / table" : "Table"}</TableHead>{scans && <TableHead className="text-right">Scans</TableHead>}<TableHead className="text-right">Logical reads</TableHead>{columns.map(key => <TableHead key={key} className="text-right">{availableColumns.find(column => column.key === key)?.label}</TableHead>)}</TableRow></TableHeader>
      <TableBody>{byStatement ? rows.map(row => {
        const key = `${row.key}:${row.occurrence}`
        const open = !closed.includes(key)
        return <Fragment key={key}><TableRow className="bg-muted/40"><TableHead scope="row" className="whitespace-normal">
          <Button variant="ghost" className="w-full justify-start" aria-expanded={open} onClick={() => setClosed(current => open ? [...current, key] : current.filter(item => item !== key))}><HugeiconsIcon icon={ArrowRight01Icon} strokeWidth={2} className={`shrink-0 transition-transform ${open ? "rotate-90" : ""}`} /><span className="truncate" title={row.text}>{row.text}</span></Button>
          <div className="mt-1 flex flex-wrap gap-2 pl-6 text-xs font-normal text-muted-foreground"><span>{row.status}</span><span>attribution: {row.attribution}</span>{row.planLinkBefore && <a href={row.planLinkBefore} className="underline hover:text-foreground focus-visible:outline-ring">Plan before</a>}{row.planLinkAfter && <a href={row.planLinkAfter} className="underline hover:text-foreground focus-visible:outline-ring">Plan after</a>}{row.hiddenWork.map((item, index) => <HiddenWorkFlag key={index} item={item} />)}</div>
          {open && <div className="statement-metrics"><span>Executions <strong>{number(row.executions)}</strong></span><span>CPU <strong>—</strong></span><span>Elapsed <strong>—</strong></span><span>Memory grant <strong>—</strong></span><span>Max used <strong>—</strong></span><span>Spills <strong>—</strong></span><span title="CPU and elapsed are not exposed by this comparison; per-statement memory and spills are unavailable.">Statement metrics unavailable</span></div>}
        </TableHead>{scans && <TableCell><Pair before={row.scansBefore} after={row.scansAfter} /></TableCell>}<TableCell><Pair before={row.readsBefore} after={row.readsAfter} change={row.change} /></TableCell>{extraCells()}</TableRow>{open && row.objects.map((item, index) => renderRow(item, `${key}:${index}`))}</Fragment>
      }) : comparison.tables.map((row, index) => renderRow(row, `${row.table}:${index}`))}
        <TableRow className="bg-muted/40 font-medium"><TableHead scope="row">Total (whole batch)</TableHead>{scans && <TableCell className="text-right">—</TableCell>}<TableCell><Pair before={comparison.wholeBatch?.before ?? null} after={comparison.wholeBatch?.after ?? null} /></TableCell>{extraCells()}</TableRow>
      </TableBody>
    </Table>}
    {byStatement && columns.length > 0 && <p className="text-xs text-muted-foreground">Additional counters are retained for the whole batch; per-statement values are unavailable.</p>}
    {comparison.collapsed && <p className="text-xs text-muted-foreground">{comparison.collapsed.label}</p>}
    <p className="text-xs text-muted-foreground">IO from recorded STATISTICS IO / BUFFERS. Unavailable measurements are shown as —.</p></ComparisonPending>
  </section>
}
