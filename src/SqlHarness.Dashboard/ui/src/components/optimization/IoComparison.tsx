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
import { executionLabel, MetricReasons, StatementMetrics } from "./StatementRows"
import { HugeiconsIcon } from "@hugeicons/react";
import { ArrowRight01Icon } from "@hugeicons/core-free-icons";

type IoRow = NonNullable<OptimizationComparison["statements"]>[number]["objects"][number]
const number = (value: number | null) => value == null ? "—" : value.toLocaleString("en-US")

function Pair({ before, after, change }: { before: number | null; after: number | null; change?: string }) {
  return <div className="io-pair text-right tabular-nums"><div><span className="text-muted-foreground">{number(before)} → </span>{number(after)}</div>{change && <div className="text-xs text-muted-foreground" data-change={change}> {change}</div>}</div>
}

export function IoComparison({ pending = false, comparison, byStatement, onViewChange, extras }: { pending?: boolean; comparison: OptimizationComparison; byStatement: boolean; onViewChange: (view: "whole" | "statements") => void; extras?: IoExtras }) {
  const [scans, setScans] = useState(true)
  const [columns, setColumns] = useState<IoExtraMetric[]>([])
  const availableColumns: { key: IoExtraMetric; label: string }[] = [{ key: "physicalReads", label: "Physical" }, { key: "pageServerReads", label: "Page server" }, { key: "readAheadReads", label: "Read-ahead" }, { key: "lobLogicalReads", label: "LOB logical" }, { key: "lobPhysicalReads", label: "LOB physical" }, { key: "lobReadAheadReads", label: "LOB read-ahead" }]
  const value = (row: IoRow | undefined, key: IoExtraMetric, side: "Before" | "After") => {
    if (!row) return null
    const property = `${key}${side}` as keyof IoRow
    if (Object.hasOwn(row, property)) {
      const stored = row[property]
      return typeof stored === "number" ? stored : null
    }
    return !byStatement ? extras?.[side === "Before" ? "before" : "after"][row.table]?.[key] ?? null : null
  }
  const candidates = byStatement ? (comparison.statements ?? []).flatMap(row => row.objects) : comparison.tables
  const visibleOptions = availableColumns.filter(column => (!byStatement || column.key === "lobLogicalReads") && candidates.some(row => value(row, column.key, "Before") != null || value(row, column.key, "After") != null))
  const visibleColumns = columns.filter(key => visibleOptions.some(column => column.key === key))
  const [previousView, setPreviousView] = useState(byStatement)
  if (previousView !== byStatement) {
    setPreviousView(byStatement)
    if (byStatement) setColumns(current => current.filter(key => key === "lobLogicalReads"))
  }
  const extraCells = (_table?: string, object?: IoRow) => visibleColumns.map(key => <TableCell key={key}><Pair before={value(object, key, "Before")} after={value(object, key, "After")} /></TableCell>)
  const [closed, setClosed] = useState<string[]>([])
  const rows = comparison.statements ?? []
  const hasReads = rows.some(row => row.readsBefore != null || row.readsAfter != null || row.objects.some(item => item.readsBefore != null || item.readsAfter != null))
  const hasScans = rows.some(row => row.scansBefore != null || row.scansAfter != null || row.objects.some(item => item.scansBefore != null || item.scansAfter != null))
  const hasIo = hasReads || hasScans
  const scansAvailable = byStatement ? hasScans : comparison.tables.some(row => row.scansBefore != null || row.scansAfter != null)
  const showScans = scans && scansAvailable
  const showReads = !byStatement || hasReads
  const renderRow = (row: IoRow, key: string) => <TableRow key={key}><TableHead scope="row" className="whitespace-normal pl-8 font-mono text-[13px]">{row.table}<div className="flex flex-col"><MetricReasons availability={row.availability} /></div></TableHead>{showScans && <TableCell><Pair before={row.scansBefore} after={row.scansAfter} /></TableCell>}{showReads && <TableCell><Pair before={row.readsBefore} after={row.readsAfter} change={row.change} /></TableCell>}{extraCells(row.table, row)}</TableRow>
  return <section aria-label="IO comparison" className="io-comparison space-y-3">
    <div className="flex flex-wrap items-center gap-2">
      <SegmentedRadioGroup label="IO view" value={byStatement ? "statements" : "whole"} options={[{ value: "statements", label: "By statement" }, { value: "whole", label: "Whole batch" }]} onValueChange={value => { if (value === "statements" || value === "whole") onViewChange(value) }} />
      <span className="ml-2 text-xs text-muted-foreground">Columns</span>{scansAvailable && <Button size="sm" variant={scans ? "secondary" : "outline"} aria-pressed={scans} onClick={() => setScans(!scans)}>Scans</Button>}
      {visibleOptions.length === 0 ? null : visibleOptions.length === 1 && visibleOptions[0].key === "lobLogicalReads" ? <Button size="sm" variant={columns.includes("lobLogicalReads") ? "secondary" : "outline"} aria-pressed={columns.includes("lobLogicalReads")} onClick={() => setColumns(current => current.includes("lobLogicalReads") ? current.filter(key => key !== "lobLogicalReads") : [...current, "lobLogicalReads"])}>LOB logical</Button> : <Popover><PopoverTrigger render={<Button variant="outline" size="sm" />}>More columns</PopoverTrigger><PopoverContent align="start"><FieldSet><FieldLegend>Columns</FieldLegend><FieldGroup>{visibleOptions.map(column => <Field key={column.key} orientation="horizontal"><FieldLabel><Checkbox checked={columns.includes(column.key)} onCheckedChange={() => setColumns(current => current.includes(column.key) ? current.filter(item => item !== column.key) : [...current, column.key])} />{column.label}</FieldLabel></Field>)}</FieldGroup></FieldSet></PopoverContent></Popover>}
    </div>
    <ComparisonPending pending={pending} announce>{byStatement && !hasIo ? <div className="space-y-2">
      <p className="text-sm text-muted-foreground">{comparison.tableIoUnavailable ? "Per-statement IO is not available for this engine." : "Some IO cannot be attributed to individual statements. IO assessment uses the whole batch."}</p>
      {comparison.tableIoUnavailable && <p className="text-sm text-muted-foreground">per-table IO not available for this engine</p>}
      <Button variant="outline" size="sm" onClick={() => onViewChange("whole")}>View whole batch</Button>
      {rows.map(row => <div key={`${row.key}:${row.occurrence}`} className="text-sm"><span>{row.text}</span><MetricReasons availability={row.availability} />{row.planLinkBefore && <> · <a href={row.planLinkBefore} className="underline">Plan before</a></>}{row.planLinkAfter && <> · <a href={row.planLinkAfter} className="underline">Plan after</a></>}{executionLabel(row) && <span>Executions: {executionLabel(row)}</span>}{!comparison.statementMetricsUnavailableReason && <StatementMetrics row={row} />}{row.hiddenWork.map((item, index) => <HiddenWorkFlag key={index} item={item} />)}</div>)}
    </div> : comparison.tableIoUnavailable ? <p className="text-sm text-muted-foreground">per-table IO not available for this engine</p> : <Table className="io-table" aria-label="IO before and after">
      <TableHeader><TableRow><TableHead>{byStatement ? "Statement / table" : "Table"}</TableHead>{showScans && <TableHead className="text-right">Scans</TableHead>}{showReads && <TableHead className="text-right">Logical reads</TableHead>}{visibleColumns.map(key => <TableHead key={key} className="text-right">{availableColumns.find(column => column.key === key)?.label}</TableHead>)}</TableRow></TableHeader>
      <TableBody>{byStatement ? rows.map(row => {
        const key = `${row.key}:${row.occurrence}`
        const open = !closed.includes(key)
        return <Fragment key={key}><TableRow className="cursor-pointer bg-muted/40 hover:bg-muted has-aria-expanded:bg-muted/40 has-aria-expanded:hover:bg-muted" onClick={event => {
          if ((event.target as HTMLElement).closest("a,button,input,[role=button]")) return
          setClosed(current => open ? [...current, key] : current.filter(item => item !== key))
        }}><TableHead scope="row" className="whitespace-normal">
          <Button variant="ghost" className="io-statement-disclosure w-full justify-start" aria-expanded={open} onClick={() => setClosed(current => open ? [...current, key] : current.filter(item => item !== key))}><HugeiconsIcon icon={ArrowRight01Icon} strokeWidth={2} className={`shrink-0 transition-transform ${open ? "rotate-90" : ""}`} /><span className="truncate" title={row.text}>{row.text}</span></Button>
          <div className="mt-1 flex flex-wrap gap-2 pl-6 text-xs font-normal text-muted-foreground"><span>{row.status}</span><span>attribution: {row.attribution}</span><MetricReasons availability={row.availability} />{row.planLinkBefore && <a href={row.planLinkBefore} className="underline hover:text-foreground focus-visible:outline-ring">Plan before</a>}{row.planLinkAfter && <a href={row.planLinkAfter} className="underline hover:text-foreground focus-visible:outline-ring">Plan after</a>}{row.hiddenWork.map((item, index) => <HiddenWorkFlag key={index} item={item} />)}</div>
          {open && <div className="statement-metrics">{executionLabel(row) && <span>Executions: {executionLabel(row)}</span>}{!comparison.statementMetricsUnavailableReason && <StatementMetrics row={row} />}</div>}
        </TableHead>{showScans && <TableCell><Pair before={row.scansBefore} after={row.scansAfter} /></TableCell>}{showReads && <TableCell><Pair before={row.readsBefore} after={row.readsAfter} change={row.change} /></TableCell>}{extraCells()}</TableRow>{open && row.objects.map((item, index) => renderRow(item, `${key}:${index}`))}</Fragment>
      }) : comparison.tables.map((row, index) => renderRow(row, `${row.table}:${index}`))}
        <TableRow className="bg-muted/40 font-medium"><TableHead scope="row">Total (whole batch)</TableHead>{showScans && <TableCell className="text-right">—</TableCell>}{showReads && <TableCell><Pair before={comparison.wholeBatch?.before ?? null} after={comparison.wholeBatch?.after ?? null} /></TableCell>}{extraCells()}</TableRow>
      </TableBody>
    </Table>}
    {byStatement && comparison.statementMetricsUnavailableReason && <p className="text-xs text-muted-foreground">{comparison.statementMetricsUnavailableReason}</p>}
    {comparison.collapsed && <p className="text-xs text-muted-foreground">{comparison.collapsed.label}</p>}
    <p className="text-xs text-muted-foreground">IO from recorded STATISTICS IO / BUFFERS. Unavailable measurements are shown as —.</p></ComparisonPending>
  </section>
}
