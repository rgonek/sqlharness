import type { StatementRow } from "@/api/types"
import { HiddenWorkFlag } from "./HiddenWorkFlag"
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from "@/components/ui/collapsible"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"

const statusLabels: Record<string, string> = {
  shared: "shared",
  "different-work": "different work",
  "only-baseline": "only in baseline",
  "only-candidate": "only in candidate",
  "not-executed": "not executed in this case",
  measured: "measured",
}

function number(value: number | null): string {
  return value === null ? "—" : value.toLocaleString()
}

function changeMagnitude(row: StatementRow, measured: boolean): number {
  if (row.readsBefore !== null && row.readsAfter !== null) return Math.abs(row.readsAfter - row.readsBefore)
  return measured ? Math.abs(row.readsAfter ?? 0) : -1
}

function Objects({ row, measured }: { row: StatementRow; measured: boolean }) {
  if (row.objects.length === 0) return null
  return <Collapsible className="mt-2">
    <CollapsibleTrigger className="cursor-pointer text-sm">Objects ({row.objects.length})</CollapsibleTrigger>
    <CollapsibleContent keepMounted><div className="mt-2">
      <Table aria-label={`${row.text} object IO`}>
        <TableHeader><TableRow><TableHead>Object</TableHead>{measured
          ? <><TableHead>Scans</TableHead><TableHead>Logical reads</TableHead></>
          : <><TableHead>Scans before</TableHead><TableHead>Scans after</TableHead><TableHead>Logical reads before</TableHead><TableHead>Logical reads after</TableHead><TableHead>Change</TableHead></>}</TableRow></TableHeader>
        <TableBody>{row.objects.map(item => <TableRow key={item.table}>
          <TableHead scope="row" className="whitespace-normal">{item.table}</TableHead>{measured
            ? <><TableCell>{number(item.scansAfter)}</TableCell><TableCell>{number(item.readsAfter)}</TableCell></>
            : <><TableCell>{number(item.scansBefore)}</TableCell><TableCell>{number(item.scansAfter)}</TableCell><TableCell>{number(item.readsBefore)}</TableCell><TableCell>{number(item.readsAfter)}</TableCell><TableCell className="whitespace-normal">{item.change}</TableCell></>}
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
  return <section aria-label="Statement IO" className="space-y-3">
    {postgres && <p className="text-sm text-muted-foreground">per-statement IO not available for this engine</p>}
    {ordered.length > 0 && <Table aria-label="Statement IO rows">
      <TableHeader><TableRow><TableHead>Statement</TableHead><TableHead>Status</TableHead>{measured
        ? <><TableHead>Executions</TableHead><TableHead>Scans</TableHead><TableHead>Logical reads</TableHead></>
        : <><TableHead>Executions</TableHead><TableHead>Reads before</TableHead><TableHead>Reads after</TableHead><TableHead>Change</TableHead></>}</TableRow></TableHeader>
      <TableBody>{ordered.map((row, index) => <TableRow key={`${row.key}:${row.occurrence}`} className="align-top">
        <TableCell className="whitespace-normal"><div className="max-w-2xl whitespace-pre-wrap break-words">{row.text}</div>
          <div className="mt-1 flex flex-wrap gap-2 text-xs">{!measured && <><PlanLink href={row.planLinkBefore} hash={row.planHashBefore} label="Plan before" /><PlanLink href={row.planLinkAfter} hash={row.planHashAfter} label="Plan after" /></>}
            {measured && <PlanLink href={row.planLinkAfter} hash={row.planHashAfter} label="Plan after" />}</div>
          {row.hiddenWork.length > 0 && <div className="mt-2 flex flex-wrap gap-1" aria-label={`Hidden work for statement ${index + 1}`}>
            {row.hiddenWork.map((item, itemIndex) => <HiddenWorkFlag key={`${item.kind}:${item.name}:${itemIndex}`} item={item} />)}
          </div>}
          <Objects row={row} measured={measured} />
        </TableCell>
        <TableCell className="whitespace-normal">{statusLabels[row.status] ?? row.status}</TableCell>
        <TableCell>{row.executions === null ? "—" : `${number(row.executions)} ${row.executions === 1 ? "execution" : "executions"}`}</TableCell>
        {measured ? <><TableCell>{number(row.scansAfter)}</TableCell><TableCell>{number(row.readsAfter)}</TableCell></>
          : <><TableCell>{number(row.readsBefore)}</TableCell><TableCell>{number(row.readsAfter)}</TableCell><TableCell className="whitespace-normal">{row.change}</TableCell></>}
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