import { useState } from "react"
import { Button } from "@/components/ui/button"
import { boundedPreview, diffTooLarge } from "@/lib/sqlDiff"

function lineDiff(baseline: string, candidate: string): string[] {
  const before = baseline.split(/\r?\n/)
  const after = candidate.split(/\r?\n/)
  let prefix = 0
  while (prefix < before.length && prefix < after.length && before[prefix] === after[prefix]) prefix++
  let suffix = 0
  while (suffix < before.length - prefix && suffix < after.length - prefix &&
    before[before.length - 1 - suffix] === after[after.length - 1 - suffix]) suffix++
  const lines = before.slice(0, prefix).map(line => `  ${line}`)
  lines.push(...before.slice(prefix, before.length - suffix).map(line => `- ${line}`))
  lines.push(...after.slice(prefix, after.length - suffix).map(line => `+ ${line}`))
  if (suffix > 0) lines.push(...before.slice(before.length - suffix).map(line => `  ${line}`))
  return lines
}

export function SqlDiff({ baseline, candidate }: { baseline: string | null; candidate: string | null }) {
  const [showFull, setShowFull] = useState(false)
  const hasBoth = baseline !== null && candidate !== null
  const tooLarge = hasBoth && diffTooLarge(baseline, candidate)

  return (
    <div className="space-y-4">
      {!hasBoth ? (
        <p className="text-sm text-muted-foreground">SQL text is not stored for both sides (journal.storeSensitive is off or historical text is unavailable).</p>
      ) : tooLarge ? (
        <div className="space-y-3">
          <p className="text-sm text-muted-foreground">Diff unavailable: SQL exceeds the 256 KiB or 4,000 line limit.</p>
          <div className="grid gap-3 md:grid-cols-2">
            <section aria-label="Baseline bounded SQL preview"><h3 className="font-medium">Baseline preview</h3><pre className="max-h-64 overflow-auto whitespace-pre-wrap break-words rounded border p-3 font-mono text-xs">{boundedPreview(baseline)}</pre></section>
            <section aria-label="Candidate bounded SQL preview"><h3 className="font-medium">Candidate preview</h3><pre className="max-h-64 overflow-auto whitespace-pre-wrap break-words rounded border p-3 font-mono text-xs">{boundedPreview(candidate)}</pre></section>
          </div>
        </div>
      ) : (
        <pre aria-label="SQL diff" className="max-h-[32rem] overflow-auto whitespace-pre-wrap break-words rounded border p-3 font-mono text-xs">{lineDiff(baseline, candidate).join("\n")}</pre>
      )}
      {(baseline !== null || candidate !== null) && (
        <div>
          <Button variant="outline" size="sm" aria-expanded={showFull} onClick={() => setShowFull(value => !value)}>
            {showFull ? "Hide full SQL versions" : "Show full SQL versions"}
          </Button>
          {showFull && <div className="mt-3 grid gap-3 md:grid-cols-2">
            <section aria-label="Baseline full SQL"><h3 className="font-medium">Baseline full SQL</h3>{baseline === null ? <p className="text-sm text-muted-foreground">Unavailable in journal history.</p> : <pre className="max-h-[32rem] overflow-auto whitespace-pre-wrap break-words rounded border p-3 font-mono text-xs">{baseline}</pre>}</section>
            <section aria-label="Candidate full SQL"><h3 className="font-medium">Candidate full SQL</h3>{candidate === null ? <p className="text-sm text-muted-foreground">Unavailable in journal history.</p> : <pre className="max-h-[32rem] overflow-auto whitespace-pre-wrap break-words rounded border p-3 font-mono text-xs">{candidate}</pre>}</section>
          </div>}
        </div>
      )}
    </div>
  )
}
