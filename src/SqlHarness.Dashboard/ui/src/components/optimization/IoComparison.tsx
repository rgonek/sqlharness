import { ComparisonPending } from "./ComparisonPending"
import { SegmentedRadioGroup } from "@/components/SegmentedRadioGroup"
import { Checkbox } from "@/components/ui/checkbox"
import { Popover, PopoverTrigger, PopoverContent } from "@/components/ui/popover"
import { Field, FieldLabel, FieldSet, FieldLegend, FieldGroup } from "@/components/ui/field"
import { Fragment, useState } from "react"
import type { OptimizationComparison, ComparisonTableRow } from "@/api/types"
import { Button } from "@/components/ui/button"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import type { IoExtraMetric, IoExtras } from "@/lib/ioComparison"
import { HiddenWorkFlag } from "./HiddenWorkFlag"
import { MetricReasons, StatementMetrics, StatementAvailability } from "./StatementRows"
import { executionLabel } from "./statementLabels"
import { HugeiconsIcon } from "@hugeicons/react";
import { ArrowRight01Icon } from "@hugeicons/core-free-icons";

type OptionalMetric = IoExtraMetric | "dataLogicalReads"
type IoRow = NonNullable<OptimizationComparison["statements"]>[number]["objects"][number]
const number = (value: number | null) => value == null ? "—" : value.toLocaleString("en-US")

function Pair({ before, after, change }: { before: number | null; after: number | null; change?: string }) {
  return <div className="io-pair text-right tabular-nums"><div><span className="text-muted-foreground">{number(before)} → </span>{number(after)}</div>{change && <div className="text-xs text-muted-foreground" data-change={change}> {change}</div>}</div>
}

export function IoComparison({ pending = false, comparison, byStatement, onViewChange, extras }: { pending?: boolean; comparison: OptimizationComparison; byStatement: boolean; onViewChange: (view: "whole" | "statements") => void; extras?: IoExtras }) {
  const [scans, setScans] = useState(true)
  const [columns, setColumns] = useState<OptionalMetric[]>([])
  const availableColumns: { key: OptionalMetric; label: string }[] = [{ key: "dataLogicalReads", label: "Data logical" }, { key: "physicalReads", label: "Physical" }, { key: "pageServerReads", label: "Page server" }, { key: "readAheadReads", label: "Read-ahead" }, { key: "lobLogicalReads", label: "LOB logical" }, { key: "lobPhysicalReads", label: "LOB physical" }, { key: "lobReadAheadReads", label: "LOB read-ahead" }]
  const extraValue = (row: IoRow | ComparisonTableRow | undefined, key: OptionalMetric, side: "Before" | "After") => {
    const projected = row ? (row as unknown as Record<string, unknown>)[`${key}${side}`] : undefined
    if (typeof projected === "number") return projected
    if (projected === null || byStatement || !row || key === "dataLogicalReads") return null
    return extras?.[side === "Before" ? "before" : "after"][row.table]?.[key] ?? null
  }
  const supported = (key: OptionalMetric) => (byStatement ? (comparison.statements ?? []).flatMap(row => row.objects) : comparison.tables).some(row => extraValue(row, key, "Before") != null || extraValue(row, key, "After") != null)
  const visibleOptions = availableColumns.filter(column => (!byStatement || column.key === "lobLogicalReads") && supported(column.key))
  const visibleColumns = columns.filter(key => visibleOptions.some(option => option.key === key))
  const extraCells = (row?: IoRow | ComparisonTableRow) => visibleColumns.map(key => <TableCell key={key}><Pair before={extraValue(row, key, "Before")} after={extraValue(row, key, "After")} /></TableCell>)
  const [closed, setClosed] = useState<string[]>([])
  const rows = comparison.statements ?? []
  const scanRows = byStatement ? [...rows, ...rows.flatMap(row => row.objects)] : comparison.tables
  const scansAvailable = !comparison.tableIoUnavailable && scanRows.some(row => row.scansBefore != null || row.scansAfter != null)
  const showScans = scans && scansAvailable
  const renderRow = (row: IoRow | ComparisonTableRow, key: string) => <TableRow key={key}><TableHead scope="row" className="whitespace-normal pl-8 font-mono text-[13px]">{row.table}<div className="flex flex-col"><MetricReasons availability={"availability" in row ? row.availability : undefined} /></div></TableHead>{showScans && <TableCell><Pair before={row.scansBefore} after={row.scansAfter} /></TableCell>}<TableCell><Pair before={row.readsBefore} after={row.readsAfter} change={row.change} /></TableCell>{extraCells(row)}</TableRow>
  const hasStatementIo = rows.some(row => [row.readsBefore, row.readsAfter, row.scansBefore, row.scansAfter].some(value => value != null) || row.objects.some(object => [object.readsBefore, object.readsAfter, object.scansBefore, object.scansAfter, object.lobLogicalReadsBefore, object.lobLogicalReadsAfter].some(value => value != null)))
  return <section aria-label="IO comparison" className="io-comparison space-y-3">
    <div className="flex flex-wrap items-center gap-2">
      <span className="text-sm text-muted-foreground">basis: {comparison.verdictBasis ?? "whole batch"}</span>
      <SegmentedRadioGroup label="IO view" value={byStatement ? "statements" : "whole"} options={[{ value: "statements", label: "By statement" }, { value: "whole", label: "Whole batch" }]} onValueChange={value => { if (value === "statements" || value === "whole") onViewChange(value) }} />
      {(scansAvailable || visibleOptions.length > 0) && <span className="ml-2 text-xs text-muted-foreground">Columns</span>}{scansAvailable && <Button size="sm" variant={scans ? "secondary" : "outline"} aria-pressed={scans} onClick={() => setScans(!scans)}>Scans</Button>}
      {visibleOptions.length > 0 && (byStatement ? <Button size="sm" variant={columns.includes("lobLogicalReads") ? "secondary" : "outline"} aria-pressed={columns.includes("lobLogicalReads")} onClick={() => setColumns(current => current.includes("lobLogicalReads") ? current.filter(key => key !== "lobLogicalReads") : [...current, "lobLogicalReads"])}>LOB logical</Button> : <Popover><PopoverTrigger render={<Button variant="outline" size="sm" />}>More columns</PopoverTrigger><PopoverContent align="start"><FieldSet><FieldLegend>Columns</FieldLegend><FieldGroup>{visibleOptions.map(column => <Field key={column.key} orientation="horizontal"><FieldLabel><Checkbox checked={columns.includes(column.key)} onCheckedChange={() => setColumns(current => current.includes(column.key) ? current.filter(item => item !== column.key) : [...current, column.key])} />{column.label}</FieldLabel></Field>)}</FieldGroup></FieldSet></PopoverContent></Popover>)}
    </div>
    <ComparisonPending pending={pending} announce>
    {comparison.wholeBatch?.label && <p className="text-sm text-muted-foreground">{comparison.wholeBatch.label}</p>}
    {comparison.fallbackNote && <p className="text-sm text-muted-foreground">Some IO cannot be attributed to individual statements. IO assessment uses the whole batch.</p>}
    {comparison.tableIoUnavailable && <p className="text-sm text-muted-foreground">per-table IO not available for this engine</p>}
    {byStatement && !hasStatementIo ? <div className="space-y-3"><p>No trustworthy statement IO is available for this selection.</p>{!comparison.fallbackNote && <p className="text-sm text-muted-foreground">Some IO cannot be attributed to individual statements. IO assessment uses the whole batch.</p>}<Button variant="outline" onClick={() => onViewChange("whole")}>View whole batch IO</Button><div className="space-y-3">{rows.map(row => <section key={`${row.key}:${row.occurrence}`} className="space-y-1"><p className="whitespace-pre-wrap break-words text-sm">{row.text}</p><p className="text-sm text-muted-foreground">{row.status} · attribution: {row.attribution}</p>{row.objects.length > 0 && <p className="break-words text-sm">Objects: {row.objects.map(object => object.table).join(", ")}</p>}<StatementAvailability row={row} /><div className="flex flex-wrap gap-3 text-sm">{row.planLinkBefore && <a className="underline" href={row.planLinkBefore}>Plan before</a>}{row.planLinkAfter && <a className="underline" href={row.planLinkAfter}>Plan after</a>}</div>{executionLabel(row) && <span>Executions: {executionLabel(row)}</span>}{!comparison.statementMetricsUnavailableReason && <StatementMetrics row={row} />}{row.hiddenWork.map((item, index) => <HiddenWorkFlag key={index} item={item} />)}</section>)}</div></div> : comparison.tableIoUnavailable ? null : <Table className="io-table" aria-label="IO before and after">
      <TableHeader><TableRow><TableHead>{byStatement ? "Statement / table" : "Table"}</TableHead>{showScans && <TableHead className="text-right">Scans</TableHead>}<TableHead className="text-right">Logical reads</TableHead>{visibleColumns.map(key => <TableHead key={key} className="text-right">{availableColumns.find(column => column.key === key)?.label}</TableHead>)}</TableRow></TableHeader>
      <TableBody>{byStatement ? rows.map(row => {
        const key = `${row.key}:${row.occurrence}`
        const open = !closed.includes(key)
        return <Fragment key={key}><TableRow className="cursor-pointer bg-muted/40 hover:bg-muted has-aria-expanded:bg-muted/40 has-aria-expanded:hover:bg-muted" onClick={event => {
          if ((event.target as HTMLElement).closest("a,button,input,[role=button]")) return
          setClosed(current => open ? [...current, key] : current.filter(item => item !== key))
        }}><TableHead scope="row" className="whitespace-normal">
          <Button variant="ghost" className="io-statement-disclosure w-full justify-start" aria-expanded={open} onClick={() => setClosed(current => open ? [...current, key] : current.filter(item => item !== key))}><HugeiconsIcon icon={ArrowRight01Icon} strokeWidth={2} className={`shrink-0 transition-transform ${open ? "rotate-90" : ""}`} /><span className="truncate" title={row.text}>{row.text}</span></Button>
          <div className="mt-1 flex flex-wrap gap-2 pl-6 text-xs font-normal text-muted-foreground"><span>{row.status}</span><span>attribution: {row.attribution}</span>{row.planLinkBefore && <a href={row.planLinkBefore} className="underline hover:text-foreground focus-visible:outline-ring">Plan before</a>}{row.planLinkAfter && <a href={row.planLinkAfter} className="underline hover:text-foreground focus-visible:outline-ring">Plan after</a>}{row.hiddenWork.map((item, index) => <HiddenWorkFlag key={index} item={item} />)}</div>
          <StatementAvailability row={row} />
          {open && <div className="statement-metrics">{executionLabel(row) && <span>Executions: {executionLabel(row)}</span>}{!comparison.statementMetricsUnavailableReason && <StatementMetrics row={row} />}</div>}
        </TableHead>{showScans && <TableCell><Pair before={row.scansBefore} after={row.scansAfter} /></TableCell>}<TableCell><Pair before={row.readsBefore} after={row.readsAfter} change={row.change} /></TableCell>{extraCells()}</TableRow>{open && row.objects.map((item, index) => renderRow(item, `${key}:${index}`))}</Fragment>
      }) : comparison.tables.map((row, index) => renderRow(row, `${row.table}:${index}`))}
        <TableRow className="bg-muted/40 font-medium"><TableHead scope="row">Total (whole batch)</TableHead>{showScans && <TableCell className="text-right">—</TableCell>}<TableCell><Pair before={comparison.wholeBatch?.before ?? null} after={comparison.wholeBatch?.after ?? null} /></TableCell>{extraCells()}</TableRow>
      </TableBody>
    </Table>}
    {byStatement && comparison.statementMetricsUnavailableReason && <p className="text-xs text-muted-foreground">{comparison.statementMetricsUnavailableReason}</p>}
    {comparison.collapsed && <p className="text-xs text-muted-foreground">{comparison.collapsed.label}</p>}
    <p className="text-xs text-muted-foreground">IO from recorded STATISTICS IO / BUFFERS. Unavailable measurements are shown as —.</p></ComparisonPending>
  </section>
}
