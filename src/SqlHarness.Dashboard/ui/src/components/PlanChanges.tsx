import { Fragment } from "react"
import { useQuery } from "@tanstack/react-query"
import { fetchDistilledPlan } from "@/api/queries"
import { ErrorState } from "@/components/ErrorState"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import { Skeleton } from "@/components/ui/skeleton"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import type { ComparePair } from "@/lib/compare"
import { planChangeStatus, planChanges, storedPlanStatements, type TopOperator } from "@/lib/planChanges"
import { formatNumber } from "@/lib/format"

const operators = (values: string[]) => values.length ? values.join(", ") : "—"
const metric = (value: number | null, unit: string) => value === null ? `unavailable ${unit}` : `${formatNumber(value)} ${unit}`
const emptyPlan = { statements: [] }

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
  const status = planChangeStatus(pair.baseline, pair.candidate)
  const changedLinks = status.changed.flatMap(change => [
    beforeLinks.find(link => link.ordinal === change.ordinal),
    afterLinks.find(link => link.ordinal === change.ordinal),
  ].filter((link): link is NonNullable<typeof link> => Boolean(link)))
  const changedHashes = [...new Set(changedLinks.map(link => link.hash))].sort()
  const statementPlans = useQuery({
    queryKey: ["changed-statement-plans", changedHashes],
    queryFn: async () => Object.fromEntries(await Promise.all(changedHashes.map(async hash => [hash, await fetchDistilledPlan(hash)] as const))),
    enabled: changedHashes.length > 0,
    staleTime: Infinity,
  })

  let content
  if (beforeLinks.length === 0) content = <p className="text-sm text-muted-foreground">Plan not stored for the baseline.</p>
  else if (afterLinks.length === 0) content = <p className="text-sm text-muted-foreground">Plan not stored for the candidate.</p>
  else if (status.same) content = <p className="text-sm text-muted-foreground">Same plan</p>
  else if (statementPlans.error) content = <ErrorState error={statementPlans.error} />
  else if (statementPlans.isPending) content = <Skeleton className="h-32 w-full" />
  else {
    content = <div className="space-y-4">
      {status.changed.map(change => {
        const beforeLink = beforeLinks.find(link => link.ordinal === change.ordinal)
        const afterLink = afterLinks.find(link => link.ordinal === change.ordinal)
        const before = beforeLink ? statementPlans.data?.[beforeLink.hash] ?? emptyPlan : emptyPlan
        const after = afterLink ? statementPlans.data?.[afterLink.hash] ?? emptyPlan : emptyPlan
        const changes = planChanges(before, after)
        const detailByObject = new Map(changes.accessDetails.map(detail => [detail.object, detail]))
        const sqlLink = afterLink ?? beforeLink
        const sql = sqlLink ? statementPlans.data?.[sqlLink.hash]?.statements.find(statement => statement.sql)?.sql : null
        return <section key={change.ordinal} role="region" aria-label={`Statement ${change.ordinal} plan changes`} className="space-y-4 rounded border p-3">
          <header>
            <h3 className="font-medium">Statement {change.ordinal}</h3>
            <p className="text-sm text-muted-foreground">{sql ?? change.statement}</p>
            {sql && <pre className="mt-1 whitespace-pre-wrap break-words font-mono text-xs">{sql}</pre>}
          </header>
          <section aria-label={`Statement ${change.ordinal} access changes`}>
            <h4 className="mb-2 text-sm font-medium">Access changes</h4>
            {changes.access.length === 0 ? <p className="text-sm text-muted-foreground">No access changes.</p> : <Table aria-label={`${pair.label} Statement ${change.ordinal} Access changes`}>
              <TableHeader><TableRow><TableHead>Object</TableHead><TableHead>Baseline</TableHead><TableHead>Candidate</TableHead></TableRow></TableHeader>
              <TableBody>{changes.access.map(access => {
                const details = detailByObject.get(access.object)!
                return <Fragment key={access.object}>
                  <TableRow>
                    <TableCell className="font-mono">{access.object}</TableCell>
                    <TableCell>{operators(access.baseline)}</TableCell>
                    <TableCell>{operators(access.candidate)}</TableCell>
                  </TableRow>
                  <TableRow>
                    <TableCell className="text-muted-foreground">Actual rows / executions</TableCell>
                    <TableCell>{details.baseline.map(node => `${node.physicalOp}: ${metric(node.actualRows, "rows")} / ${metric(node.executions, "executions")}`).join("; ") || "—"}</TableCell>
                    <TableCell>{details.candidate.map(node => `${node.physicalOp}: ${metric(node.actualRows, "rows")} / ${metric(node.executions, "executions")}`).join("; ") || "—"}</TableCell>
                  </TableRow>
                </Fragment>
              })}</TableBody>
            </Table>}
          </section>
          <section aria-label={`Statement ${change.ordinal} warnings`}>
            <h4 className="mb-2 text-sm font-medium">Warnings</h4>
            {changes.warnings.length === 0 ? <p className="text-sm text-muted-foreground">No warning changes.</p> : <ul className="list-inside list-disc text-sm">
              {changes.warnings.map(warning => {
                const warningStatus = warning.baseline > 0 && warning.candidate === 0 ? "resolved" : warning.baseline === 0 && warning.candidate > 0 ? "introduced" : "changed"
                return <li key={warning.warning}>{warning.warning}: {warningStatus} ({formatNumber(warning.baseline)} → {formatNumber(warning.candidate)})</li>
              })}
            </ul>}
          </section>
          <div className="grid gap-4 sm:grid-cols-2"><TopOperators title="Top baseline operators" nodes={changes.topBaseline} /><TopOperators title="Top candidate operators" nodes={changes.topCandidate} /></div>
        </section>
      })}
    </div>
  }

  return <Card>
    <CardHeader><CardTitle>Plan changes</CardTitle><p className="text-sm text-muted-foreground">{pair.label}</p></CardHeader>
    <CardContent>{content}</CardContent>
  </Card>
}
