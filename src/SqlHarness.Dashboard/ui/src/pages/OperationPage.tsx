import type { ReactNode } from "react"
import type { OperationDetail } from "@/api/types"
import { Link, useParams, useSearch } from "@tanstack/react-router"
import { NotFoundError } from "@/api/client"
import { useOperation } from "@/api/queries"
import { ErrorState } from "@/components/ErrorState"
import { FlagBadges } from "@/components/FlagBadges"
import { SqlBlock } from "@/components/SqlBlock"
import { StatusBadge } from "@/components/StatusBadge"
import { VariantPanel } from "@/components/VariantPanel"
import { CompareDetail } from "@/components/CompareDetail"
import { Badge } from "@/components/ui/badge"
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Skeleton } from "@/components/ui/skeleton"
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs"
import { variantLabel } from "@/lib/compare"
import { formatDuration, formatNumber, formatTimestamp, shortHash } from "@/lib/format"
import { agentLabel } from "@/lib/agents"
import { parseRouteId } from "@/lib/routeId"

function Fact({ label, children }: { label: string; children: ReactNode }) {
  return (
    <div className="min-w-0">
      <div className="text-sm text-muted-foreground">{label}</div>
      <div className="truncate">{children}</div>
    </div>
  )
}

function SummaryFacts({ summary }: { summary: Record<string, unknown> }) {
  const equivalent = summary.resultsEquivalent
  const { sets, stableSets } = summary
  return (
    <div className="flex flex-wrap items-center gap-2">
      {typeof summary.kind === "string" && <Badge variant="outline">{summary.kind}</Badge>}
      {typeof summary.comparison === "string" && <span className="text-sm">comparison: {summary.comparison}</span>}
      {equivalent === true && <Badge variant="secondary">equivalent</Badge>}
      {equivalent === false && <Badge variant="destructive">not equivalent</Badge>}
      {(summary.resultsStable === false || (typeof sets === "number" && typeof stableSets === "number" && stableSets < sets)) && (
        <Badge variant="destructive">unstable results</Badge>
      )}
      {typeof sets === "number" && typeof stableSets === "number" && <span className="text-sm">{stableSets}/{sets} sets stable</span>}
      {typeof summary.parameterName === "string" && (
        <span className="text-sm">
          parameter: {summary.parameterName}
          {typeof summary.parameterType === "string" && ` (${summary.parameterType})`}
        </span>
      )}
      {typeof summary.cells === "number" && <span className="text-sm">{summary.equivalentCells as number}/{summary.cells} cells equivalent</span>}
    </div>
  )
}

function OperationTimings({ detail }: { detail: OperationDetail }) {
  const { phases, events } = detail
  const durationMs = detail.operation.durationMs
  return <>
    <Card>
      <CardHeader>
        <CardTitle><h2>Time breakdown</h2></CardTitle>
      </CardHeader>
      <CardContent>
        <div role="region" aria-label="Time breakdown" className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead><tr className="border-b text-left">
              <th className="py-2 pr-4">Phase</th><th className="py-2 pr-4">Count</th>
              <th className="py-2 pr-4">Total</th><th className="py-2 pr-4">Max</th><th className="py-2">Share of wall time</th>
            </tr></thead>
            <tbody>{phases.length === 0 ? <tr>
              <td colSpan={5} className="py-3 text-muted-foreground">No phase timing data was recorded for this operation.</td>
            </tr> : phases.map(phase => {
              const share = durationMs !== null && durationMs > 0
                ? `${Number((phase.totalMs * 100 / durationMs).toFixed(1))}%`
                : "—"
              return <tr key={phase.phase} className="border-b last:border-0">
                <td className="py-2 pr-4">{phase.phase}</td><td className="py-2 pr-4">{formatNumber(phase.count)}</td>
                <td className="py-2 pr-4">{formatDuration(phase.totalMs)}</td><td className="py-2 pr-4">{formatDuration(phase.maxMs)}</td>
                <td className="py-2">{share}</td>
              </tr>
            })}</tbody>
          </table>
        </div>
      </CardContent>
    </Card>
    {events && events.length > 0 && <Card>
      <CardContent className="pt-4">
        <details>
          <summary className="cursor-pointer font-medium">Connection events</summary>
          <ol className="mt-3 space-y-2 text-sm">
            {events.map((event, index) => <li key={`${event.phase}:${event.offsetMs}:${index}`} className="rounded-md border p-3">
              {event.outcome === "truncated" && event.droppedCount !== null ? (
                <span>{formatNumber(event.droppedCount)} connection events omitted</span>
              ) : <div className="flex flex-wrap gap-x-4 gap-y-1">
                <span>{event.phase}</span><span>Offset {formatDuration(event.offsetMs)}</span>
                <span>Duration {formatDuration(event.durationMs)}</span><span>Outcome: {event.outcome}</span>
                {event.errorNumber !== null && <span>Error number: {event.errorNumber}</span>}
                {event.clientConnectionId && <span className="font-mono">{event.clientConnectionId}</span>}
              </div>}
            </li>)}
          </ol>
        </details>
      </CardContent>
    </Card>}
  </>
}

export function OperationPage() {
  const raw = useParams({ from: "/operations/$id" }).id
  const search = useSearch({ from: "/operations/$id" })
  const id = parseRouteId(raw)
  if (id === null) return <ErrorState error={new NotFoundError(`/operations/${raw}`)} />
  return <OperationView id={id} returnSearch={search} />
}

function OperationView({ id, returnSearch }: { id: number; returnSearch: { returnSession?: number; returnSubject?: string; returnScope?: string; returnUnassigned?: boolean } }) {
  const query = useOperation(id)

  if (query.error) return <ErrorState error={query.error} />
  if (query.isPending) return <Skeleton className="h-64 w-full" />

  const detail = query.data
  const op = detail.operation
  const returnSession = returnSearch.returnSession === op.sessionId ? returnSearch.returnSession : undefined
  return (
    <div className="space-y-4">
      <Card>
        <CardHeader>
          <div className="flex flex-wrap items-center gap-2">
            <CardTitle>
              <h1>Operation #{op.id}</h1>
            </CardTitle>
            <StatusBadge operation={op} />
            <FlagBadges operation={op} />
          </div>
          <CardDescription>
            {op.operation} by {agentLabel(op.agentKind)} in{" "}
            <Link to="/sessions/$id" params={{ id: String(op.sessionId) }} search={returnSession ? { subject: returnSearch.returnSubject, scope: returnSearch.returnScope, unassigned: returnSearch.returnUnassigned } : undefined}>
              {detail.session.sessionKey}
            </Link>
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="grid grid-cols-2 gap-4 md:grid-cols-4">
            <Fact label="Started">{formatTimestamp(op.startedAt)}</Fact>
            <Fact label="Finished">{formatTimestamp(op.finishedAt)}</Fact>
            <Fact label="Duration">{formatDuration(op.durationMs)}</Fact>
            <Fact label="Exit code">{op.exitCode ?? "—"}{op.errorKind ? ` (${op.errorKind}${op.cancelReason ? `: ${op.cancelReason}` : ""})` : ""}</Fact>
            <Fact label="Profile">{op.profile ?? "—"}</Fact>
            <Fact label="Engine">{op.engine ?? "—"}</Fact>
            <Fact label="Server / database">{[op.server, op.database].filter(Boolean).join(" / ") || "—"}</Fact>
            <Fact label="Rows">{formatNumber(op.rowsReturned)}</Fact>
            <Fact label="SQL hash"><span className="font-mono">{shortHash(op.sqlHash)}</span></Fact>
            <Fact label="Output bytes (raw / emitted)">{formatNumber(detail.rawBytes)} / {formatNumber(detail.emittedBytes)}</Fact>
            <Fact label="Estimated output tokens (raw / emitted)">{formatNumber(detail.rawTokens)} / {formatNumber(detail.emittedTokens)}</Fact>
            <Fact label="Token estimation method">{detail.tokenEstimationMethod}; UTF-8 bytes divided by four, rounded up</Fact>
            {op.dryRun && <Fact label="Execution mode">Dry run; output counted, no database execution</Fact>}
            {op.operationRef && <Fact label="Operation reference"><span className="font-mono">{op.operationRef}</span></Fact>}
            {op.parentOperationRef && <Fact label="Producer reference"><span className="font-mono">{op.parentOperationRef}</span></Fact>}
            <Fact label="Artifact directory"><span className="font-mono">{detail.artifactDirectory ?? "—"}</span></Fact>
            {op.progress && <Fact label="Watch progress">{op.progress.polls} polls, {op.progress.changedPolls} changed</Fact>}
          </div>
          {detail.vars && (
            <div className="flex flex-wrap gap-2">
              {Object.entries(detail.vars).map(([key, value]) => (
                <Badge key={key} variant="outline">
                  {key}=<span>{value}</span>
                </Badge>
              ))}
            </div>
          )}
          {detail.dimensions.values.length > 0 && <div className="flex flex-wrap gap-2" aria-label="Scope dimensions">
            {detail.dimensions.values.map(value => {
              const filters = { [value.name]: value.isUnknown ? null : value.value }
              return <Link key={`${value.name}:${value.isUnknown ? "missing" : value.value}`} to="/operations" search={{
                profile: op.profile ?? undefined, unprofiled: op.profile === null || undefined,
                dimensions: JSON.stringify(filters),
              }} className="rounded-md border px-2 py-1 text-sm underline-offset-4 hover:underline focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring">
                {value.name} = {value.isUnknown ? "Unknown (missing)" : value.value}{value.source === "inferred" ? " (inferred)" : ""}
              </Link>
            })}
          </div>}
          {detail.summary && op.operation !== "compare" && <SummaryFacts summary={detail.summary} />}
        </CardContent>
      </Card>

      <OperationTimings detail={detail} />

      {detail.agent && (
        <Card>
          <CardHeader>
            <CardTitle><h2>Agent call</h2></CardTitle>
            <CardDescription>Labels the client sent with this call; not verified.</CardDescription>
          </CardHeader>
          <CardContent>
            <div className="grid grid-cols-2 gap-4 md:grid-cols-4">
              <Fact label="Model">{detail.agent.model ?? "—"}</Fact>
              <Fact label="Effort">{detail.agent.reasoningEffort ?? "—"}</Fact>
              <Fact label="Agent session"><span className="font-mono">{detail.agent.sessionId ?? "—"}</span></Fact>
              <Fact label="Turn"><span className="font-mono">{detail.agent.turnId ?? "—"}</span></Fact>
              <Fact label="Trigger">{detail.agent.turnTrigger ?? "—"}</Fact>
              <Fact label="Thread source">{detail.agent.threadSource ?? "—"}</Fact>
              <Fact label="Call id"><span className="font-mono">{detail.agent.callId ?? "—"}</span></Fact>
            </div>
          </CardContent>
        </Card>
      )}

      {op.operation === "compare" && <CompareDetail detail={detail} />}

      {op.operation !== "compare" && detail.variants.length === 1 && <VariantPanel variant={detail.variants[0]} engine={op.engine} />}
      {op.operation !== "compare" && detail.variants.length > 1 && (
        <Tabs defaultValue={String(detail.variants[0].ordinal)}>
          <TabsList>
            {detail.variants.map(v => (
              <TabsTrigger key={v.ordinal} value={String(v.ordinal)}>
                {variantLabel(v)}
              </TabsTrigger>
            ))}
          </TabsList>
          {detail.variants.map(v => (
            <TabsContent key={v.ordinal} value={String(v.ordinal)}>
              <VariantPanel variant={v} engine={op.engine} />
            </TabsContent>
          ))}
        </Tabs>
      )}

      {op.operation !== "compare" && <Card>
        <CardHeader>
          <CardTitle>SQL</CardTitle>
        </CardHeader>
        <CardContent className="space-y-2">
          {detail.sqlText === null ? (
            <p className="text-sm text-muted-foreground">SQL text is not stored (journal.storeSensitive is off).</p>
          ) : (
            <>
              <SqlBlock sql={detail.sqlText} engine={op.engine} label={detail.candidateSqlText !== null ? "Baseline" : undefined} />
              {detail.candidateSqlText !== null && <SqlBlock sql={detail.candidateSqlText} engine={op.engine} label="Candidate" />}
            </>
          )}
        </CardContent>
      </Card>}
    </div>
  )
}
