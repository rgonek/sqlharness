import { Fragment } from "react"
import { useQuery } from "@tanstack/react-query"
import { fetchDistilledPlan, useDistilledPlan } from "@/api/queries"
import { ErrorState } from "@/components/ErrorState"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import { Skeleton } from "@/components/ui/skeleton"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import type { ComparePair } from "@/lib/compare"
import { planChangeStatus, planChanges, storedPlanStatements, type TopOperator } from "@/lib/planChanges"
import { formatNumber } from "@/lib/format"

const operators = (values: string[]) => values.length ? values.join(", ") : "—"
const metric = (value: number | null, unit: string) => value === null ? `unavailable ${unit}` : `${formatNumber(value)} ${unit}`

function TopOperators({ title, nodes }: { title: string; nodes: TopOperator[] }) {
  return <div>
    <h4 className="mb-1 text-sm font-medium">{title}</h4>
    {nodes.length === 0 ? <p className="text-sm text-muted-foreground">No operator cost data.</p> : <ol className="list-inside list-decimal text-sm">
      {nodes.map((node, index) => <li key={`${node.physicalOp}-${node.object ?? ""}-${index}`}>
        {node.physicalOp}{node.object ? ` · ${node.object}` : ""} · {formatNumber(node.costFraction * 100)}%
      </li>)}
    </ol>}
  </div>
}

export function PlanChanges({ pair }: { pair: ComparePair }) {
  const beforeLinks = storedPlanStatements(pair.baseline)
  const afterLinks = storedPlanStatements(pair.candidate)
  const hashB = beforeLinks[0]?.hash ?? null
  const hashC = afterLinks[0]?.hash ?? null
  const status = planChangeStatus(pair.baseline, pair.candidate)
  const changedLinks = status.changed.flatMap(change => {
    const before = beforeLinks.find(link => link.ordinal === change.ordinal)
    const after = afterLinks.find(link => link.ordinal === change.ordinal)
    return [before, after].filter((link): link is NonNullable<typeof link> => Boolean(link))
  })
  const statementPlans = useQuery({
    queryKey: ["changed-statement-plans", [...new Set(changedLinks.map(link => link.hash))].sort()],
    queryFn: async () => Object.fromEntries(await Promise.all([...new Set(changedLinks.map(link => link.hash))].map(async hash => [hash, await fetchDistilledPlan(hash)] as const))),
    enabled: changedLinks.length > 0,
    staleTime: Infinity,
  })
  const baseline = useDistilledPlan(hashB ?? "", hashB !== null)
  const candidate = useDistilledPlan(hashC ?? "", hashC !== null && hashC !== hashB)

  let content
  if (hashB === null) content = <p className="text-sm text-muted-foreground">Plan not stored for the baseline.</p>
  else if (hashC === null) content = <p className="text-sm text-muted-foreground">Plan not stored for the candidate.</p>
  else if (baseline.error) content = <ErrorState error={baseline.error} />
  else if (hashB !== hashC && candidate.error) content = <ErrorState error={candidate.error} />
  else if (baseline.isPending || (hashB !== hashC && candidate.isPending)) content = <Skeleton className="h-32 w-full" />
  else if (status.same) content = <p className="text-sm text-muted-foreground">Same plan</p>
  else if (baseline.data && candidate.data) {
    const changes = planChanges(baseline.data, candidate.data)
    const detailByObject = new Map(changes.accessDetails.map(detail => [detail.object, detail]))
    content = <div className="space-y-4">
      <section aria-label={`${pair.label} changed statements`}>
        <h3 className="mb-2 font-medium">Changed statements</h3>
        {status.changed.length === 0 ? <p className="text-sm text-muted-foreground">Statement plan comparison unavailable.</p> : <ul className="space-y-2">
          {status.changed.map(change => {
            const link = afterLinks.find(item => item.ordinal === change.ordinal) ?? beforeLinks.find(item => item.ordinal === change.ordinal)
            const sql = link ? statementPlans.data?.[link.hash]?.statements.find(statement => statement.sql)?.sql : null
            return <li key={change.ordinal} className="rounded border p-2">
              <h4 className="font-medium">{sql ?? change.statement}</h4>
              {sql && <pre className="mt-1 whitespace-pre-wrap break-words font-mono text-xs">{sql}</pre>}
            </li>
          })}
        </ul>}
      </section>
      <section aria-label={`${pair.label} access changes`}>
        <h3 className="mb-2 font-medium">Access changes</h3>
        {changes.access.length === 0 ? <p className="text-sm text-muted-foreground">No access changes.</p> : <Table aria-label={`${pair.label} Access changes`}>
          <TableHeader><TableRow><TableHead>Object</TableHead><TableHead>Baseline</TableHead><TableHead>Candidate</TableHead></TableRow></TableHeader>
          <TableBody>{changes.access.map(change => {
            const details = detailByObject.get(change.object)!
            return <Fragment key={change.object}>
              <TableRow key={change.object}>
                <TableCell className="font-mono">{change.object}</TableCell>
                <TableCell>{operators(change.baseline)}</TableCell>
                <TableCell>{operators(change.candidate)}</TableCell>
              </TableRow>
              <TableRow key={`${change.object}-details`}>
                <TableCell className="text-muted-foreground">Actual rows / executions</TableCell>
                <TableCell>{details.baseline.map(node => `${node.physicalOp}: ${metric(node.actualRows, "rows")} / ${metric(node.executions, "executions")}`).join("; ") || "—"}</TableCell>
                <TableCell>{details.candidate.map(node => `${node.physicalOp}: ${metric(node.actualRows, "rows")} / ${metric(node.executions, "executions")}`).join("; ") || "—"}</TableCell>
              </TableRow>
            </Fragment>
          })}</TableBody>
        </Table>}
      </section>
      <section>
        <h3 className="mb-2 font-medium">Warnings</h3>
        {changes.warnings.length === 0 ? <p className="text-sm text-muted-foreground">No warning changes.</p> : <ul className="list-inside list-disc text-sm">
          {changes.warnings.map(change => {
            const status = change.baseline > 0 && change.candidate === 0 ? "resolved" : change.baseline === 0 && change.candidate > 0 ? "introduced" : "changed"
            return <li key={change.warning}>{change.warning}: {status} ({formatNumber(change.baseline)} → {formatNumber(change.candidate)})</li>
          })}
        </ul>}
      </section>
      <div className="grid gap-4 sm:grid-cols-2"><TopOperators title="Top baseline operators" nodes={changes.topBaseline} /><TopOperators title="Top candidate operators" nodes={changes.topCandidate} /></div>
    </div>
  }

  return <Card>
    <CardHeader><CardTitle>Plan changes</CardTitle><p className="text-sm text-muted-foreground">{pair.label}</p></CardHeader>
    <CardContent>{content}</CardContent>
  </Card>
}
