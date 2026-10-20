import { useId, useState } from "react"
import { CopyButton } from "@/components/CopyButton"
import { LazySqlMergeView, LazySqlViewer, loadUnifiedDiffText } from "@/components/SqlEditors"
import { Checkbox } from "@/components/ui/checkbox"
import { Label } from "@/components/ui/label"
import { RadioGroup, RadioGroupItem } from "@/components/ui/radio-group"
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
  const sideBySideId = `${id}-side-by-side`
  const unifiedId = `${id}-unified`
  const ignoreFormattingId = `${id}-ignore-formatting`
  return <div className="flex flex-wrap items-center gap-3">
    <RadioGroup aria-label="Diff mode" className="flex gap-2" value={mode} onValueChange={value => {
      if (value === "side-by-side" || value === "unified") onModeChange(value)
    }}>
      <div className="flex items-center gap-1">
        <RadioGroupItem id={sideBySideId} value="side-by-side" />
        <Label htmlFor={sideBySideId}>Side by side</Label>
      </div>
      <div className="flex items-center gap-1">
        <RadioGroupItem id={unifiedId} value="unified" />
        <Label htmlFor={unifiedId}>Unified</Label>
      </div>
    </RadioGroup>
    <div className="flex items-center gap-2">
      <Checkbox id={ignoreFormattingId} checked={ignoreFormatting} onCheckedChange={onIgnoreFormattingChange} />
      <Label htmlFor={ignoreFormattingId}>Ignore formatting</Label>
    </div>
  </div>
}

function SideContent({ label, sql, engine }: { label: "baseline" | "candidate"; sql: string; engine: string | null }) {
  return <div className="space-y-2">
    <div className="flex items-center justify-between">
      <h3 className="font-medium">{label === "baseline" ? "Baseline SQL" : "Candidate SQL"}</h3>
      <CopyButton label={`Copy ${label} SQL`} getText={() => sql} />
    </div>
    {utf8BytesWithin(sql, MAX_DIFF_BYTES) === null
      ? <pre className="max-h-[32rem] overflow-auto whitespace-pre-wrap break-words rounded border p-3 font-mono text-xs">{boundedPreview(sql)}</pre>
      : <LazySqlViewer sql={sql} dialect={dialectOf(engine)} />}
  </div>
}

function TooLargeMessage({ baseline, candidate }: { baseline: string; candidate: string }) {
  return <div className="space-y-3">
    <p className="text-sm text-muted-foreground">Diff unavailable: SQL exceeds the 256 KiB or 4,000 line limit.</p>
    <div className="grid gap-3 md:grid-cols-2">
      <section aria-label="Baseline bounded SQL preview"><h3 className="font-medium">Baseline preview</h3><pre className="max-h-64 overflow-auto whitespace-pre-wrap break-words rounded border p-3 font-mono text-xs">{boundedPreview(baseline)}</pre></section>
      <section aria-label="Candidate bounded SQL preview"><h3 className="font-medium">Candidate preview</h3><pre className="max-h-64 overflow-auto whitespace-pre-wrap break-words rounded border p-3 font-mono text-xs">{boundedPreview(candidate)}</pre></section>
    </div>
  </div>
}

export function SqlDiff({ baseline, candidate, engine }: { baseline: string | null; candidate: string | null; engine: string | null }) {
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

  return <div className="space-y-4">
    {!hasBoth && <p className="text-sm text-muted-foreground">SQL text is not stored for both sides (journal.storeSensitive is off or historical text is unavailable).</p>}
    {availableTabs.length > 0 && <Tabs value={availableTabs.includes(tab) ? tab : availableTabs.at(-1) ?? "baseline"} onValueChange={changeTab}>
      <TabsList aria-label="SQL versions" className="grid h-auto w-full grid-cols-3">
        {hasBaseline && <TabsTrigger value="baseline">Baseline</TabsTrigger>}
        {hasCandidate && <TabsTrigger value="candidate">Candidate</TabsTrigger>}
        {hasBoth && <TabsTrigger value="diff">Diff</TabsTrigger>}
      </TabsList>
      {hasBaseline && <TabsContent value="baseline"><SideContent label="baseline" sql={baseline} engine={engine} /></TabsContent>}
      {hasCandidate && <TabsContent value="candidate"><SideContent label="candidate" sql={candidate} engine={engine} /></TabsContent>}
      {hasBoth && <TabsContent value="diff" className="space-y-3">
        {tooLargeRaw ? <TooLargeMessage baseline={baseline} candidate={candidate} /> : tooLargeNormalized ? <TooLargeMessage baseline={baseline} candidate={candidate} /> : <>
          <div className="flex flex-wrap items-center gap-3">
            <DiffControls mode={mode} onModeChange={changeMode} ignoreFormatting={ignoreFormatting} onIgnoreFormattingChange={changeIgnoreFormatting} />
            <CopyButton label="Copy diff" getText={getDiffText} />
          </div>
          {formatFailed && <p className="text-sm text-muted-foreground">Formatting failed; showing the SQL as written.</p>}
          <LazySqlMergeView baseline={normalizedBaseline!} candidate={normalizedCandidate!} dialect={dialectOf(engine)} mode={mode} />
        </>}
      </TabsContent>}
    </Tabs>}
  </div>
}
