import { useState, type ReactNode } from "react"
import type { OperationDetail } from "@/api/types"
import { Link, useNavigate, useParams, useSearch } from "@tanstack/react-router"
import { NotFoundError } from "@/api/client"
import type { AgentSessionKey } from "@/api/types"
import { useOperation, useOperationOptimization, useUpdateOperationAnnotation } from "@/api/queries"
import { ErrorState } from "@/components/ErrorState"
import { FlagBadges } from "@/components/FlagBadges"
import { SqlBlock } from "@/components/SqlBlock"
import { StatusBadge } from "@/components/StatusBadge"
import { VariantPanel } from "@/components/VariantPanel"
import { CompareDetail } from "@/components/CompareDetail"
import { ConditionsList } from "@/components/ConditionsList"
import { ReproScriptButtons } from "@/components/ReproScriptButtons"
import { displayCaseLabel } from "@/lib/caseLabel"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Skeleton } from "@/components/ui/skeleton"
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs"
import { variantLabel } from "@/lib/compare"
import { formatDuration, formatNumber, formatTimestamp, shortHash } from "@/lib/format"
import { agentLabel } from "@/lib/agents"
import { parseRouteId } from "@/lib/routeId"
import { sessionSearchFromOperation } from "@/lib/sessionSearch"
import type { OperationDetailSearch } from "@/router"
import { ComparisonPanel } from "@/components/optimization/ComparisonPanel"
import { StatementRows } from "@/components/optimization/StatementRows"
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from "@/components/ui/collapsible"

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

function OperationOwnerAnnotations({ operation }: { operation: OperationDetail["operation"] }) {
  const update = useUpdateOperationAnnotation(operation.id)
  const [note, setNote] = useState(operation.ownerNote ?? "")
  return <section className="flex flex-wrap items-center gap-2" aria-label="Operation annotations">
    <Button variant="outline" size="sm" aria-pressed={operation.ownerStarred}
      onClick={() => update.mutate({ starred: !operation.ownerStarred })}>{operation.ownerStarred ? "★ Owner starred" : "☆ Star operation"}</Button>
    {operation.agentKeep && <Badge variant="secondary">Agent keep</Badge>}
    {operation.agentLabel && <span className="rounded border px-2 py-1 text-sm">{operation.agentLabel}</span>}
    <div className="flex items-center gap-2 text-sm">
      <Label htmlFor="operation-owner-note">Owner note</Label>
      <Input id="operation-owner-note" value={note} onChange={event => setNote(event.currentTarget.value)} />
    </div>
    <Button variant="outline" size="sm" disabled={update.isPending} onClick={() => update.mutate({ note })}>Save note</Button>
    {update.error && <p role="alert" className="text-sm text-destructive">Could not save operation annotations: {update.error.message}</p>}
  </section>
}

function StoredSqlReuse({ detail }: { detail: OperationDetail }) {
  const sql = detail.sqlText ?? detail.candidateSqlText
  const [copyError, setCopyError] = useState(false)
  return sql
    ? <><Button variant="outline" size="sm" onClick={() => {
      if (!navigator.clipboard) { setCopyError(true); return }
      void navigator.clipboard.writeText(sql).then(() => setCopyError(false), () => setCopyError(true))
    }}>Copy stored SQL to reuse</Button>{copyError && <p role="alert" className="text-sm text-destructive">Could not copy stored SQL to the clipboard.</p>}</>
    : <p className="text-sm text-muted-foreground">SQL not stored</p>
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
        <Collapsible>
          <CollapsibleTrigger className="cursor-pointer text-sm font-medium">Connection events</CollapsibleTrigger>
          <CollapsibleContent keepMounted><ol className="mt-3 space-y-2 text-sm">
            {events.map((event, index) => <li key={`${event.phase}:${event.offsetMs}:${index}`} className="rounded-md border p-3">
              {event.outcome === "truncated" && event.droppedCount !== null ? (
                <span>{formatNumber(event.droppedCount)} diagnostic events omitted</span>
              ) : <div className="flex flex-wrap gap-x-4 gap-y-1">
                <span>{event.phase}</span><span>Offset {formatDuration(event.offsetMs)}</span>
                <span>Duration {formatDuration(event.durationMs)}</span><span>Outcome: {event.outcome}</span>
                {event.errorNumber !== null && <span>Error number: {event.errorNumber}</span>}
                {event.clientConnectionId && <span className="font-mono">{event.clientConnectionId}</span>}
              </div>}
            </li>)}
          </ol></CollapsibleContent>
        </Collapsible>
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

function OperationView({ id, returnSearch }: { id: number; returnSearch: OperationDetailSearch }) {
  const query = useOperation(id)

  if (query.error) return <ErrorState error={query.error} />
  if (query.isPending) return <Skeleton className="h-64 w-full" />

  const detail = query.data
  const op = detail.operation
  if (op.operation === "compare") return <CompareOperationView id={id} detail={detail} returnSearch={returnSearch} />
  const returnSession = returnSearch.returnSession === op.sessionId ? returnSearch.returnSession : undefined
  const returnAgent = agentReturnKey(returnSearch)
  const hasPlanParameterSet = returnSearch.planParameterSetNull === true || returnSearch.planParameterSet !== undefined
  const planTarget = hasPlanParameterSet && returnSearch.planCase !== undefined && returnSearch.planRepetition !== undefined
    && returnSearch.planDocument !== undefined && returnSearch.planStatement !== undefined
    ? { caseId: returnSearch.planCase, parameterSet: returnSearch.planParameterSetNull ? null : returnSearch.planParameterSet!, repetition: returnSearch.planRepetition, documentOrdinal: returnSearch.planDocument, statementIndex: returnSearch.planStatement }
    : undefined
  const targetVariant = planTarget ? detail.variants.find(variant => variant.caseId === planTarget.caseId
    && variant.variant === returnSearch.planVariant && variant.parameterSet === planTarget.parameterSet) : undefined
  const tabVariant = targetVariant ?? detail.variants[0]
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
          <OperationOwnerAnnotations key={op.id} operation={op} />
          <CardDescription>
            {op.operation} by {agentLabel(op.agentKind)} in{" "}
            <Link to="/sessions/$id" params={{ id: String(op.sessionId) }} search={returnSession ? sessionSearchFromOperation(returnSearch) : undefined}>
              {detail.session.sessionKey}
            </Link>
              {returnAgent && <Link className="ml-3 underline" to="/agent-sessions/$kind/$id" params={{ kind: returnAgent.agentKind, id: returnAgent.conversationId }}
                search={sessionSearchFromOperation(returnSearch)}>Back to agent session</Link>}
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

      {op.operation === "measure" && <Card>
        <CardHeader><CardTitle>Statement IO</CardTitle></CardHeader>
        <CardContent className="space-y-2">
          <p className="text-sm text-muted-foreground">statement attribution: {detail.statementAttribution ?? "unavailable"}</p>
          {(detail.omittedStatements ?? 0) > 0 && <p className="text-sm text-muted-foreground">omitted statements: {detail.omittedStatements} (their reads remain in the batch total)</p>}
          <StatementRows rows={detail.statements ?? []} measured postgres={op.engine?.toLowerCase() === "postgresql" || op.engine?.toLowerCase() === "postgres"} />
        </CardContent>
      </Card>}

      {detail.variants.length === 1 && <VariantPanel operationId={op.id} variant={detail.variants[0]} engine={op.engine} planTarget={targetVariant ? planTarget : undefined} />}
      {detail.variants.length > 1 && (
        <Tabs defaultValue={String(tabVariant.ordinal)}>
          <TabsList>
            {detail.variants.map(v => <TabsTrigger key={v.ordinal} value={String(v.ordinal)}>{variantLabel(v)}</TabsTrigger>)}
          </TabsList>
          {detail.variants.map(v => <TabsContent key={v.ordinal} value={String(v.ordinal)}><VariantPanel operationId={op.id} variant={v} engine={op.engine} planTarget={v === targetVariant ? planTarget : undefined} /></TabsContent>)}
        </Tabs>
      )}
      <Collapsible className="rounded-lg border p-4"><CollapsibleTrigger className="cursor-pointer font-medium">Run diagnostics</CollapsibleTrigger><CollapsibleContent keepMounted><div className="mt-4 space-y-4">
        <Card><CardHeader><CardTitle>Output size</CardTitle></CardHeader><CardContent className="grid grid-cols-2 gap-4 md:grid-cols-4">
          <Fact label="Output bytes (raw / emitted)">{formatNumber(detail.rawBytes)} / {formatNumber(detail.emittedBytes)}</Fact>
          <Fact label="Estimated output tokens (raw / emitted)">{formatNumber(detail.rawTokens)} / {formatNumber(detail.emittedTokens)}</Fact>
          <Fact label="Token estimation method">{detail.tokenEstimationMethod}; UTF-8 bytes divided by four, rounded up</Fact>
        </CardContent></Card>
        <OperationTimings detail={detail} />
        {detail.agent && <Card><CardHeader><CardTitle><h2>Agent call</h2></CardTitle></CardHeader><CardContent className="grid grid-cols-2 gap-4 md:grid-cols-4">
          <Fact label="Model">{detail.agent.model ?? detail.agent.transcriptModel ?? "—"}{!detail.agent.model && detail.agent.transcriptModel && <span className="ml-1 text-sm text-muted-foreground">from transcript</span>}</Fact>
          <Fact label="Effort">{detail.agent.reasoningEffort ?? detail.agent.transcriptReasoningEffort ?? "—"}{!detail.agent.reasoningEffort && detail.agent.transcriptReasoningEffort && <span className="ml-1 text-sm text-muted-foreground">from transcript</span>}</Fact>
          {detail.agent.conversationTitle && <Fact label="Conversation">{detail.agent.conversationTitle}</Fact>}<Fact label="Agent session">{detail.agent.sessionId ?? "—"}</Fact><Fact label="Turn">{detail.agent.turnId ?? "—"}</Fact><Fact label="Trigger">{detail.agent.turnTrigger ?? "—"}</Fact><Fact label="Thread source">{detail.agent.threadSource ?? "—"}</Fact><Fact label="Call id">{detail.agent.callId ?? "—"}</Fact>
        </CardContent></Card>}
      </div></CollapsibleContent></Collapsible>

      {op.operation === "measure" && <Card>
        <CardHeader><CardTitle>Measurement conditions</CardTitle></CardHeader>
        <CardContent className="space-y-4">
          <ConditionsList conditions={detail.conditions} />
          {detail.variants.length > 0 && <section className="space-y-2">
            <h2 className="font-medium">Reproduction scripts</h2>
            {[...new Map(detail.variants.map(variant => [variant.caseId, variant])).values()].map(variant => <div key={variant.caseId} className="space-y-1">
              {variant.caseLabel !== "Case 1 of 1" && <h3 className="text-sm font-medium" title={variant.caseLabel}>{displayCaseLabel(variant.caseLabel)}</h3>}
              <ReproScriptButtons operationId={op.id} side="query" caseId={variant.caseId} />
            </div>)}
          </section>}
        </CardContent>
      </Card>}

      {op.operation === "compare" && <CompareDetail detail={detail} />}

      {op.operation !== "compare" && <Card>
        <CardHeader>
          <CardTitle>SQL</CardTitle>
        </CardHeader>
        <CardContent className="space-y-2">
          {detail.sqlText === null ? (
            <p className="text-sm text-muted-foreground">SQL text is not stored (journal.storeSensitive is off).</p>
          ) : (
            <>
              {op.operation === "measure" && typeof detail.setupSqlText === "string" && detail.setupSqlText.trim() !== "" && (
                <section aria-label="Setup SQL" className="space-y-2">
                  <h3 className="font-medium">Setup SQL</h3>
                  <p className="text-sm text-muted-foreground">Run setup and the measured SQL in the same database session, with the original parameter values.</p>
                  <SqlBlock sql={detail.setupSqlText} engine={op.engine} initiallyFormatted={false} />
                </section>
              )}
              <SqlBlock sql={detail.sqlText} engine={op.engine} label={detail.candidateSqlText !== null ? "Baseline" : undefined} />
              {detail.candidateSqlText !== null && <SqlBlock sql={detail.candidateSqlText} engine={op.engine} label="Candidate" />}
            </>
          )}
        </CardContent>
      </Card>}
      <StoredSqlReuse detail={detail} />
    </div>
  )
}

function CompareOperationView({ id, detail, returnSearch }: { id: number; detail: OperationDetail; returnSearch: OperationDetailSearch }) {
  const op = detail.operation
  const navigate = useNavigate({ from: "/operations/$id" })
  const selectedCase = returnSearch.case ?? "all"
  const aggregate = returnSearch.agg ?? "sum"
  const view = returnSearch.view ?? "changed"
  const comparison = useOperationOptimization(id, selectedCase, aggregate, view)
  const returnSession = returnSearch.returnSession === op.sessionId ? returnSearch.returnSession : undefined
  const returnAgent = agentReturnKey(returnSearch)
  const title = detail.session.title ?? detail.session.clientTitle ?? detail.session.sessionKey
  if (comparison.error) return <ErrorState error={comparison.error} />
  return <main className="space-y-4">
    <header className="flex flex-wrap items-center gap-x-3 gap-y-1 border-b pb-3">
      <h1 className="font-heading text-lg font-medium">{detail.compareSubject ?? "Unassigned compare"}</h1>
      <span className="text-sm">{detail.compareScope === "whole" ? "whole query" : detail.compareScope ?? "Unassigned scope"}</span>
      <span className="text-sm">{detail.baselineVersion ?? "Unknown"} → {detail.candidateVersion ?? "Unknown"}</span>
      <span className="text-sm">{op.database ?? "Unknown database"}</span>
      <time className="text-sm" dateTime={op.startedAt}>{formatTimestamp(op.startedAt)}</time>
      <StatusBadge operation={op} />
      <OperationOwnerAnnotations key={op.id} operation={op} />
      {returnAgent
        ? <Link to="/agent-sessions/$kind/$id" params={{ kind: returnAgent.agentKind, id: returnAgent.conversationId }} search={sessionSearchFromOperation(returnSearch)} className="ml-auto text-sm underline">Back to agent session</Link>
        : <Link to="/sessions/$id" params={{ id: String(op.sessionId) }} search={returnSession ? sessionSearchFromOperation(returnSearch) : undefined} className="ml-auto text-sm underline">Back to {title}</Link>}
    </header>
    <StoredSqlReuse detail={detail} />
    {detail.vars && <div className="flex flex-wrap gap-2" aria-label="Variables">{Object.entries(detail.vars).map(([key, value]) => <Badge key={key} variant="outline">{key}=<span>{value}</span></Badge>)}</div>}
    {comparison.isPending ? <Skeleton className="h-64 w-full" /> : comparison.data ? <>
      <ComparisonPanel comparison={comparison.data} sessionId={op.sessionId} agentSessionKey={returnAgent} subject={detail.compareSubject ?? "Unassigned"} scope={detail.compareScope ?? "whole query"} engine={op.engine} hideCaseSelector
        searchState={{ case: selectedCase, agg: aggregate, view }} sessionSearch={sessionSearchFromOperation(returnSearch)}
        onSelectionChange={patch => { void navigate({ search: previous => ({ ...previous, ...patch }) }) }} />
      <CompareDetail detail={detail} selectedCase={selectedCase === "all" ? comparison.data.selectedCase : selectedCase} onCaseChange={value => { void navigate({ search: previous => ({ ...previous, case: value }) }) }} />
    </> : null}
    <Collapsible className="rounded-lg border p-4"><CollapsibleTrigger className="cursor-pointer font-medium">Run diagnostics</CollapsibleTrigger><CollapsibleContent keepMounted><div className="mt-4 space-y-4">
      <Card><CardHeader><CardTitle>Output size</CardTitle></CardHeader><CardContent className="grid grid-cols-2 gap-4 md:grid-cols-4">
        <Fact label="Output bytes (raw / emitted)">{formatNumber(detail.rawBytes)} / {formatNumber(detail.emittedBytes)}</Fact>
        <Fact label="Estimated output tokens (raw / emitted)">{formatNumber(detail.rawTokens)} / {formatNumber(detail.emittedTokens)}</Fact>
        <Fact label="Token estimation method">{detail.tokenEstimationMethod}; UTF-8 bytes divided by four, rounded up</Fact>
      </CardContent></Card>
      <OperationTimings detail={detail} />
      {detail.agent && <Card><CardHeader><CardTitle><h2>Agent call</h2></CardTitle></CardHeader><CardContent className="grid grid-cols-2 gap-4 md:grid-cols-4">
        <Fact label="Model">{detail.agent.model ?? detail.agent.transcriptModel ?? "—"}</Fact><Fact label="Effort">{detail.agent.reasoningEffort ?? detail.agent.transcriptReasoningEffort ?? "—"}</Fact>
        {detail.agent.conversationTitle && <Fact label="Conversation">{detail.agent.conversationTitle}</Fact>}<Fact label="Agent session">{detail.agent.sessionId ?? "—"}</Fact><Fact label="Turn">{detail.agent.turnId ?? "—"}</Fact><Fact label="Trigger">{detail.agent.turnTrigger ?? "—"}</Fact><Fact label="Thread source">{detail.agent.threadSource ?? "—"}</Fact><Fact label="Call id">{detail.agent.callId ?? "—"}</Fact>
      </CardContent></Card>}
    </div></CollapsibleContent></Collapsible>
  </main>
}

function agentReturnKey(search: OperationDetailSearch): AgentSessionKey | undefined {
  return search.returnAgentKind && search.returnAgentId
    ? { agentKind: search.returnAgentKind, conversationId: search.returnAgentId }
    : undefined
}
