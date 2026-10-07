import type { ReactNode } from "react"
import { Link, useParams } from "@tanstack/react-router"
import { NotFoundError } from "@/api/client"
import { useOperation } from "@/api/queries"
import type { OperationDetail } from "@/api/types"
import { ErrorState } from "@/components/ErrorState"
import { FlagBadges } from "@/components/FlagBadges"
import { StatusBadge } from "@/components/StatusBadge"
import { VariantPanel } from "@/components/VariantPanel"
import { Badge } from "@/components/ui/badge"
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Skeleton } from "@/components/ui/skeleton"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs"
import { comparePairs, compareRows, variantLabel } from "@/lib/compare"
import { formatDuration, formatNumber, formatPercent, formatTimestamp, shortHash } from "@/lib/format"
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
  return (
    <div className="flex flex-wrap items-center gap-2">
      {typeof summary.kind === "string" && <Badge variant="outline">{summary.kind}</Badge>}
      {typeof summary.comparison === "string" && <span className="text-sm">comparison: {summary.comparison}</span>}
      {equivalent === true && <Badge variant="secondary">equivalent</Badge>}
      {equivalent === false && <Badge variant="destructive">not equivalent</Badge>}
      {summary.resultsStable === false && <Badge variant="destructive">unstable results</Badge>}
      {typeof summary.cells === "number" && <span className="text-sm">{summary.equivalentCells as number}/{summary.cells} cells equivalent</span>}
    </div>
  )
}

function Comparison({ detail }: { detail: OperationDetail }) {
  const pairs = comparePairs(detail.variants)
  if (pairs.length === 0) return null
  return (
    <Card role="region" aria-label="Baseline vs candidate">
      <CardHeader>
        <CardTitle>Baseline vs candidate</CardTitle>
      </CardHeader>
      <CardContent className="space-y-4 overflow-x-auto">
        {pairs.map(pair => (
          <div key={pair.label} className="space-y-2">
            {pairs.length > 1 && <div className="text-sm text-muted-foreground">{pair.label}</div>}
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Metric</TableHead>
                  <TableHead className="text-right">Baseline</TableHead>
                  <TableHead className="text-right">Candidate</TableHead>
                  <TableHead className="text-right">Change</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {compareRows(pair.baseline, pair.candidate).map(row => (
                  <TableRow key={row.metric}>
                    <TableCell>{row.metric}</TableCell>
                    <TableCell className="text-right">{formatNumber(row.baseline)}</TableCell>
                    <TableCell className="text-right">{formatNumber(row.candidate)}</TableCell>
                    <TableCell className="text-right">
                      {row.delta === null ? "—" : `${row.delta > 0 ? "+" : ""}${formatPercent(row.delta)}`}
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
        ))}
      </CardContent>
    </Card>
  )
}

export function OperationPage() {
  const raw = useParams({ from: "/operations/$id" }).id
  const id = parseRouteId(raw)
  if (id === null) return <ErrorState error={new NotFoundError(`/operations/${raw}`)} />
  return <OperationView id={id} />
}

function OperationView({ id }: { id: number }) {
  const query = useOperation(id)

  if (query.error) return <ErrorState error={query.error} />
  if (query.isPending) return <Skeleton className="h-64 w-full" />

  const detail = query.data
  const op = detail.operation
  return (
    <div className="space-y-4">
      <Card>
        <CardHeader>
          <div className="flex flex-wrap items-center gap-2">
            <CardTitle>
              <h1>Operation #{op.id}</h1>
            </CardTitle>
            <StatusBadge status={op.status} />
            <FlagBadges operation={op} />
          </div>
          <CardDescription>
            {op.operation} by {op.agentKind} in{" "}
            <Link to="/sessions/$id" params={{ id: String(op.sessionId) }}>
              {detail.session.sessionKey}
            </Link>
          </CardDescription>
        </CardHeader>
        <CardContent className="space-y-4">
          <div className="grid grid-cols-2 gap-4 md:grid-cols-4">
            <Fact label="Started">{formatTimestamp(op.startedAt)}</Fact>
            <Fact label="Finished">{formatTimestamp(op.finishedAt)}</Fact>
            <Fact label="Duration">{formatDuration(op.durationMs)}</Fact>
            <Fact label="Exit code">{op.exitCode ?? "—"}{op.errorKind ? ` (${op.errorKind})` : ""}</Fact>
            <Fact label="Profile">{op.profile ?? "—"}</Fact>
            <Fact label="Engine">{op.engine ?? "—"}</Fact>
            <Fact label="Server / database">{[op.server, op.database].filter(Boolean).join(" / ") || "—"}</Fact>
            <Fact label="Rows">{formatNumber(op.rowsReturned)}</Fact>
            <Fact label="SQL hash"><span className="font-mono">{shortHash(op.sqlHash)}</span></Fact>
            <Fact label="Tokens raw / emitted">{formatNumber(detail.rawTokens)} / {formatNumber(detail.emittedTokens)}</Fact>
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
          {detail.summary && <SummaryFacts summary={detail.summary} />}
        </CardContent>
      </Card>

      <Comparison detail={detail} />

      {detail.variants.length === 1 && <VariantPanel variant={detail.variants[0]} />}
      {detail.variants.length > 1 && (
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
              <VariantPanel variant={v} />
            </TabsContent>
          ))}
        </Tabs>
      )}

      <Card>
        <CardHeader>
          <CardTitle>SQL</CardTitle>
        </CardHeader>
        <CardContent className="space-y-2">
          {detail.sqlText === null ? (
            <p className="text-sm text-muted-foreground">SQL text is not stored (journal.storeSensitive is off).</p>
          ) : (
            <>
              <pre className="overflow-x-auto whitespace-pre-wrap font-mono text-sm">{detail.sqlText}</pre>
              {detail.candidateSqlText !== null && (
                <pre className="overflow-x-auto whitespace-pre-wrap font-mono text-sm">{detail.candidateSqlText}</pre>
              )}
            </>
          )}
        </CardContent>
      </Card>
    </div>
  )
}
