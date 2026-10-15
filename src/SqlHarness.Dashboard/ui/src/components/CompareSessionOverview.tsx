import { Link } from "@tanstack/react-router"
import { useState } from "react"
import { useSessionCompares } from "@/api/queries"
import type { CompareAttemptSummary, CompareSubjectSummary } from "@/api/types"
import { Button } from "@/components/ui/button"
import { SessionVersionDiff } from "@/components/SessionVersionDiff"
import { regressionLabel, versionOptions } from "@/lib/compare"
import { formatTimestamp } from "@/lib/format"

function assessment(attempt: CompareAttemptSummary | null): string {
  if (!attempt || attempt.status !== "succeeded") return "No assessment"
  return regressionLabel(attempt.regression)
}

function attemptVersions(attempt: CompareAttemptSummary) {
  return `${attempt.baselineVersion ?? "unknown"} → ${attempt.candidateVersion ?? "unknown"}`
}

function AttemptLink({ attempt, sessionId, returnUnassigned = false }: { attempt: CompareAttemptSummary; sessionId: number; returnUnassigned?: boolean }) {
  return (
    <li className="flex flex-wrap items-center gap-x-3 gap-y-1 py-2">
      <Link
        to="/operations/$id"
        params={{ id: String(attempt.operationId) }}
        search={{
          returnSession: sessionId,
          returnSubject: returnUnassigned ? undefined : attempt.subject ?? undefined,
          returnScope: returnUnassigned ? undefined : attempt.scope ?? undefined,
          returnUnassigned: returnUnassigned || undefined,
        }}
      >
        Compare #{attempt.operationId}
      </Link>
      <span>{attemptVersions(attempt)}</span>
      <span>{attempt.status}</span>
      <span className="text-muted-foreground">{formatTimestamp(attempt.startedAt)}</span>
    </li>
  )
}

function ScopeOverview({ subject, scope, sessionId, engine, attempts, shouldOpen }: {
  subject: string
  scope: string
  sessionId: number
  engine: string | null
  attempts: CompareAttemptSummary[]
  shouldOpen: boolean
}) {
  const [showDiff, setShowDiff] = useState(false)
  const options = versionOptions(attempts, subject, scope)
  const group = attempts.filter(item => item.subject === subject && item.scope === scope)
    .sort((left, right) => left.startedAt.localeCompare(right.startedAt) || left.operationId - right.operationId)
  return <details className="rounded-md border px-3" open={shouldOpen}>
    <summary className="cursor-pointer py-2">{scope}</summary>
    {group.length ? <>
      {options.length >= 2 && <div className="py-2">
        <Button type="button" variant="outline" size="sm" aria-expanded={showDiff} onClick={() => setShowDiff(value => !value)}>
          {showDiff ? "Hide version diff" : "Diff versions"}
        </Button>
        {showDiff && <div className="mt-3"><SessionVersionDiff sessionId={sessionId} engine={engine} options={options} /></div>}
      </div>}
      <ol className="divide-y" aria-label={`${subject} ${scope} compare attempts`}>
        {group.map(attempt => <AttemptLink key={attempt.operationId} attempt={attempt} sessionId={sessionId} />)}
      </ol>
    </> : <p className="pb-2 text-sm text-muted-foreground">No attempts loaded for this scope yet.</p>}
  </details>
}

function SubjectOverview({ subject, sessionId, engine, attempts, returnSubject, returnScope }: {
  subject: CompareSubjectSummary
  sessionId: number
  engine: string | null
  attempts: CompareAttemptSummary[]
  returnSubject?: string
  returnScope?: string
}) {
  const latest = subject.latestCompletedWhole
  const started = subject.latestWhole
  const shouldOpen = returnSubject === subject.subject
  const scopes = [...new Set([...subject.scopes, ...attempts.filter(item => item.subject === subject.subject).map(item => item.scope).filter((scope): scope is string => Boolean(scope))])]

  return (
    <details className="rounded-lg border p-3" open={shouldOpen}>
      <summary className="cursor-pointer font-medium">{subject.subject}</summary>
      <div className="mt-3 grid gap-3">
        <div className="grid gap-1 text-sm">
          {started?.status === "running" && <p>Whole compare in progress{latest ? "; latest completed result shown below" : "; no result yet"}.</p>}
          {latest ? (
            <p>Latest completed whole compare: {latest.status}; {attemptVersions(latest)}; {assessment(latest)}.</p>
          ) : started?.status !== "running" ? (
            <p>No final whole compare</p>
          ) : null}
          {subject.startToLatestWhole && (
            <p>Direct whole compare from retained-history v0: {attemptVersions(subject.startToLatestWhole)}; {assessment(subject.startToLatestWhole)}.</p>
          )}
          {subject.versionsFromRetainedHistory && <p className="text-muted-foreground">Versions use retained history; labels can change after retention.</p>}
          <p className="text-muted-foreground">SQL hashes define versions; no winning version is selected.</p>
        </div>
        {scopes.map(scope => {
          return <ScopeOverview key={scope} subject={subject.subject} scope={scope} sessionId={sessionId} engine={engine}
            attempts={attempts} shouldOpen={shouldOpen && returnScope === scope} />
        })}
      </div>
    </details>
  )
}

export function CompareSessionOverview({ sessionId, engine, subjects, unassignedCompareCount, returnSubject, returnScope, returnUnassigned = false }: {
  sessionId: number
  engine: string | null
  subjects: CompareSubjectSummary[]
  unassignedCompareCount: number
  returnSubject?: string
  returnScope?: string
  returnUnassigned?: boolean
}) {
  const query = useSessionCompares(sessionId)
  const attempts = query.data?.pages.flatMap(page => page.items) ?? []
  const unassigned = attempts.filter(attempt => !attempt.subject || !attempt.scope)
    .sort((left, right) => left.startedAt.localeCompare(right.startedAt) || left.operationId - right.operationId)

  return (
    <section className="space-y-3" aria-labelledby="compare-history-heading">
      <div>
        <h2 id="compare-history-heading" className="font-heading text-lg font-medium">Optimization history</h2>
        <p className="text-sm text-muted-foreground">Direct compare attempts for this session. Other operations remain in All operations.</p>
      </div>
      {query.isPending ? <p role="status">Loading compare attempts…</p> : null}
      {query.error && <div role="alert" className="flex flex-wrap items-center gap-3"><span>Could not load compare attempts: {query.error.message}</span><Button variant="outline" onClick={() => void query.refetch()}>Retry</Button></div>}
      {!query.isPending && !query.error && attempts.length === 0 && subjects.length === 0 && unassignedCompareCount === 0 && <p>No compare attempts in this session.</p>}
      {subjects.map(subject => (
        <SubjectOverview key={subject.subject} subject={subject} sessionId={sessionId} engine={engine} attempts={attempts} returnSubject={returnSubject} returnScope={returnScope} />
      ))}
      {unassignedCompareCount > 0 && (
        <details className="rounded-lg border p-3" open={returnUnassigned}>
          <summary className="cursor-pointer font-medium">Unassigned context ({unassignedCompareCount})</summary>
          <p className="mt-2 text-sm text-muted-foreground">Legacy compares without a complete subject and scope remain separate; they are not treated as whole compares.</p>
          {unassigned.length ? <ol className="divide-y">{unassigned.map(attempt => <AttemptLink key={attempt.operationId} attempt={attempt} sessionId={sessionId} returnUnassigned />)}</ol> : <p className="mt-2 text-sm text-muted-foreground">No unassigned attempts loaded on this page.</p>}
        </details>
      )}
      {query.hasNextPage && <Button variant="outline" disabled={query.isFetchingNextPage} onClick={() => void query.fetchNextPage()}>{query.isFetchingNextPage ? "Loading more…" : "Load more compare attempts"}</Button>}
      {query.isFetchNextPageError && <p role="alert">Could not load the next page. Your loaded compare history is still available.</p>}
    </section>
  )
}
