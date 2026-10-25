import { useState } from "react"
import { NotFoundError } from "@/api/client"
import { useSessionSqlText } from "@/api/queries"
import type { AgentSessionKey } from "@/api/types"
import { CopyButton } from "@/components/CopyButton"
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { DiffControls } from "@/components/SqlDiff"
import { LazySqlMergeView, loadUnifiedDiffText } from "@/components/SqlEditors"
import type { SqlVersionOption } from "@/lib/compare"
import { boundedPreview, diffTooLarge, dialectOf, normalizeForDiff, readPreference, type DiffMode, writePreference } from "@/lib/sqlDiff"

const MODE_KEY = "sqlharness.sqlDiff.mode"
const FORMATTING_KEY = "sqlharness.sqlDiff.ignoreFormatting"

function keyOf(option: SqlVersionOption | undefined): string | null {
  return option ? JSON.stringify([option.version, option.hash]) : null
}

function selectedOption(options: SqlVersionOption[], key: string | null): SqlVersionOption | undefined {
  return options.find(option => keyOf(option) === key)
}

function sideState(version: string, query: ReturnType<typeof useSessionSqlText>) {
  if (query.isPending) return <p role="status">Loading SQL for {version}…</p>
  if (query.error instanceof NotFoundError) return <p role="status">{version} is no longer in the retained history.</p>
  if (query.error) return <p role="alert">Could not load SQL for {version}: {query.error.message}</p>
  if (query.data?.tooLarge) return <p role="status">{version} is larger than 256 KiB; open a compare to see a bounded preview.</p>
  if (query.data?.text === null || query.data?.text === undefined) return <p role="status">SQL text for {version} is not stored (journal.storeSensitive was off).</p>
  return null
}

export function SessionVersionDiff({ sessionId, engine, options, initialFrom, initialTo, agentSessionKey, selectionControls = true }: {
  sessionId: number
  agentSessionKey?: AgentSessionKey
  engine: string | null
  options: SqlVersionOption[]
  selectionControls?: boolean
  initialFrom?: string
  initialTo?: string
}) {
  const [fromSelection, setFromSelection] = useState<string | null>(() => keyOf(options.find(option => option.version === initialFrom) ?? options[0]))
  const [toSelection, setToSelection] = useState<string | null>(() => keyOf(options.find(option => option.version === initialTo) ?? options.at(-1)))
  const fromOption = selectedOption(options, fromSelection) ?? options[0]
  const toOption = selectedOption(options, toSelection) ?? options.at(-1)
  const selectItems = options.map(option => ({ value: keyOf(option)!, label: option.label ?? option.version }))
  const fromQuery = useSessionSqlText(sessionId, fromOption?.hash ?? null, agentSessionKey)
  const toQuery = useSessionSqlText(sessionId, toOption?.hash ?? null, agentSessionKey)
  const [mode, setMode] = useState<DiffMode>(() => readPreference(MODE_KEY, ["side-by-side", "unified"], "side-by-side"))
  const [ignoreFormatting, setIgnoreFormatting] = useState(() => readPreference(FORMATTING_KEY, ["true", "false"], "false") === "true")

  function changeMode(value: DiffMode) {
    setMode(value)
    writePreference(MODE_KEY, value)
  }

  function changeIgnoreFormatting(value: boolean) {
    setIgnoreFormatting(value)
    writePreference(FORMATTING_KEY, String(value))
  }

  const fromText = fromQuery.data?.text ?? null
  const toText = toQuery.data?.text ?? null
  const fromState = fromOption ? sideState(fromOption.version, fromQuery) : null
  const toState = toOption ? sideState(toOption.version, toQuery) : null
  const rawTooLarge = fromText !== null && toText !== null && diffTooLarge(fromText, toText)
  let normalizedFrom: string | null = null
  let normalizedTo: string | null = null
  let formatFailed = false
  if (fromText !== null && toText !== null && !rawTooLarge) {
    const before = normalizeForDiff(fromText, engine, ignoreFormatting)
    const after = normalizeForDiff(toText, engine, ignoreFormatting)
    formatFailed = before.formatFailed || after.formatFailed
    if (formatFailed) {
      normalizedFrom = normalizeForDiff(fromText, engine, false).text
      normalizedTo = normalizeForDiff(toText, engine, false).text
    } else {
      normalizedFrom = before.text
      normalizedTo = after.text
    }
  }
  const normalizedTooLarge = normalizedFrom !== null && normalizedTo !== null && diffTooLarge(normalizedFrom, normalizedTo)
  const diffUnavailable = rawTooLarge || normalizedTooLarge

  async function getDiffText() {
    if (normalizedFrom === null || normalizedTo === null || diffUnavailable) return "Diff unavailable: SQL exceeds the 256 KiB or 4,000 line limit."
    const unifiedDiffText = await loadUnifiedDiffText()
    return unifiedDiffText(normalizedFrom, normalizedTo)
  }

  return <div className="session-version-diff space-y-3">
    {selectionControls ? <div className="grid gap-3 sm:grid-cols-2">
      <label className="grid min-w-0 gap-1 text-sm">From version
        <Select items={selectItems} value={keyOf(fromOption)} onValueChange={value => setFromSelection(value)}>
          <SelectTrigger aria-label="From version" className="w-full min-w-0"><SelectValue placeholder="No versions available" /></SelectTrigger>
          <SelectContent>{options.map(option => <SelectItem key={keyOf(option)!} value={keyOf(option)!}>{option.label ?? option.version}</SelectItem>)}</SelectContent>
        </Select>
      </label>
      <label className="grid min-w-0 gap-1 text-sm">To version
        <Select items={selectItems} value={keyOf(toOption)} onValueChange={value => setToSelection(value)}>
          <SelectTrigger aria-label="To version" className="w-full min-w-0"><SelectValue placeholder="No versions available" /></SelectTrigger>
          <SelectContent>{options.map(option => <SelectItem key={keyOf(option)!} value={keyOf(option)!}>{option.label ?? option.version}</SelectItem>)}</SelectContent>
        </Select>
      </label>
    </div> : null}
    <div className="space-y-3">
      {fromState}
      {toState}
      {fromText !== null && toText !== null && fromOption && toOption && (
        diffUnavailable ? <div className="space-y-2">
          <p className="text-sm text-muted-foreground">Diff unavailable: SQL exceeds the 256 KiB or 4,000 line limit.</p>
          <section aria-label="From bounded SQL preview"><h3 className="font-medium">{fromOption.version} preview</h3><pre className="max-h-64 overflow-auto whitespace-pre-wrap break-words rounded border p-3 font-mono text-xs">{boundedPreview(fromText)}</pre></section>
          <section aria-label="To bounded SQL preview"><h3 className="font-medium">{toOption.version} preview</h3><pre className="max-h-64 overflow-auto whitespace-pre-wrap break-words rounded border p-3 font-mono text-xs">{boundedPreview(toText)}</pre></section>
        </div> : <>
          <div className="flex flex-wrap items-center gap-3">
            <DiffControls mode={mode} onModeChange={changeMode} ignoreFormatting={ignoreFormatting} onIgnoreFormattingChange={changeIgnoreFormatting} />
            <CopyButton label="Copy diff" getText={getDiffText} />
          </div>
          {formatFailed && <p className="text-sm text-muted-foreground">Formatting failed; showing the SQL as written.</p>}
          <LazySqlMergeView baseline={normalizedFrom!} candidate={normalizedTo!} dialect={dialectOf(engine)} mode={mode} />
        </>
      )}
    </div>
    <p className="text-sm text-muted-foreground">SQL only. Metrics live on each compare; different compares are not compared with each other.</p>
  </div>
}
