import type { StatementRow } from "@/api/types"
import { HiddenWorkFlag } from "./HiddenWorkFlag"

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
  return <details className="mt-2">
    <summary className="cursor-pointer text-sm">Objects ({row.objects.length})</summary>
    <div className="mt-2 overflow-x-auto">
      <table className="w-full text-sm" aria-label={`${row.text} object IO`}>
        <thead><tr className="border-b text-left"><th className="py-1 pr-3">Object</th>{measured
          ? <><th className="px-2 py-1">Scans</th><th className="px-2 py-1">Logical reads</th></>
          : <><th className="px-2 py-1">Scans before</th><th className="px-2 py-1">Scans after</th><th className="px-2 py-1">Logical reads before</th><th className="px-2 py-1">Logical reads after</th><th className="px-2 py-1">Change</th></>}</tr></thead>
        <tbody>{row.objects.map(item => <tr key={item.table} className="border-b">
          <th scope="row" className="py-1 pr-3 text-left font-normal">{item.table}</th>{measured
            ? <><td className="px-2 py-1">{number(item.scansAfter)}</td><td className="px-2 py-1">{number(item.readsAfter)}</td></>
            : <><td className="px-2 py-1">{number(item.scansBefore)}</td><td className="px-2 py-1">{number(item.scansAfter)}</td><td className="px-2 py-1">{number(item.readsBefore)}</td><td className="px-2 py-1">{number(item.readsAfter)}</td><td className="px-2 py-1">{item.change}</td></>}
        </tr>)}</tbody>
      </table>
    </div>
  </details>
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
    {ordered.length > 0 && <div className="overflow-x-auto">
      <table className="w-full text-sm" aria-label="Statement IO rows">
        <thead><tr className="border-b text-left"><th className="py-2 pr-3">Statement</th><th className="px-3 py-2">Status</th>{measured
          ? <><th className="px-3 py-2">Executions</th><th className="px-3 py-2">Scans</th><th className="px-3 py-2">Logical reads</th></>
          : <><th className="px-3 py-2">Executions</th><th className="px-3 py-2">Reads before</th><th className="px-3 py-2">Reads after</th><th className="px-3 py-2">Change</th></>}</tr></thead>
        <tbody>{ordered.map((row, index) => <tr key={`${row.key}:${row.occurrence}`} className="border-b align-top">
          <td className="py-2 pr-3"><div className="max-w-2xl whitespace-pre-wrap">{row.text}</div>
            <div className="mt-1 flex flex-wrap gap-2 text-xs">{!measured && <><PlanLink href={row.planLinkBefore} hash={row.planHashBefore} label="Plan before" /><PlanLink href={row.planLinkAfter} hash={row.planHashAfter} label="Plan after" /></>}
              {measured && <PlanLink href={row.planLinkAfter} hash={row.planHashAfter} label="Plan after" />}</div>
            {row.hiddenWork.length > 0 && <div className="mt-2 flex flex-wrap gap-1" aria-label={`Hidden work for statement ${index + 1}`}>
              {row.hiddenWork.map((item, itemIndex) => <HiddenWorkFlag key={`${item.kind}:${item.name}:${itemIndex}`} item={item} />)}
            </div>}
            <Objects row={row} measured={measured} />
          </td>
          <td className="px-3 py-2">{statusLabels[row.status] ?? row.status}</td>
          <td className="px-3 py-2">{row.executions === null ? "—" : `${number(row.executions)} ${row.executions === 1 ? "execution" : "executions"}`}</td>
          {measured ? <><td className="px-3 py-2">{number(row.scansAfter)}</td><td className="px-3 py-2">{number(row.readsAfter)}</td></>
            : <><td className="px-3 py-2">{number(row.readsBefore)}</td><td className="px-3 py-2">{number(row.readsAfter)}</td><td className="px-3 py-2">{row.change}</td></>}
        </tr>)}</tbody>
      </table>
    </div>}
  </section>
}

export function StatementHiddenWork({ rows }: { rows: StatementRow[] }) {
  const flags = rows.flatMap(row => row.hiddenWork)
  const unique = [...new Map(flags.map(item => [`${item.kind}:${item.name}:${item.executions}:${item.sharePercent}`, item])).values()]
  return unique.length > 0 ? <div className="flex flex-wrap gap-1" aria-label="Hidden work in statements">
    {unique.map((item, index) => <HiddenWorkFlag key={`${item.kind}:${item.name}:${index}`} item={item} />)}
  </div> : null
}
