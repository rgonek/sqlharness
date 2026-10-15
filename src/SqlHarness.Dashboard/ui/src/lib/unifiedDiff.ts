import { structuredPatch } from "diff"
import { diffTooLarge } from "./sqlDiff"

export function unifiedDiffText(baseline: string, candidate: string, context = 3): string {
  if (diffTooLarge(baseline, candidate)) throw new RangeError("SQL exceeds the 256 KiB or 4,000 line diff limit.")
  if (!Number.isInteger(context) || context < 0) throw new RangeError("Diff context must be a non-negative integer.")

  const oldText = baseline.endsWith("\n") ? baseline : `${baseline}\n`
  const newText = candidate.endsWith("\n") ? candidate : `${candidate}\n`
  const patch = structuredPatch("", "", oldText, newText, "", "", { context })
  return patch.hunks.flatMap(hunk => [
    `@@ -${hunk.oldStart},${hunk.oldLines} +${hunk.newStart},${hunk.newLines} @@`,
    ...hunk.lines,
  ]).join("\n")
}
