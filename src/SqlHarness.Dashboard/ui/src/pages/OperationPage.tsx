import { ComparisonPending } from "@/components/optimization/ComparisonPending"
import { CopyButton } from "@/components/CopyButton"
import { useState, type ReactNode } from "react"
import type { OperationDetail } from "@/api/types"
import { Link, useNavigate, useParams, useSearch } from "@tanstack/react-router"
import { NotFoundError } from "@/api/client"
import type { AgentSessionKey } from "@/api/types"
import { useOperation, useOperationOptimization, useUpdateOperationAnnotation } from "@/api/queries"
import { ErrorState } from "@/components/ErrorState"
import { FlagBadges } from "@/components/FlagBadges"
import { SqlBlock } from "@/components/SqlBlock"
import { SqlDiff } from "@/components/SqlDiff"
import { StatusBadge } from "@/components/StatusBadge"
import { VariantPanel } from "@/components/VariantPanel"
import { CompareDetail } from "@/components/CompareDetail"
import { ConditionsList } from "@/components/ConditionsList"
import { ReproScriptButtons } from "@/components/ReproScriptButtons"
import { displayCaseLabel } from "@/lib/caseLabel"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Skeleton } from "@/components/ui/skeleton"
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs"
import { variantLabel } from "@/lib/compare"
import { formatDuration, formatNumber, formatTimestamp } from "@/lib/format"
import { agentLabel } from "@/lib/agents"
import { parseRouteId } from "@/lib/routeId"
import { sessionSearchFromOperation } from "@/lib/sessionSearch"
import type { OperationDetailSearch } from "@/router"
import { ComparisonPanel } from "@/components/optimization/ComparisonPanel"
import { StatementRows } from "@/components/optimization/StatementRows"
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from "@/components/ui/collapsible"
import { HugeiconsIcon } from "@hugeicons/react";
import { MoreHorizontalCircle01Icon, SparklesIcon, StarIcon } from "@hugeicons/core-free-icons";

function OperationHeading({ detail, returnSearch, metadata }: { detail: OperationDetail; returnSearch: OperationDetailSearch; metadata?: ReactNode }) {
  const op = detail.operation
  const update = useUpdateOperationAnnotation(op.id)
  const returnAgent = agentReturnKey(returnSearch)
  const title = op.operation === "compare" ? `${detail.compareSubject ?? "Unassigned compare"} · ${detail.baselineVersion ?? "?"} → ${detail.candidateVersion ?? "?"}` : op.agentLabel ?? op.subject ?? `Operation #${op.id}`
  return <div className="operation-heading">
    <div className="operation-breadcrumb"><Link to="/">Activity</Link><span>/</span>{returnAgent ? <Link to="/agent-sessions/$kind/$id" params={{ kind: returnAgent.agentKind, id: returnAgent.conversationId }} search={sessionSearchFromOperation(returnSearch)}>Back to agent session</Link> : <Link to="/sessions/$id" params={{ id: String(op.sessionId) }} search={returnSearch.returnSession === op.sessionId ? sessionSearchFromOperation(returnSearch) : undefined}>{detail.session.title ?? detail.session.sessionKey}</Link>}<span>/</span><span>#{op.id}</span></div>
    <div className="flex items-center gap-2"><h1 aria-label={title} className="min-w-0 flex-1">{op.operation === "compare" ? <>{detail.compareSubject ?? "Unassigned compare"} · <span className="heading-version-pair">{detail.baselineVersion ?? "?"} → {detail.candidateVersion ?? "?"}</span></> : title}{op.agentKeep && <HugeiconsIcon icon={SparklesIcon} strokeWidth={2} className="ml-2 inline size-4 text-violet-600" aria-label="Kept by agent" />}</h1><Button variant="ghost" size="icon-sm" aria-label={op.ownerStarred ? "★ Owner starred" : "☆ Star operation"} aria-pressed={op.ownerStarred} disabled={update.isPending} onClick={() => update.mutate({ starred: !op.ownerStarred })}><HugeiconsIcon icon={StarIcon} strokeWidth={2} className={op.ownerStarred ? "fill-amber-400 text-amber-500" : ""} /></Button>{detail.sqlText && <CopyButton label="Copy stored SQL" getText={() => detail.sqlText!} />}<details className="operation-owner-menu"><summary aria-label="Operation actions"><HugeiconsIcon icon={MoreHorizontalCircle01Icon} strokeWidth={2} className="size-4" /></summary><div><OperationOwnerAnnotations key={op.id} operation={op} /></div></details></div>
    <div className="operation-subtitle"><StatusBadge operation={op} /><span className="capitalize">{op.operation} #{op.id}</span>{op.operation === "compare" && <><span>·</span><span>{detail.compareScope === "whole" ? "whole query" : detail.compareScope ?? "Unassigned scope"}</span></>}<span>·</span><span>{agentLabel(op.agentKind)}</span><code>{detail.session.transport}</code><span>·</span><time title={op.startedAt}>{formatTimestamp(op.startedAt)}</time><span>·</span><span>{formatDuration(op.durationMs)}</span><span>·</span><span>{op.profile && <>{op.profile} / </>}<span>{op.database ?? "—"}</span></span><FlagBadges operation={op} />{metadata && <><span>·</span>{metadata}</>}</div>
    {update.error && <p role="alert" className="text-sm text-destructive">Could not save operation annotations: {update.error.message}</p>}
  </div>
}

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
        <div role="region" aria-label="Time breakdown">
          <Table aria-label="Time breakdown">
            <TableHeader><TableRow className="text-left">
              <TableHead>Phase</TableHead><TableHead>Count</TableHead>
              <TableHead>Total</TableHead><TableHead>Max</TableHead><TableHead>Share of wall time</TableHead>
            </TableRow></TableHeader>
            <TableBody>{phases.length === 0 ? <TableRow>
              <TableCell colSpan={5} className="py-3 text-muted-foreground">No phase timing data was recorded for this operation.</TableCell>
            </TableRow> : phases.map(phase => {
              const share = durationMs !== null && durationMs > 0
                ? `${Number((phase.totalMs * 100 / durationMs).toFixed(1))}%`
                : "—"
              return <TableRow key={phase.phase}>
                <TableCell className="whitespace-normal">{phase.phase}</TableCell><TableCell>{formatNumber(phase.count)}</TableCell>
                <TableCell>{formatDuration(phase.totalMs)}</TableCell><TableCell>{formatDuration(phase.maxMs)}</TableCell>
                <TableCell>{share}</TableCell>
              </TableRow>
            })}</TableBody>
          </Table>
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
  const hasPlanParameterSet = returnSearch.planParameterSetNull === true || returnSearch.planParameterSet !== undefined
  const planTarget = hasPlanParameterSet && returnSearch.planCase !== undefined && returnSearch.planRepetition !== undefined
    && returnSearch.planDocument !== undefined && returnSearch.planStatement !== undefined
    ? { caseId: returnSearch.planCase, parameterSet: returnSearch.planParameterSetNull ? null : returnSearch.planParameterSet!, repetition: returnSearch.planRepetition, documentOrdinal: returnSearch.planDocument, statementIndex: returnSearch.planStatement }
    : undefined
  const targetVariant = planTarget ? detail.variants.find(variant => variant.caseId === planTarget.caseId
    && variant.variant === returnSearch.planVariant && variant.parameterSet === planTarget.parameterSet) : undefined
  const tabVariant = targetVariant ?? detail.variants[0]
  return (
    <div className="operation-page space-y-4">
      <OperationHeading detail={detail} returnSearch={returnSearch} metadata={<Collapsible className="operation-heading-metadata"><CollapsibleTrigger className="text-sm text-muted-foreground underline">Details</CollapsibleTrigger><CollapsibleContent keepMounted>
          <div className="mt-3 grid grid-cols-2 gap-4 md:grid-cols-4">
            <Fact label="Started">{formatTimestamp(op.startedAt)}</Fact>
            <Fact label="Finished">{formatTimestamp(op.finishedAt)}</Fact>
            <Fact label="Duration">{formatDuration(op.durationMs)}</Fact>
            <Fact label="Exit code">{op.exitCode ?? "—"}{op.errorKind ? ` (${op.errorKind}${op.cancelReason ? `: ${op.cancelReason}` : ""})` : ""}</Fact>
            <Fact label="Profile">{op.profile ?? "—"}</Fact>
            <Fact label="Engine">{op.engine ?? "—"}</Fact>
            <Fact label="Server / database">{[op.server, op.database].filter(Boolean).join(" / ") || "—"}</Fact>
            <Fact label="Rows">{formatNumber(op.rowsReturned)}</Fact>
            <Fact label="SQL hash"><span className="break-all font-mono">{op.sqlHash ?? "—"}</span></Fact>
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
          {detail.summary && <SummaryFacts summary={detail.summary} />}</CollapsibleContent></Collapsible>} />

      <div className={`operation-metrics ${op.operation === "measure" ? "measure-metrics" : ""}`} aria-label="Operation metrics">
        {op.operation === "query" ? <><Fact label="Rows">{formatNumber(op.rowsReturned)}</Fact><Fact label="Duration">{formatDuration(op.durationMs)}</Fact><Fact label="Output">{formatNumber(detail.emittedBytes)} B</Fact></> : op.operation === "mutation" ? <><Fact label="Duration">{formatDuration(op.durationMs)}</Fact><Fact label="Rows affected">{op.recordsAffected == null ? "Unavailable" : formatNumber(op.recordsAffected)}</Fact><Fact label="Waited for approval">{formatDuration(detail.phases.find(phase => /approval/i.test(phase.phase))?.totalMs ?? null)}</Fact></> : <><Fact label="Logical reads">{formatNumber(op.logicalReadsMedian)}</Fact><Fact label="Elapsed median">{formatDuration(tabVariant?.elapsedMs?.median ?? null)}</Fact><Fact label="CPU">{formatDuration(tabVariant?.cpuMs?.median ?? null)}</Fact><Fact label="Scans">{tabVariant?.tableIo.length && tabVariant.tableIo.every(row => row.scanCount !== null) ? formatNumber(tabVariant.tableIo.reduce((sum, row) => sum + row.scanCount!, 0)) : "—"}</Fact><Fact label="Rows">{formatNumber(op.rowsReturned)}</Fact></>}

      </div>
      <Tabs defaultValue="sql" className="operation-tabs">
        <TabsList variant="line" className="flex flex-wrap">
          <TabsTrigger value="sql">{op.operation === "measure" ? "SQL & plan" : "SQL"}</TabsTrigger>
          {op.operation === "measure" && <TabsTrigger value="io">IO</TabsTrigger>}
          {op.operation === "query" && <TabsTrigger value="results">Results</TabsTrigger>}
          {op.mutationRequested && <TabsTrigger value="approval">Approval</TabsTrigger>}
          <TabsTrigger value="timing">Timing</TabsTrigger>
          <TabsTrigger value="details">Details</TabsTrigger>
        </TabsList>
        <TabsContent value="sql"><div className="operation-parameters"><span>Parameters</span>{Object.entries(detail.vars ?? {}).map(([key,value]) => <code key={key}>@{key} = {value}</code>)}</div>      {op.operation === "compare" && <CompareDetail detail={detail} />}

      {op.operation !== "compare" && <Card className="operation-sql-card">

        <CardContent className="space-y-2 p-0">
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
              {op.operation === "measure" && detail.variants.some(variant => variant.plans.some(plan => plan.stored)) ? null : <SqlBlock sql={detail.sqlText} engine={op.engine} initiallyFormatted={false} label={detail.candidateSqlText !== null ? "Baseline" : undefined} />}
              {detail.candidateSqlText !== null && <SqlBlock sql={detail.candidateSqlText} engine={op.engine} label="Candidate" />}
            </>
          )}
        </CardContent>
      </Card>}
      {detail.variants.length === 1 && <VariantPanel operationId={op.id} variant={detail.variants[0]} engine={op.engine} mode="plan" planTarget={targetVariant ? planTarget : undefined} />}
      {detail.variants.length > 1 && (
        <Tabs defaultValue={String(tabVariant.ordinal)}>
          <TabsList>
            {detail.variants.map(v => <TabsTrigger key={v.ordinal} value={String(v.ordinal)}>{variantLabel(v)}</TabsTrigger>)}
          </TabsList>
          {detail.variants.map(v => <TabsContent key={v.ordinal} value={String(v.ordinal)}><VariantPanel operationId={op.id} variant={v} engine={op.engine} mode="plan" planTarget={v === targetVariant ? planTarget : undefined} /></TabsContent>)}
        </Tabs>
      )}
      {detail.sqlText !== null && op.operation === "measure" && detail.variants.some(variant => variant.plans.some(plan => plan.stored)) && <Collapsible><CollapsibleTrigger className="cursor-pointer text-xs text-muted-foreground underline">Stored SQL</CollapsibleTrigger><CollapsibleContent><SqlBlock sql={detail.sqlText} engine={op.engine} initiallyFormatted={false} /></CollapsibleContent></Collapsible>}
        </TabsContent>
        <TabsContent value="io">      {op.operation === "measure" && <Card>
        <CardHeader><CardTitle>Statement IO</CardTitle></CardHeader>
        <CardContent className="space-y-2">
          <p className="text-sm text-muted-foreground">statement attribution: {detail.statementAttribution ?? "unavailable"}</p>
          {(detail.omittedStatements ?? 0) > 0 && <p className="text-sm text-muted-foreground">omitted statements: {detail.omittedStatements} (their reads remain in the batch total)</p>}
          <StatementRows rows={detail.statements ?? []} measured postgres={op.engine?.toLowerCase() === "postgresql" || op.engine?.toLowerCase() === "postgres"} />
        </CardContent>
      </Card>}

          {detail.variants.map(variant => <VariantPanel key={variant.ordinal} operationId={op.id} variant={variant} engine={op.engine} mode="io" />)}
        </TabsContent>
        <TabsContent value="results"><p className="text-sm text-muted-foreground">{formatNumber(op.rowsReturned)} rows returned. Result cells are not retained in the activity journal.</p></TabsContent>
        <TabsContent value="approval"><div className="flex flex-col gap-4">
          {detail.mutationRequest ? <div className="grid grid-cols-2 gap-4 md:grid-cols-4">
            <Fact label="Request ID">{detail.mutationRequest.requestId}</Fact>
            <Fact label="Request state">{detail.mutationRequest.state}</Fact>
            {detail.mutationRequest.status != null && <Fact label="Recorded status">{detail.mutationRequest.status}</Fact>}
            {detail.mutationRequest.verification != null && <Fact label="Verification">{detail.mutationRequest.verification}</Fact>}
            <Fact label="Created at">{detail.mutationRequest.createdAt}</Fact>
            {detail.mutationRequest.decidedAt != null && <Fact label="Decided at">{detail.mutationRequest.decidedAt}</Fact>}
            {detail.mutationRequest.finishedAt != null && <Fact label="Finished at">{detail.mutationRequest.finishedAt}</Fact>}
          </div> : <p className="text-sm">No linked approval lifecycle was recorded for this operation.</p>}
          <p className="text-sm">View requests in <Link to="/mutations" className="underline">Mutations</Link>.</p>
        </div></TabsContent>
        <TabsContent value="timing"><OperationTimings detail={detail} /></TabsContent>
        <TabsContent value="details">      {op.operation === "measure" && <Card>
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

      <Collapsible className="rounded-lg border p-4"><CollapsibleTrigger className="cursor-pointer font-medium">Run diagnostics</CollapsibleTrigger><CollapsibleContent keepMounted><div className="mt-4 space-y-4">
        <Card><CardHeader><CardTitle>Output size</CardTitle></CardHeader><CardContent className="grid grid-cols-2 gap-4 md:grid-cols-4">
          <Fact label="Output bytes (raw / emitted)">{formatNumber(detail.rawBytes)} / {formatNumber(detail.emittedBytes)}</Fact>
          <Fact label="Estimated output tokens (raw / emitted)">{formatNumber(detail.rawTokens)} / {formatNumber(detail.emittedTokens)}</Fact>
          <Fact label="Token estimation method">{detail.tokenEstimationMethod}; UTF-8 bytes divided by four, rounded up</Fact>
        </CardContent></Card>
        {detail.agent && <Card><CardHeader><CardTitle><h2>Agent call</h2></CardTitle></CardHeader><CardContent className="grid grid-cols-2 gap-4 md:grid-cols-4">
          <Fact label="Model">{detail.agent.model ?? detail.agent.transcriptModel ?? "—"}{!detail.agent.model && detail.agent.transcriptModel && <span className="ml-1 text-sm text-muted-foreground">from transcript</span>}</Fact>
          <Fact label="Effort">{detail.agent.reasoningEffort ?? detail.agent.transcriptReasoningEffort ?? "—"}{!detail.agent.reasoningEffort && detail.agent.transcriptReasoningEffort && <span className="ml-1 text-sm text-muted-foreground">from transcript</span>}</Fact>
          {detail.agent.conversationTitle && <Fact label="Conversation">{detail.agent.conversationTitle}</Fact>}<Fact label="Agent session">{detail.agent.sessionId ?? "—"}</Fact><Fact label="Turn">{detail.agent.turnId ?? "—"}</Fact><Fact label="Trigger">{detail.agent.turnTrigger ?? "—"}</Fact><Fact label="Thread source">{detail.agent.threadSource ?? "—"}</Fact><Fact label="Call id">{detail.agent.callId ?? "—"}</Fact>
        </CardContent></Card>}
      </div></CollapsibleContent></Collapsible>

        </TabsContent>
      </Tabs>
      <StoredSqlReuse detail={detail} />
    </div>
  )
}

function CompareOperationView({ id, detail, returnSearch }: { id: number; detail: OperationDetail; returnSearch: OperationDetailSearch }) {
  const op = detail.operation
  const navigate = useNavigate({ from: "/operations/$id" })
  const [comparisonTab, setComparisonTab] = useState("diff")
  const selectedCase = returnSearch.case ?? "all"
  const aggregate = returnSearch.agg ?? "sum"
  const view = returnSearch.view ?? "changed"
  const comparison = useOperationOptimization(id, selectedCase, aggregate, view)
  const returnSession = returnSearch.returnSession === op.sessionId ? returnSearch.returnSession : undefined
  const returnAgent = agentReturnKey(returnSearch)
  const returnLink = returnAgent ? <Link aria-label="Back to session Optimization" to="/agent-sessions/$kind/$id" params={{ kind: returnAgent.agentKind, id: returnAgent.conversationId }} search={sessionSearchFromOperation(returnSearch)} className="underline">Open in session Optimization →</Link> : <Link aria-label="Back to session Optimization" to="/sessions/$id" params={{ id: String(op.sessionId) }} search={returnSession ? sessionSearchFromOperation(returnSearch) : undefined} className="underline">Open in session Optimization →</Link>
  if (comparison.error) return <ErrorState error={comparison.error} />
  return <div className="operation-page space-y-4">
    <OperationHeading detail={detail} returnSearch={returnSearch} />

    <ComparisonPending pending={comparison.isPlaceholderData}><div className="operation-metrics" aria-label="Operation metrics">
      <Fact label="Logical reads">{formatNumber(comparison.data?.wholeBatch?.after ?? op.logicalReadsMedian)}</Fact>
      <Fact label="Elapsed median">{formatDuration(comparison.data?.elapsed.afterMs ?? null)}</Fact>
      <Fact label="CPU">{formatDuration(comparison.data?.cpuAfterMs ?? null)}</Fact>
      <Fact label="Results">{comparison.data?.equivalenceLabel ?? "—"}</Fact>
    </div></ComparisonPending>

    {comparison.isPending ? <Skeleton className="h-64 w-full" /> : comparison.data ? <>
      <ComparisonPanel pending={comparison.isPlaceholderData} navigation={returnLink} headerContent={detail.vars && <div className="operation-parameters"><span>Parameters</span><div aria-label="Variables" className="flex flex-wrap gap-2">{Object.entries(detail.vars).map(([key,value]) => <code key={key}>@{key} = {value}</code>)}</div></div>} comparison={comparison.data} sessionId={op.sessionId} agentSessionKey={returnAgent} subject={detail.compareSubject ?? "Unassigned"} scope={detail.compareScope ?? "whole query"} engine={op.engine} hideCaseSelector activeTab={comparisonTab} onTabChange={setComparisonTab} details={<><CompareDetail detail={detail} selectedCase={selectedCase === "all" ? comparison.data?.selectedCase ?? "all" : selectedCase} onCaseChange={value => { void navigate({ search: previous => ({ ...previous, case: value }), resetScroll: false }) }} />    <Collapsible className="rounded-lg border p-4"><CollapsibleTrigger className="cursor-pointer font-medium">Run diagnostics</CollapsibleTrigger><CollapsibleContent keepMounted><div className="mt-4 space-y-4">
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
    </div></CollapsibleContent></Collapsible></>} diff={<SqlDiff embedded baseline={detail.sqlText} candidate={detail.candidateSqlText} engine={op.engine} />} timing={<OperationTimings detail={detail} />}
        searchState={{ case: selectedCase, agg: aggregate, view }} sessionSearch={sessionSearchFromOperation(returnSearch)}
        onSelectionChange={patch => { void navigate({ search: previous => ({ ...previous, ...patch }), resetScroll: false }) }} />

    </> : null}
    <StoredSqlReuse detail={detail} />

  </div>
}

function agentReturnKey(search: OperationDetailSearch): AgentSessionKey | undefined {
  return search.returnAgentKind && search.returnAgentId
    ? { agentKind: search.returnAgentKind, conversationId: search.returnAgentId }
    : undefined
}
