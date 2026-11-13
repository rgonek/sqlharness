import { useId, useState, type ReactNode } from "react"
import { CopyButton } from "@/components/CopyButton"
import { LazySqlMergeView, LazySqlViewer, loadUnifiedDiffText } from "@/components/SqlEditors"
import { Checkbox } from "@/components/ui/checkbox"
import { Label } from "@/components/ui/label"
import { SegmentedRadioGroup } from "@/components/SegmentedRadioGroup"
import { Tabs, TabsContent, TabsList, TabsTrigger } from "@/components/ui/tabs"
import { boundedPreview, diffTooLarge, dialectOf, MAX_DIFF_BYTES, normalizeForDiff, readPreference, utf8BytesWithin, writePreference, type DiffMode, type SqlTab } from "@/lib/sqlDiff"

const TAB_KEY = "sqlharness.sqlDiff.tab"
const MODE_KEY = "sqlharness.sqlDiff.mode"
const FORMATTING_KEY = "sqlharness.sqlDiff.ignoreFormatting"

export function DiffControls({ mode, onModeChange, ignoreFormatting, onIgnoreFormattingChange }: {
  mode: DiffMode
  onModeChange: (mode: DiffMode) => void
  ignoreFormatting: boolean
  onIgnoreFormattingChange: (value: boolean) => void
}) {
  const id = useId()
  const ignoreFormattingId = `${id}-ignore-formatting`
  return <div className="diff-controls flex flex-wrap items-center gap-3">
    <SegmentedRadioGroup label="Diff mode" value={mode} options={[{ value: "side-by-side", label: "Side by side" }, { value: "unified", label: "Unified" }]} onValueChange={value => {
      if (value === "side-by-side" || value === "unified") onModeChange(value)
    }} />
    <div className="flex items-center gap-2">
      <Checkbox id={ignoreFormattingId} checked={ignoreFormatting} onCheckedChange={onIgnoreFormattingChange} />
      <Label htmlFor={ignoreFormattingId}>Ignore formatting</Label>
    </div>
  </div>
}

function SideActions({ label, sql, children }: { label: "baseline" | "candidate"; sql: string; children?: ReactNode }) {
  return <div role="group" aria-label={`${label === "baseline" ? "Baseline" : "Candidate"} SQL actions`} className="flex min-w-0 flex-wrap items-center gap-2">
    <CopyButton label={`Copy ${label} SQL`} scopeKey={sql} showLabel getText={() => sql} />
    {children}
  </div>
}

function SideContent({ label, sql, engine, actions }: { label: "baseline" | "candidate"; sql: string; engine: string | null; actions?: ReactNode }) {
  return <div className="space-y-2">
    <div className="flex flex-wrap items-center justify-between gap-2">
      <h3 className="font-medium">{label === "baseline" ? "Baseline SQL" : "Candidate SQL"}</h3>
      <SideActions label={label} sql={sql}>{actions}</SideActions>
    </div>
    {utf8BytesWithin(sql, MAX_DIFF_BYTES) === null
      ? <pre className="max-h-[32rem] overflow-auto whitespace-pre-wrap break-words rounded border p-3 font-mono text-sm">{boundedPreview(sql)}</pre>
      : <LazySqlViewer sql={sql} dialect={dialectOf(engine)} />}
  </div>
}

function TooLargeMessage({ baseline, candidate }: { baseline: string; candidate: string }) {
  return <div className="space-y-3">
    <p className="text-sm text-muted-foreground">Diff unavailable: SQL exceeds the 256 KiB or 4,000 line limit.</p>
    <div className="grid gap-3 md:grid-cols-2">
      <section aria-label="Baseline bounded SQL preview"><h3 className="font-medium">Baseline preview</h3><pre className="max-h-64 overflow-auto whitespace-pre-wrap break-words rounded border p-3 font-mono text-sm">{boundedPreview(baseline)}</pre></section>
      <section aria-label="Candidate bounded SQL preview"><h3 className="font-medium">Candidate preview</h3><pre className="max-h-64 overflow-auto whitespace-pre-wrap break-words rounded border p-3 font-mono text-sm">{boundedPreview(candidate)}</pre></section>
    </div>
  </div>
}

export function SqlDiff({ baseline, candidate, engine, embedded = false, baselineActions, candidateActions }: {
  baseline: string | null; candidate: string | null; engine: string | null; embedded?: boolean
  baselineActions?: ReactNode; candidateActions?: ReactNode
}) {
  const hasBaseline = baseline !== null
  const hasCandidate = candidate !== null
  const hasBoth = hasBaseline && hasCandidate
  const availableTabs: SqlTab[] = [
    ...(hasBaseline ? ["baseline" as const] : []),
    ...(hasCandidate ? ["candidate" as const] : []),
    ...(hasBoth ? ["diff" as const] : []),
  ]
  const [tab, setTab] = useState<SqlTab>(() => readPreference<SqlTab>(TAB_KEY, availableTabs, availableTabs.at(-1) ?? "baseline"))
  const [mode, setMode] = useState<DiffMode>(() => readPreference(MODE_KEY, ["side-by-side", "unified"], "side-by-side"))
  const [ignoreFormatting, setIgnoreFormatting] = useState(() => readPreference(FORMATTING_KEY, ["true", "false"], "false") === "true")
  const tooLargeRaw = hasBoth && diffTooLarge(baseline, candidate)

  function changeTab(value: string | null) {
    if (value === null || !availableTabs.includes(value as SqlTab)) return
    setTab(value as SqlTab)
    writePreference(TAB_KEY, value)
  }

  function changeMode(value: DiffMode) {
    setMode(value)
    writePreference(MODE_KEY, value)
  }

  function changeIgnoreFormatting(value: boolean) {
    setIgnoreFormatting(value)
    writePreference(FORMATTING_KEY, String(value))
  }

  let normalizedBaseline: string | null = null
  let normalizedCandidate: string | null = null
  let formatFailed = false
  if (hasBoth && !tooLargeRaw) {
    const before = normalizeForDiff(baseline, engine, ignoreFormatting)
    const after = normalizeForDiff(candidate, engine, ignoreFormatting)
    formatFailed = before.formatFailed || after.formatFailed
    normalizedBaseline = formatFailed ? normalizeForDiff(baseline, engine, false).text : before.text
    normalizedCandidate = formatFailed ? normalizeForDiff(candidate, engine, false).text : after.text
  }
  const tooLargeNormalized = normalizedBaseline !== null && normalizedCandidate !== null && diffTooLarge(normalizedBaseline, normalizedCandidate)
  const diffUnavailable = tooLargeRaw || tooLargeNormalized

  async function getDiffText() {
    if (normalizedBaseline === null || normalizedCandidate === null || diffUnavailable) return "Diff unavailable: SQL exceeds the 256 KiB or 4,000 line limit."
    const unifiedDiffText = await loadUnifiedDiffText()
    return unifiedDiffText(normalizedBaseline, normalizedCandidate)
  }

  const copySides = embedded && hasBoth ? <div className="flex min-w-0 flex-wrap gap-3">
    <SideActions label="baseline" sql={baseline}>{baselineActions}</SideActions>
    <SideActions label="candidate" sql={candidate}>{candidateActions}</SideActions>
  </div> : null

  return <div className="sql-diff space-y-4">
    {!hasBoth && <p className="text-sm text-muted-foreground">SQL text is not stored for both sides (journal.storeSensitive is off or historical text is unavailable).</p>}
    {availableTabs.length > 0 && <Tabs value={embedded && hasBoth ? "diff" : availableTabs.includes(tab) ? tab : availableTabs.at(-1) ?? "baseline"} onValueChange={changeTab}>
      {!(embedded && hasBoth) && <TabsList aria-label="SQL versions" className="grid h-auto w-full grid-cols-3">
        {hasBaseline && <TabsTrigger value="baseline">Baseline</TabsTrigger>}
        {hasCandidate && <TabsTrigger value="candidate">Candidate</TabsTrigger>}
        {hasBoth && <TabsTrigger value="diff">Diff</TabsTrigger>}
      </TabsList>}
      {hasBaseline && <TabsContent value="baseline"><SideContent label="baseline" sql={baseline} engine={engine} actions={baselineActions} /></TabsContent>}
      {hasCandidate && <TabsContent value="candidate"><SideContent label="candidate" sql={candidate} engine={engine} actions={candidateActions} /></TabsContent>}
      {hasBoth && <TabsContent value="diff" className="space-y-3">{diffUnavailable && copySides}
        {tooLargeRaw ? <TooLargeMessage baseline={baseline} candidate={candidate} /> : tooLargeNormalized ? <TooLargeMessage baseline={baseline} candidate={candidate} /> : <>
          <div className="flex flex-wrap items-center gap-3">
            <DiffControls mode={mode} onModeChange={changeMode} ignoreFormatting={ignoreFormatting} onIgnoreFormattingChange={changeIgnoreFormatting} />
            {copySides}<CopyButton label="Copy diff" scopeKey={`${normalizedBaseline}\0${normalizedCandidate}`} showLabel getText={getDiffText} />
          </div>
          {formatFailed && <p className="text-sm text-muted-foreground">Formatting failed; showing the SQL as written.</p>}
          <LazySqlMergeView baseline={normalizedBaseline!} candidate={normalizedCandidate!} dialect={dialectOf(engine)} mode={mode} />
        </>}
      </TabsContent>}
    </Tabs>}
  </div>
}
