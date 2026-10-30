import type { MetricAvailabilityPair, StatementRow } from "@/api/types"
import { HiddenWorkFlag } from "./HiddenWorkFlag"
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from "@/components/ui/collapsible"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"

const statusLabels: Record<string, string> = {
  shared: "shared",
  "different-work": "different work",
  "only-baseline": "only in baseline",
  "only-candidate": "only in candidate",
  "not-executed": "not executed in this case",
  unmatched: "unmatched or omitted",
  measured: "measured",
}

function number(value: number | null): string {
  return value === null ? "—" : value.toLocaleString()
}

function changeMagnitude(row: StatementRow, measured: boolean): number {
  if (row.readsBefore !== null && row.readsAfter !== null) return Math.abs(row.readsAfter - row.readsBefore)
  return measured ? Math.abs(row.readsAfter ?? 0) : -1
}

export function MetricReasons({ availability, measured = false }: { availability?: Record<string, MetricAvailabilityPair> | null; measured?: boolean }) {
  if (!availability) return null
  const groups = new Map<string, string[]>()
  for (const [metric, pair] of Object.entries(availability)) {
    const sides = measured ? [["after", pair.after] as const] : [["before", pair.before] as const, ["after", pair.after] as const]
    for (const [side, item] of sides) {
      if (item.status !== "unavailable") continue
      const explanation = `${item.reason} (${item.availableCases}/${item.totalCases} cases)`
      groups.set(explanation, [...(groups.get(explanation) ?? []), `${metric} ${side}`])
    }
  }
  return <>{[...groups].map(([reason, metrics]) => <span key={reason} className="text-xs text-muted-foreground">{metrics.join(", ")}: {reason}</span>)}</>
}

export function StatementMetrics({ row, measured = false }: { row: StatementRow; measured?: boolean }) {
  const values = [row.cpuBeforeMs, row.cpuAfterMs, row.elapsedBeforeMs, row.elapsedAfterMs, row.grantRequestedKbBefore, row.grantRequestedKbAfter, row.grantGrantedKbBefore, row.grantGrantedKbAfter, row.grantMaxUsedKbBefore, row.grantMaxUsedKbAfter, row.hasSpillWarningBefore, row.hasSpillWarningAfter]
  if (values.every(value => value == null)) return <span>Additional statement metrics unavailable</span>
  const pair = (before: number | null | undefined, after: number | null | undefined) => measured ? number(after ?? null) : `${number(before ?? null)} → ${number(after ?? null)}`
  const spill = (value: boolean | null | undefined) => value == null ? "—" : value ? "Yes" : "No"
  const spillPair = measured ? spill(row.hasSpillWarningAfter) : `${spill(row.hasSpillWarningBefore)} → ${spill(row.hasSpillWarningAfter)}`
  return <div className="statement-metrics"><span>CPU (ms): {pair(row.cpuBeforeMs, row.cpuAfterMs)}</span><span>Elapsed (ms): {pair(row.elapsedBeforeMs, row.elapsedAfterMs)}</span><span>Requested grant (KB): {pair(row.grantRequestedKbBefore, row.grantRequestedKbAfter)}</span><span>Granted grant (KB): {pair(row.grantGrantedKbBefore, row.grantGrantedKbAfter)}</span><span>Max used grant (KB): {pair(row.grantMaxUsedKbBefore, row.grantMaxUsedKbAfter)}</span><span>Spill warning: {spillPair}</span><span>Showplan; run selected by median total logical reads{row.planRepetitionBefore != null && !measured ? `; before repetition ${row.planRepetitionBefore}` : ""}{row.planRepetitionAfter != null ? `; after repetition ${row.planRepetitionAfter}` : ""}</span></div>
}

function Objects({ row, measured }: { row: StatementRow; measured: boolean }) {
  if (row.objects.length === 0) return null
  const hasLob = row.objects.some(item => item.lobLogicalReadsBefore != null || item.lobLogicalReadsAfter != null)
  const hasReadsBefore = row.objects.some(item => item.readsBefore != null)
  const hasReadsAfter = row.objects.some(item => item.readsAfter != null)
  const hasScansBefore = row.objects.some(item => item.scansBefore != null)
  const hasScansAfter = row.objects.some(item => item.scansAfter != null)
  return <Collapsible className="mt-2">
    <CollapsibleTrigger className="cursor-pointer text-sm">Objects ({row.objects.length})</CollapsibleTrigger>
    <CollapsibleContent keepMounted><div className="mt-2">
      <Table aria-label={`${row.text} object IO`}>
        <TableHeader><TableRow><TableHead>Object</TableHead>{measured
          ? <>{hasScansAfter && <TableHead>Scans</TableHead>}{hasReadsAfter && <TableHead>Logical reads</TableHead>}</>
          : <>{hasScansBefore && <TableHead>Scans before</TableHead>}{hasScansAfter && <TableHead>Scans after</TableHead>}{hasReadsBefore && <TableHead>Logical reads before</TableHead>}{hasReadsAfter && <TableHead>Logical reads after</TableHead>}{hasReadsBefore && hasReadsAfter && <TableHead>Change</TableHead>}</>}{hasLob && (measured ? <TableHead>LOB logical</TableHead> : <><TableHead>LOB logical before</TableHead><TableHead>LOB logical after</TableHead></>)}</TableRow></TableHeader>
        <TableBody>{row.objects.map(item => <TableRow key={item.table}>
          <TableHead scope="row" className="whitespace-normal">{item.table}<div className="flex flex-col"><MetricReasons availability={item.availability} measured={measured} /></div></TableHead>{measured
            ? <>{hasScansAfter && <TableCell>{number(item.scansAfter)}</TableCell>}{hasReadsAfter && <TableCell>{number(item.readsAfter)}</TableCell>}</>
            : <>{hasScansBefore && <TableCell>{number(item.scansBefore)}</TableCell>}{hasScansAfter && <TableCell>{number(item.scansAfter)}</TableCell>}{hasReadsBefore && <TableCell>{number(item.readsBefore)}</TableCell>}{hasReadsAfter && <TableCell>{number(item.readsAfter)}</TableCell>}{hasReadsBefore && hasReadsAfter && <TableCell className="whitespace-normal">{item.change}</TableCell>}</>}{hasLob && (measured ? <TableCell>{number(item.lobLogicalReadsAfter ?? null)}</TableCell> : <><TableCell>{number(item.lobLogicalReadsBefore ?? null)}</TableCell><TableCell>{number(item.lobLogicalReadsAfter ?? null)}</TableCell></>)}
        </TableRow>)}</TableBody>
      </Table>
    </div></CollapsibleContent>
  </Collapsible>
}

function PlanLink({ href, hash, label }: { href: string | null; hash: string | null; label: string }) {
  if (href) return <a className="underline" href={href}>{label}</a>
  return hash ? <span className="text-muted-foreground">Plan unavailable</span> : null
}

export function StatementRows({ rows, measured = false, postgres = false }: { rows: StatementRow[]; measured?: boolean; postgres?: boolean }) {
  const ordered = [...rows].sort((left, right) => changeMagnitude(right, measured) - changeMagnitude(left, measured)
    || left.text.localeCompare(right.text) || left.occurrence - right.occurrence)
  const hasIo = ordered.some(row => row.readsBefore != null || row.readsAfter != null || row.scansBefore != null || row.scansAfter != null)
  const hasReadsBefore = ordered.some(row => row.readsBefore != null)
  const hasReadsAfter = ordered.some(row => row.readsAfter != null)
  const hasScansAfter = ordered.some(row => row.scansAfter != null)
  const hasExecutions = ordered.some(row => row.executions != null)
  if (ordered.length > 0 && !hasIo) return <section aria-label="Statement IO" className="space-y-3">
    <p className="text-sm text-muted-foreground">Some IO cannot be attributed to individual statements. IO assessment uses the whole batch.</p>
    {ordered.map(row => <div key={`${row.key}:${row.occurrence}`}><div>{row.text}</div><PlanLink href={row.planLinkBefore} hash={row.planHashBefore} label="Plan before" /> <PlanLink href={row.planLinkAfter} hash={row.planHashAfter} label="Plan after" /><MetricReasons availability={row.availability} measured={measured} /><StatementMetrics row={row} measured={measured} />{row.objects.map((item, index) => <div key={index}>{item.table} <MetricReasons availability={item.availability} measured={measured} /></div>)}</div>)}
  </section>
  return <section aria-label="Statement IO" className="space-y-3">
    {postgres && <p className="text-sm text-muted-foreground">per-statement IO not available for this engine</p>}
    {ordered.length > 0 && <Table aria-label="Statement IO rows">
      <TableHeader><TableRow><TableHead>Statement</TableHead><TableHead>Status</TableHead>{measured
        ? <>{hasExecutions && <TableHead>Executions</TableHead>}{hasScansAfter && <TableHead>Scans</TableHead>}{hasReadsAfter && <TableHead>Logical reads</TableHead>}</>
        : <>{hasExecutions && <TableHead>Executions</TableHead>}{hasReadsBefore && <TableHead>Reads before</TableHead>}{hasReadsAfter && <TableHead>Reads after</TableHead>}{hasReadsBefore && hasReadsAfter && <TableHead>Change</TableHead>}</>}</TableRow></TableHeader>
      <TableBody>{ordered.map((row, index) => <TableRow key={`${row.key}:${row.occurrence}`} className="align-top">
        <TableCell className="whitespace-normal"><div className="max-w-2xl whitespace-pre-wrap break-words">{row.text}</div>
          <div className="mt-1 flex flex-wrap gap-2 text-xs">{!measured && <><PlanLink href={row.planLinkBefore} hash={row.planHashBefore} label="Plan before" /><PlanLink href={row.planLinkAfter} hash={row.planHashAfter} label="Plan after" /></>}
            {measured && <PlanLink href={row.planLinkAfter} hash={row.planHashAfter} label="Plan after" />}</div>
          {row.hiddenWork.length > 0 && <div className="mt-2 flex flex-wrap gap-1" aria-label={`Hidden work for statement ${index + 1}`}>
            {row.hiddenWork.map((item, itemIndex) => <HiddenWorkFlag key={`${item.kind}:${item.name}:${itemIndex}`} item={item} />)}
          </div>}
          <StatementMetrics row={row} measured={measured} />
          <Objects row={row} measured={measured} />
        </TableCell>
        <TableCell className="whitespace-normal">{statusLabels[row.status] ?? row.status}<div className="flex flex-col"><MetricReasons availability={row.availability} measured={measured} /></div></TableCell>
        {hasExecutions && <TableCell>{row.executions === null ? "—" : `${number(row.executions)} ${row.executions === 1 ? "execution" : "executions"}`}</TableCell>}
        {measured ? <>{hasScansAfter && <TableCell>{number(row.scansAfter)}</TableCell>}{hasReadsAfter && <TableCell>{number(row.readsAfter)}</TableCell>}</>
          : <>{hasReadsBefore && <TableCell>{number(row.readsBefore)}</TableCell>}{hasReadsAfter && <TableCell>{number(row.readsAfter)}</TableCell>}{hasReadsBefore && hasReadsAfter && <TableCell className="whitespace-normal">{row.change}</TableCell>}</>}
      </TableRow>)}</TableBody>
    </Table>}
  </section>
}

export function StatementHiddenWork({ rows }: { rows: StatementRow[] }) {
  const flags = rows.flatMap(row => row.hiddenWork)
  const unique = [...new Map(flags.map(item => [`${item.kind}:${item.name}:${item.executions}:${item.sharePercent}`, item])).values()]
  return unique.length > 0 ? <div className="flex flex-wrap gap-1" aria-label="Hidden work in statements">
    {unique.map((item, index) => <HiddenWorkFlag key={`${item.kind}:${item.name}:${index}`} item={item} />)}
  </div> : null
}
