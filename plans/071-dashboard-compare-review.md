# Plan 071: Dashboard compare review (SQL diff editor, IO and plan deltas, Copy for PR)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **STOP — operator checkpoint before any code.** The editor choice is provisional. Ask the operator: *"Plan 071 uses CodeMirror 6 (`@codemirror/merge` + `@codemirror/lang-sql`) for the SQL diff. Is that your final decision, or do you want another editor (e.g. Monaco)?"* Do not start Task 1 until they answer. If they choose something else, stop and revise the spec and this plan first.
>
> **Drift check (run first):** `git diff --stat 40a6871..HEAD -- src/SqlHarness.Dashboard README.md`. Plan 062 is DONE (`818f1e4`); this plan builds on its `CompareDetail`, `SqlDiff`, `CompareSessionOverview`, `lib/compare.ts` and `/api/sessions/{id}/compares`. Plans 069/070 may land before this one: they touch `SessionPage.tsx`, `DashboardSecurity.cs` and `JournalReader.cs`, not the compare components; merge around them. This plan has no journal schema change.

**Goal:** A reviewer-ready compare page: SQL in Baseline/Candidate/Diff tabs with side-by-side and unified diff, per-table reads and plan changes between the two sides, a one-click Markdown copy for the PR, and a version-to-version SQL diff on the session page.

**Architecture:** CodeMirror 6 is loaded lazily from one module (`components/sql-editor/codemirror.tsx`) that exports a read-only SQL viewer, a merge view (side-by-side or unified) and a pure `unifiedDiffText` helper built on `@codemirror/merge` chunks. Pure functions in `lib/` compute table IO deltas, plan changes and the PR Markdown; small components render them inside 062's `CompareDetail` tabs. One new bounded read endpoint returns a session's SQL text by hash for the version diff.

**Tech Stack:** React 19, TypeScript, Vite 8, TanStack Query, CodeMirror 6 (`@codemirror/state`, `@codemirror/view`, `@codemirror/merge`, `@codemirror/lang-sql`), `sql-formatter` (existing), Vitest + Testing Library; .NET 10 minimal API, xUnit.

**Spec:** [2026-10-14-dashboard-compare-review-design.md](../docs/superpowers/specs/2026-10-14-dashboard-compare-review-design.md)

## Status

- TODO; planned 2026-10-14 at `40a6871` after 062 landed; spec accepted by the operator 2026-10-14. Gated on the CodeMirror checkpoint above.
- Priority P2. Effort M–L. Risk LOW–MEDIUM (new frontend dependency in the embedded bundle; CSP; clipboard).

## Global Constraints

- CSP stays exactly `script-src 'self'` etc. (`DashboardSecurity.ApplyHeaders`): no CDN, no `blob:`/`data:` workers, no `eval`. CodeMirror needs no workers.
- CodeMirror code is reachable only through dynamic `import()` from `components/SqlEditors.tsx`; the main bundle must not grow by the editor (verify the Vite build output lists a separate chunk).
- Diff limits from 062 stay: 256 KiB total UTF-8 and 4000 lines; above them, a message and the Baseline/Candidate tabs still work (with the 062 bounded preview when the editor is also too large — reuse 062's helpers).
- `journal.storeSensitive` off (any side text `null`): keep 062's message; no Diff tab; Copy omits SQL.
- Dialect: `engine === "postgres"` → PostgreSQL (`sql-formatter` `postgresql`, `@codemirror/lang-sql` `PostgreSQL`); otherwise T-SQL (`transactsql`, `MSSQL`).
- Copy for PR **never** contains server, database, profile, vars, parameter values, setup SQL, artifact paths or measurement-condition payloads.
- No percentage when the baseline is `0` or `null` (062 rule, `compareRows`/`formatChange`).
- Different compares never form a metric trend: the session version diff shows SQL only.
- Remembered UI preferences (tab, diff mode, ignore formatting) use `localStorage` inside try/catch, keyed `sqlharness.sqlDiff.*`.
- No auto-formatting; touch only lines the change needs.
- Done = `npm run check`, `npm run build`, then `pwsh ./scripts/verify.ps1`, then `pwsh ./scripts/verify-linux.ps1`, one at a time, all green; plus the manual fixture check in Task 7.

## Review Focus

1. Matrix compare where one cell regresses on one table while the total improves: the per-cell IO table shows that table's increase; the Markdown names the cell (T3/T5 tests).
2. Same plan hash on both sides: "Same plan", no fetch of the second plan, no empty change list presented as "no changes" ambiguity (T4 test).
3. SQL with CRLF line endings on one side only: Ignore formatting off still shows a real diff only where text differs after normalising line endings; the copied unified diff has `\n` lines (T1 test).
4. Clipboard API rejected (insecure context, permission): the fallback dialog shows the full Markdown selected for manual copy (T5 test).
5. Session version diff for a hash whose text was never stored or whose operations were removed by retention: an explicit message, no endless spinner (T6 tests).

## File map

| File | Responsibility |
|---|---|
| `ui/package.json`, `package-lock.json` | CodeMirror dependencies |
| `ui/src/components/sql-editor/codemirror.tsx` (new) | lazy module: `SqlViewer`, `SqlMergeView`, `unifiedDiffText` |
| `ui/src/components/SqlEditors.tsx` (new) | `React.lazy` wrappers + loading fallback |
| `ui/src/lib/sqlDiff.ts` (new) | limits (moved from `SqlDiff.tsx`), dialects, normalisation, preferences |
| `ui/src/components/SqlDiff.tsx` | Baseline/Candidate/Diff tabs, controls, copy icons |
| `ui/src/components/CopyButton.tsx` (new) | clipboard with fallback dialog |
| `ui/src/lib/tableIoDelta.ts`, `ui/src/components/TableIoComparison.tsx` (new) | per-table reads B/C |
| `ui/src/lib/planChanges.ts`, `ui/src/components/PlanChanges.tsx` (new) | plan access/warning/top-cost changes |
| `ui/src/lib/prMarkdown.ts` (new) | Markdown for the PR |
| `ui/src/components/CompareDetail.tsx` | wiring: copy icon, Plans and IO tab, SqlDiff props |
| `src/SqlHarness.Dashboard/JournalReader.cs`, `DashboardModels.cs`, `DashboardServer.cs` | `GET /api/sessions/{id}/sql?hash=` |
| `ui/src/components/SessionVersionDiff.tsx` (new), `CompareSessionOverview.tsx` | version-to-version diff per scope group |
| `README.md`, `plans/README.md` | docs |

(`ui/` = `src/SqlHarness.Dashboard/ui/`. Run npm commands in `ui/`.)

---

### Task 1: CodeMirror module, lazy wrappers and diff helpers

**Files:**
- Modify: `ui/package.json`, `ui/package-lock.json`, `ui/src/components/SqlDiff.tsx` (only to import the moved limit helpers)
- Create: `ui/src/components/sql-editor/codemirror.tsx`, `ui/src/components/SqlEditors.tsx`, `ui/src/lib/sqlDiff.ts`
- Test: `ui/src/lib/sqlDiff.test.ts`, `ui/src/components/sql-editor/codemirror.test.ts`

**Interfaces:**
- Produces:
  - `lib/sqlDiff.ts`: `MAX_DIFF_BYTES = 256 * 1024`, `MAX_DIFF_LINES = 4000`, `PREVIEW_CHARS = 8 * 1024`, `boundedPreview(value)`, `diffTooLarge(baseline: string, candidate: string): boolean`, `type SqlDialect = "postgres" | "tsql"`, `dialectOf(engine: string | null | undefined): SqlDialect`, `normalizeForDiff(sql: string, engine, ignoreFormatting: boolean): { text: string; formatFailed: boolean }`, `type DiffMode = "side-by-side" | "unified"`, `type SqlTab = "baseline" | "candidate" | "diff"`, `readPreference<T extends string>(key: string, allowed: readonly T[], fallback: T): T`, `writePreference(key: string, value: string): void`.
  - `components/sql-editor/codemirror.tsx` (default-exported object is not needed; named exports): `SqlViewer({ sql, dialect }: { sql: string; dialect: SqlDialect })`, `SqlMergeView({ baseline, candidate, dialect, mode }: { baseline: string; candidate: string; dialect: SqlDialect; mode: DiffMode })`, `unifiedDiffText(baseline: string, candidate: string, context?: number): string`.
  - `components/SqlEditors.tsx`: `LazySqlViewer`, `LazySqlMergeView` (same props), and `loadUnifiedDiffText(): Promise<typeof unifiedDiffText>`.

- [ ] **Step 0: Operator checkpoint** (see the banner). Record the answer in the task report.

- [ ] **Step 1: Install dependencies** (in `ui/`):

```bash
npm install @codemirror/state @codemirror/view @codemirror/merge @codemirror/lang-sql @codemirror/language
```

Keep the caret ranges npm writes (repo convention). Read `node_modules/@codemirror/merge/dist/index.d.ts` and note the exact signatures of `MergeView`, `unifiedMergeView`, `Chunk.build` (or `Chunk` static builder) and the `Chunk` fields (`fromA`, `toA`, `fromB`, `toB`); the code below assumes those names — adapt if the installed version differs.

- [ ] **Step 2: Failing tests.** `lib/sqlDiff.test.ts`:

```ts
import { afterEach, expect, test } from "vitest"
import { diffTooLarge, dialectOf, normalizeForDiff, readPreference, writePreference } from "./sqlDiff"

afterEach(() => localStorage.clear())

test("limits match plan 062", () => {
  expect(diffTooLarge("a", "b")).toBe(false)
  expect(diffTooLarge("x".repeat(200 * 1024), "y".repeat(57 * 1024))).toBe(true)
  expect(diffTooLarge("a\n".repeat(2000), "b\n".repeat(2001))).toBe(true)
})

test("dialect follows the engine", () => {
  expect(dialectOf("postgres")).toBe("postgres")
  expect(dialectOf("sqlserver")).toBe("tsql")
  expect(dialectOf(null)).toBe("tsql")
})

test("line endings are normalised; ignore formatting formats both sides", () => {
  expect(normalizeForDiff("SELECT 1\r\nFROM t", "sqlserver", false)).toEqual({ text: "SELECT 1\nFROM t", formatFailed: false })
  const formatted = normalizeForDiff("select a,b from t where x=1", "sqlserver", true)
  expect(formatted.formatFailed).toBe(false)
  expect(formatted.text).toContain("\n")
})

test("a formatter failure keeps the raw text and says so", () => {
  expect(normalizeForDiff("SELECT ((( broken", "sqlserver", true)).toEqual({ text: "SELECT ((( broken", formatFailed: true })
})

test("preferences survive and fall back on unknown values", () => {
  writePreference("sqlharness.sqlDiff.mode", "unified")
  expect(readPreference("sqlharness.sqlDiff.mode", ["side-by-side", "unified"] as const, "side-by-side")).toBe("unified")
  writePreference("sqlharness.sqlDiff.mode", "weird")
  expect(readPreference("sqlharness.sqlDiff.mode", ["side-by-side", "unified"] as const, "side-by-side")).toBe("side-by-side")
})
```

If `sql-formatter` accepts `SELECT ((( broken` without throwing, pick an input from its test suite that throws (e.g. an unterminated string `SELECT 'abc`), and keep the assertion.

`components/sql-editor/codemirror.test.ts`:

```ts
import { expect, test } from "vitest"
import { unifiedDiffText } from "./codemirror"

test("unified diff text marks removed and added lines with context", () => {
  const text = unifiedDiffText("SELECT a\nFROM t\nWHERE x = 1\nORDER BY a", "SELECT a\nFROM t\nWHERE x = 2\nORDER BY a", 1)
  expect(text).toBe(["@@ -2,3 +2,3 @@", " FROM t", "-WHERE x = 1", "+WHERE x = 2", " ORDER BY a"].join("\n"))
})

test("identical text gives an empty diff", () => {
  expect(unifiedDiffText("SELECT 1", "SELECT 1")).toBe("")
})

test("additions at the end", () => {
  expect(unifiedDiffText("SELECT 1", "SELECT 1\nUNION ALL SELECT 2", 3)).toBe(["@@ -1,1 +1,2 @@", " SELECT 1", "+UNION ALL SELECT 2"].join("\n"))
})
```

- [ ] **Step 3: RED** — `npm run test -- src/lib/sqlDiff.test.ts src/components/sql-editor/codemirror.test.ts`.

- [ ] **Step 4: Implement `lib/sqlDiff.ts`.** Move `MAX_DIFF_BYTES`, `MAX_DIFF_LINES`, `PREVIEW_CHARS`, `boundedPreview`, `utf8BytesWithin`, `lineLimitExceeded` out of `SqlDiff.tsx` unchanged, and add:

```ts
import { format } from "sql-formatter"

export function diffTooLarge(baseline: string, candidate: string): boolean {
  const baselineBytes = utf8BytesWithin(baseline, MAX_DIFF_BYTES)
  return baselineBytes === null || utf8BytesWithin(candidate, MAX_DIFF_BYTES - baselineBytes) === null || lineLimitExceeded(baseline, candidate)
}

export type SqlDialect = "postgres" | "tsql"
export const dialectOf = (engine: string | null | undefined): SqlDialect => (engine === "postgres" ? "postgres" : "tsql")

/** Diff input: LF line endings; with ignoreFormatting both sides go through the same formatter. */
export function normalizeForDiff(sql: string, engine: string | null | undefined, ignoreFormatting: boolean): { text: string; formatFailed: boolean } {
  const text = sql.replace(/\r\n?/g, "\n")
  if (!ignoreFormatting) return { text, formatFailed: false }
  try {
    return { text: format(text, { language: dialectOf(engine) === "postgres" ? "postgresql" : "transactsql", keywordCase: "upper" }), formatFailed: false }
  } catch {
    return { text, formatFailed: true }
  }
}

export type DiffMode = "side-by-side" | "unified"
export type SqlTab = "baseline" | "candidate" | "diff"

export function readPreference<T extends string>(key: string, allowed: readonly T[], fallback: T): T {
  try {
    const value = localStorage.getItem(key)
    return value !== null && (allowed as readonly string[]).includes(value) ? (value as T) : fallback
  } catch {
    return fallback
  }
}

export function writePreference(key: string, value: string): void {
  try {
    localStorage.setItem(key, value)
  } catch {
    // Not remembered; the choice still applies on this page.
  }
}
```

(`keywordCase: "upper"` makes keyword case differences disappear when ignoring formatting; the existing `formatSql` in `lib/sql.ts` keeps `preserve` for display.) Make `SqlDiff.tsx` import the moved helpers instead of defining them (no behaviour change yet).

- [ ] **Step 5: Implement `components/sql-editor/codemirror.tsx`:**

```tsx
import { useEffect, useRef } from "react"
import { EditorState, Text, type Extension } from "@codemirror/state"
import { EditorView, lineNumbers } from "@codemirror/view"
import { syntaxHighlighting, defaultHighlightStyle } from "@codemirror/language"
import { sql, MSSQL, PostgreSQL } from "@codemirror/lang-sql"
import { Chunk, MergeView, unifiedMergeView } from "@codemirror/merge"
import type { DiffMode, SqlDialect } from "@/lib/sqlDiff"

function base(dialect: SqlDialect): Extension[] {
  return [
    lineNumbers(),
    sql({ dialect: dialect === "postgres" ? PostgreSQL : MSSQL }),
    syntaxHighlighting(defaultHighlightStyle, { fallback: true }),
    EditorView.lineWrapping,
    EditorState.readOnly.of(true),
    EditorView.editable.of(false),
    EditorView.theme({ "&": { fontSize: "12px" }, ".cm-scroller": { fontFamily: "var(--font-mono, ui-monospace, monospace)" } }),
  ]
}

/** Mounts an editor into a div and disposes it when inputs change. */
function useMounted(create: (parent: HTMLDivElement) => { destroy(): void }, deps: unknown[]) {
  const ref = useRef<HTMLDivElement>(null)
  useEffect(() => {
    if (!ref.current) return
    const instance = create(ref.current)
    return () => instance.destroy()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, deps)
  return ref
}

export function SqlViewer({ sql: text, dialect }: { sql: string; dialect: SqlDialect }) {
  const ref = useMounted(parent => new EditorView({ parent, state: EditorState.create({ doc: text, extensions: base(dialect) }) }), [text, dialect])
  return <div ref={ref} className="sql-editor max-h-[32rem] overflow-auto rounded border" />
}

export function SqlMergeView({ baseline, candidate, dialect, mode }: { baseline: string; candidate: string; dialect: SqlDialect; mode: DiffMode }) {
  const ref = useMounted(parent => mode === "side-by-side"
    ? new MergeView({ parent, a: { doc: baseline, extensions: base(dialect) }, b: { doc: candidate, extensions: base(dialect) }, gutter: true, collapseUnchanged: { margin: 3, minSize: 6 } })
    : new EditorView({ parent, state: EditorState.create({ doc: candidate, extensions: [...base(dialect), unifiedMergeView({ original: baseline, mergeControls: false, collapseUnchanged: { margin: 3, minSize: 6 } })] }) }),
  [baseline, candidate, dialect, mode])
  return <div ref={ref} aria-label={mode === "side-by-side" ? "Side-by-side SQL diff" : "Unified SQL diff"} className="sql-editor max-h-[40rem] overflow-auto rounded border" />
}

/** Plain-text unified diff with `context` lines around each change, built from @codemirror/merge line chunks. */
export function unifiedDiffText(baseline: string, candidate: string, context = 3): string {
  const a = Text.of(baseline.split("\n"))
  const b = Text.of(candidate.split("\n"))
  const chunks = Chunk.build(a, b)
  if (chunks.length === 0) return ""
  const aLines = baseline.split("\n")
  const bLines = candidate.split("\n")
  const lineA = (pos: number) => a.lineAt(Math.min(pos, a.length)).number - 1
  const lineB = (pos: number) => b.lineAt(Math.min(pos, b.length)).number - 1
  // Each chunk covers whole lines: [startA, endA) in A and [startB, endB) in B (0-based line indexes).
  const ranges = chunks.map(chunk => {
    const startA = lineA(chunk.fromA)
    const startB = lineB(chunk.fromB)
    const endA = chunk.toA > chunk.fromA ? lineA(chunk.toA - 1) + 1 : startA
    const endB = chunk.toB > chunk.fromB ? lineB(chunk.toB - 1) + 1 : startB
    return { startA, endA, startB, endB }
  })
  // Merge chunks whose context overlaps into hunks.
  const hunks: (typeof ranges)[] = []
  for (const range of ranges) {
    const last = hunks.at(-1)
    if (last && range.startA - last.at(-1)!.endA <= context * 2) last.push(range)
    else hunks.push([range])
  }
  const out: string[] = []
  for (const hunk of hunks) {
    const fromA = Math.max(0, hunk[0].startA - context)
    const fromB = Math.max(0, hunk[0].startB - context)
    const toA = Math.min(aLines.length, hunk.at(-1)!.endA + context)
    const toB = Math.min(bLines.length, hunk.at(-1)!.endB + context)
    out.push(`@@ -${fromA + 1},${toA - fromA} +${fromB + 1},${toB - fromB} @@`)
    let cursorA = fromA
    for (const range of hunk) {
      for (; cursorA < range.startA; cursorA++) out.push(` ${aLines[cursorA]}`)
      for (let i = range.startA; i < range.endA; i++) out.push(`-${aLines[i]}`)
      for (let i = range.startB; i < range.endB; i++) out.push(`+${bLines[i]}`)
      cursorA = range.endA
    }
    for (; cursorA < toA; cursorA++) out.push(` ${aLines[cursorA]}`)
  }
  return out.join("\n")
}
```

The chunk → line arithmetic is the risky part: run the three tests and add cases (change in first line, deletion of the last line, two distant changes → two hunks) until `unifiedDiffText` is right. If `Chunk.build`'s positions do not end on line boundaries in the installed version, compute line indexes with `lineAt` on both ends and widen to whole lines. Line-aligned chunks can include lines that are equal on both sides (e.g. an append after the last line reports the last line as changed); trim equal leading/trailing lines inside each range before emitting `-`/`+`, so the third test's expected output holds.

- [ ] **Step 6: Implement `components/SqlEditors.tsx`:**

```tsx
import { lazy, Suspense, type ComponentProps } from "react"
import { Skeleton } from "@/components/ui/skeleton"

const load = () => import("@/components/sql-editor/codemirror")
const Viewer = lazy(() => load().then(module => ({ default: module.SqlViewer })))
const Merge = lazy(() => load().then(module => ({ default: module.SqlMergeView })))

type ViewerProps = ComponentProps<typeof import("@/components/sql-editor/codemirror").SqlViewer>
type MergeProps = ComponentProps<typeof import("@/components/sql-editor/codemirror").SqlMergeView>

export function LazySqlViewer(props: ViewerProps) {
  return <Suspense fallback={<Skeleton className="h-32 w-full" />}><Viewer {...props} /></Suspense>
}

export function LazySqlMergeView(props: MergeProps) {
  return <Suspense fallback={<Skeleton className="h-48 w-full" />}><Merge {...props} /></Suspense>
}

export const loadUnifiedDiffText = () => load().then(module => module.unifiedDiffText)
```

Add to `index.css` minimal styles so the editor follows the theme: `.sql-editor .cm-editor { background: var(--background); color: var(--foreground); }` and a dark-mode rule under `.dark .sql-editor` for `.cm-gutters` and the merge `.cm-changedLine`/`.cm-deletedChunk` backgrounds using existing tokens (check `index.css` token names).

- [ ] **Step 7: GREEN** — Step 3 command, then `npm run check` and `npm run build`. Confirm the build output lists a separate chunk containing CodeMirror (file name with `codemirror` or a hashed chunk well over 100 kB that is not `index-*.js`) and that `index-*.js` size did not grow by more than a few kB versus before the task (record both numbers).

- [ ] **Step 8: Commit**

```bash
git add src/SqlHarness.Dashboard/ui/package.json src/SqlHarness.Dashboard/ui/package-lock.json src/SqlHarness.Dashboard/ui/src
git commit -m "feat(dashboard-ui): lazy CodeMirror SQL viewer, merge view and unified diff text"
```

---

### Task 2: SQL tabs with side-by-side/unified diff and copy icons

**Files:**
- Create: `ui/src/components/CopyButton.tsx`, `ui/src/components/CopyButton.test.tsx`
- Modify: `ui/src/components/SqlDiff.tsx`, `ui/src/components/SqlDiff.test.tsx`, `ui/src/components/CompareDetail.tsx` (pass `engine`)

**Interfaces:**
- Consumes: Task 1.
- Produces:
  - `CopyButton({ label, getText }: { label: string; getText: () => string | Promise<string> })` — icon button (`lucide-react` `Copy`, then `Check` for 2 s), `aria-label={label}`, tooltip text `label`; on clipboard failure opens a `Dialog` titled "Copy manually" with a read-only `textarea` (`aria-label="Text to copy"`) whose content is selected on open.
  - `SqlDiff({ baseline, candidate, engine }: { baseline: string | null; candidate: string | null; engine: string | null })`.

- [ ] **Step 1: Failing tests.** In tests mock the lazy editor module so jsdom does not need layout:

```tsx
vi.mock("@/components/SqlEditors", () => ({
  LazySqlViewer: ({ sql }: { sql: string }) => <pre aria-label="SQL viewer">{sql}</pre>,
  LazySqlMergeView: ({ baseline, candidate, mode }: { baseline: string; candidate: string; mode: string }) =>
    <pre aria-label={`merge ${mode}`}>{baseline}|{candidate}</pre>,
  loadUnifiedDiffText: async () => (await import("@/components/sql-editor/codemirror")).unifiedDiffText,
}))
```

`CopyButton.test.tsx`:

```tsx
test("copies text and confirms", async () => {
  const writeText = vi.fn().mockResolvedValue(undefined)
  Object.assign(navigator, { clipboard: { writeText } })
  render(<CopyButton label="Copy baseline SQL" getText={() => "SELECT 1"} />)
  await userEvent.click(screen.getByRole("button", { name: "Copy baseline SQL" }))
  expect(writeText).toHaveBeenCalledWith("SELECT 1")
  expect(await screen.findByRole("status")).toHaveTextContent("Copied")
})

test("falls back to a manual copy dialog", async () => {
  Object.assign(navigator, { clipboard: { writeText: vi.fn().mockRejectedValue(new Error("denied")) } })
  render(<CopyButton label="Copy" getText={async () => "# Compare"} />)
  await userEvent.click(screen.getByRole("button", { name: "Copy" }))
  expect(await screen.findByRole("dialog", { name: "Copy manually" })).toBeInTheDocument()
  expect(screen.getByRole("textbox", { name: "Text to copy" })).toHaveValue("# Compare")
})
```

Replace `SqlDiff.test.tsx` cases with:

```tsx
test("tabs show each side and the diff; mode and ignore formatting are remembered", async () => {
  render(<SqlDiff baseline={"select a from t"} candidate={"select a from t where x = 1"} engine="sqlserver" />)
  expect(screen.getByRole("tab", { name: "Diff" })).toHaveAttribute("aria-selected", "true")
  expect(await screen.findByLabelText("merge side-by-side")).toBeInTheDocument()
  await userEvent.click(screen.getByRole("radio", { name: "Unified" }))
  expect(await screen.findByLabelText("merge unified")).toBeInTheDocument()
  expect(localStorage.getItem("sqlharness.sqlDiff.mode")).toBe("unified")
  await userEvent.click(screen.getByRole("checkbox", { name: "Ignore formatting" }))
  expect((await screen.findByLabelText("merge unified")).textContent).toContain("\n")
  await userEvent.click(screen.getByRole("tab", { name: "Baseline" }))
  expect(screen.getByLabelText("SQL viewer")).toHaveTextContent("select a from t")
})

test("without stored text there is no diff tab", () => {
  render(<SqlDiff baseline={null} candidate={"SELECT 1"} engine={null} />)
  expect(screen.getByText(/journal\.storeSensitive is off/)).toBeInTheDocument()
  expect(screen.queryByRole("tab", { name: "Diff" })).not.toBeInTheDocument()
})

test("over the limit the diff is replaced by a message but sides stay viewable", async () => {
  render(<SqlDiff baseline={"a\n".repeat(2500)} candidate={"b\n".repeat(2500)} engine={null} />)
  expect(screen.getByText(/exceeds the 256 KiB or 4,000 line limit/)).toBeInTheDocument()
  await userEvent.click(screen.getByRole("tab", { name: "Candidate" }))
  expect(screen.getByLabelText("SQL viewer")).toBeInTheDocument()
})

test("copying the diff gives a unified text diff", async () => {
  const writeText = vi.fn().mockResolvedValue(undefined)
  Object.assign(navigator, { clipboard: { writeText } })
  render(<SqlDiff baseline={"SELECT 1"} candidate={"SELECT 2"} engine={null} />)
  await userEvent.click(screen.getByRole("button", { name: "Copy diff" }))
  await waitFor(() => expect(writeText).toHaveBeenCalledWith("@@ -1,1 +1,1 @@\n-SELECT 1\n+SELECT 2"))
})
```

Clear `localStorage` in `afterEach`. Keep the 062 test for the "Show full SQL versions" bounded preview only if the behaviour is kept (it is replaced by the Baseline/Candidate tabs — remove that test and the toggle).

- [ ] **Step 2: RED** — `npm run test -- src/components/SqlDiff.test.tsx src/components/CopyButton.test.tsx`.

- [ ] **Step 3: Implement `CopyButton.tsx`** (use `Button` `variant="ghost" size="icon"`, `Dialog` from `components/ui/dialog`, a `role="status"` visually-hidden span that says "Copied" for 2 s; select the textarea content in an effect when the dialog opens).

- [ ] **Step 4: Implement `SqlDiff.tsx`:**
  - Without both texts: keep 062's message; render Baseline/Candidate tabs only for the side that exists (or just the message when both are null).
  - Tabs (`components/ui/tabs`): `baseline`, `candidate`, `diff`; initial tab `readPreference("sqlharness.sqlDiff.tab", ["baseline","candidate","diff"], "diff")`, written on change.
  - Diff tab content: a radio group (`role="radiogroup"` with two `role="radio"` buttons or the existing toggle pattern) "Side by side" / "Unified" (preference `sqlharness.sqlDiff.mode`), a checkbox "Ignore formatting" (preference `sqlharness.sqlDiff.ignoreFormatting`, values `"true"`/`"false"`), then `LazySqlMergeView` with `normalizeForDiff` of both sides; if either side's `formatFailed`, show "Formatting failed; showing the SQL as written." Over the limit (`diffTooLarge` on the raw texts): 062's message and 062's two bounded previews.
  - Baseline/Candidate tab content: `LazySqlViewer` when that side is within `MAX_DIFF_BYTES`, else `boundedPreview` in a `<pre>`.
  - Copy icons: in each side tab `CopyButton label="Copy baseline SQL"|"Copy candidate SQL"` (raw text); in the Diff tab `CopyButton label="Copy diff"` → `loadUnifiedDiffText().then(f => f(a.text, b.text))` using the currently normalised texts.
  - Pass `engine={detail.operation.engine}` from `CompareDetail`.

- [ ] **Step 5: GREEN** — Step 2 command plus `src/components/CompareDetail.test.tsx src/pages/OperationPage.test.tsx` (mock `SqlEditors` there too if they render the SQL tab).

- [ ] **Step 6: Commit** — `git commit -m "feat(dashboard-ui): SQL tabs with side-by-side and unified diff, copy icons"`.

---

### Task 3: Table IO: baseline vs candidate

**Files:**
- Create: `ui/src/lib/tableIoDelta.ts`, `ui/src/lib/tableIoDelta.test.ts`, `ui/src/components/TableIoComparison.tsx`, `ui/src/components/TableIoComparison.test.tsx`
- Modify: `ui/src/components/CompareDetail.tsx` (Plans and IO tab)

**Interfaces:**
- Consumes: `ComparePair` from `lib/compare.ts`; `TableIoRow`, `PostgresBuffers`.
- Produces:
  - `type TableIoDeltaRow = { table: string; presence: "both" | "baseline-only" | "candidate-only"; baseline: TableIoRow | null; candidate: TableIoRow | null; logicalDelta: number | null }` where `logicalDelta` is the relative change `(c - b) / b` or `null` when `b` is `0`/missing (same convention as `compareRows`' `delta` — read `lib/compare.ts` and reuse its helper if one exists).
  - `tableIoDelta(baseline: TableIoRow[], candidate: TableIoRow[]): { rows: TableIoDeltaRow[]; total: { baseline: number; candidate: number; delta: number | null } }`, rows sorted by absolute logical-read change (`|c - b|`, missing side = 0) descending, then table name.
  - `TableIoComparison({ pairs, engine }: { pairs: ComparePair[]; engine: string | null })`.

- [ ] **Step 1: Failing tests** (`tableIoDelta.test.ts`):

```ts
import { expect, test } from "vitest"
import type { TableIoRow } from "@/api/types"
import { tableIoDelta } from "./tableIoDelta"

const io = (table: string, logicalReads: number, scanCount: number | null = 1): TableIoRow => ({
  table, logicalReads, scanCount, physicalReads: 0, pageServerReads: null, readAheadReads: 0,
  lobLogicalReads: 0, lobPhysicalReads: 0, lobReadAheadReads: 0, coldRuns: 0,
})

test("joins tables, labels one-sided rows, sorts by absolute change and totals", () => {
  const result = tableIoDelta([io("Orders", 1000), io("Customers", 10), io("Temp", 50)], [io("Orders", 100), io("Customers", 12), io("Lines", 30)])
  expect(result.rows.map(r => [r.table, r.presence])).toEqual([
    ["Orders", "both"], ["Temp", "baseline-only"], ["Lines", "candidate-only"], ["Customers", "both"],
  ])
  expect(result.rows[0].logicalDelta).toBeCloseTo(-0.9)
  expect(result.rows[1].logicalDelta).toBeNull()
  expect(result.total).toEqual({ baseline: 1060, candidate: 142, delta: (142 - 1060) / 1060 })
})

test("zero baseline gives no percentage", () => {
  expect(tableIoDelta([io("T", 0)], [io("T", 5)]).rows[0].logicalDelta).toBeNull()
})
```

`TableIoComparison.test.tsx`: renders one table per pair labelled with `pair.label` when `pairs.length > 1` (matrix): a cell where `Orders` rises from 100 to 400 shows `+300%` on that cell's `Orders` row even though another cell improves; a PostgreSQL pair shows a "Buffers" table (shared hit/read, temp read/written B | C | Change) and no per-table section; empty `tableIo` on both sides shows "No table IO recorded for this pair."

- [ ] **Step 2: RED** — `npm run test -- src/lib/tableIoDelta.test.ts src/components/TableIoComparison.test.tsx`.

- [ ] **Step 3: Implement** `tableIoDelta` (Map by table name, ordinal comparison) and `TableIoComparison`: card "Table IO: baseline vs candidate"; per pair a table with columns Table | Logical reads B | C | Change | Scans B | C; a `<details>` "More" row section with physical, read-ahead and LOB reads B | C; one-sided rows show a `Badge` "only in baseline"/"only in candidate"; a bold Total row; `formatNumber`/`formatChange` from existing libs. PostgreSQL (`engine === "postgres"`): rows from `PostgresBuffers` fields with the same delta rule.

- [ ] **Step 4: Wire** in `CompareDetail` Plans and IO tab: render `<TableIoComparison pairs={pairs} engine={detail.operation.engine} />` above the existing per-side `VariantPanel` grid.

- [ ] **Step 5: GREEN** — Step 2 command plus `src/components/CompareDetail.test.tsx`.

- [ ] **Step 6: Commit** — `git commit -m "feat(dashboard-ui): per-table IO comparison for compare pairs"`.

---

### Task 4: Plan changes

**Files:**
- Create: `ui/src/lib/planChanges.ts`, `ui/src/lib/planChanges.test.ts`, `ui/src/components/PlanChanges.tsx`, `ui/src/components/PlanChanges.test.tsx`
- Modify: `ui/src/components/CompareDetail.tsx`

**Interfaces:**
- Consumes: `DistilledPlan`, `PlanNode`, `PlanLinkRow`, `useDistilledPlan(hash, enabled)`, `flattenPlan` (`lib/plan.ts`).
- Produces:
  - `firstPlanHash(variant: VariantDetail): string | null` — the stored link with the lowest `(repetition, ordinal)`; null when none is stored.
  - `type AccessChange = { object: string; baseline: string[]; candidate: string[] }` — physical operators touching the object (`objectName` + `.indexName` when present), deduplicated, in plan order.
  - `type WarningChange = { warning: string; baseline: number; candidate: number }` — counts of each warning text (`PlanNode.warnings`) plus `"Missing index"` from `missingIndexes` length.
  - `type TopOperator = { physicalOp: string; object: string | null; costFraction: number }`.
  - `planChanges(baseline: DistilledPlan, candidate: DistilledPlan): { access: AccessChange[]; warnings: WarningChange[]; topBaseline: TopOperator[]; topCandidate: TopOperator[] }` — `access` lists only objects whose operator lists differ; `warnings` only where counts differ; top 5 by `costFraction`.
  - `PlanChanges({ pair }: { pair: ComparePair })`.

- [ ] **Step 1: Failing tests** (`planChanges.test.ts`):

```ts
import { expect, test } from "vitest"
import type { DistilledPlan, PlanNode } from "@/api/types"
import { planChanges } from "./planChanges"

const plan = (root: PlanNode, missing = 0): DistilledPlan => ({
  statements: [{ root, missingIndexes: Array.from({ length: missing }, () => ({ table: "Orders", equalityColumns: ["CustomerId"], inequalityColumns: [], includeColumns: [], impact: 90 })) }],
})

test("scan to seek, resolved missing index and implicit conversion", () => {
  const before = plan({ physicalOp: "Hash Match", costFraction: 0.2, children: [
    { physicalOp: "Clustered Index Scan", objectName: "dbo.Orders", indexName: "PK_Orders", costFraction: 0.7, warnings: ["Implicit conversion"] },
    { physicalOp: "Index Seek", objectName: "dbo.Customers", indexName: "IX_C", costFraction: 0.1 },
  ] }, 1)
  const after = plan({ physicalOp: "Nested Loops", costFraction: 0.1, children: [
    { physicalOp: "Index Seek", objectName: "dbo.Orders", indexName: "IX_Orders_Customer", costFraction: 0.5 },
    { physicalOp: "Index Seek", objectName: "dbo.Customers", indexName: "IX_C", costFraction: 0.4 },
  ] })

  const changes = planChanges(before, after)

  expect(changes.access).toEqual([
    { object: "dbo.Orders.PK_Orders", baseline: ["Clustered Index Scan"], candidate: [] },
    { object: "dbo.Orders.IX_Orders_Customer", baseline: [], candidate: ["Index Seek"] },
  ])
  expect(changes.warnings).toEqual([
    { warning: "Implicit conversion", baseline: 1, candidate: 0 },
    { warning: "Missing index", baseline: 1, candidate: 0 },
  ])
  expect(changes.topBaseline[0]).toEqual({ physicalOp: "Clustered Index Scan", object: "dbo.Orders.PK_Orders", costFraction: 0.7 })
})

test("identical plans have no access or warning changes", () => {
  const p = plan({ physicalOp: "Index Seek", objectName: "T", costFraction: 1 })
  expect(planChanges(p, p)).toMatchObject({ access: [], warnings: [] })
})
```

Sort `access` by object name (ordinal) and `warnings` by warning name so the expected order is deterministic; adjust the expected arrays if you choose another documented order.

`PlanChanges.test.tsx` (stub `fetch` for `/api/plans/<hash>?view=distilled` with `stubFetch` from `test/render` — or render inside a `QueryClientProvider` with `getJson` stubbed): equal hashes → "Same plan" and exactly **one** plan request; a side without a stored plan → "Plan not stored for the candidate."; a fetch error → the `ErrorState` message; success → a table "Access changes" with `Clustered Index Scan → —` style rows and a "Warnings" list (`Missing index: resolved` when baseline > 0 and candidate = 0, `introduced` for the reverse).

- [ ] **Step 2: RED** — `npm run test -- src/lib/planChanges.test.ts src/components/PlanChanges.test.tsx`.

- [ ] **Step 3: Implement** `planChanges` over all statements of each plan (walk with `flattenPlan`), and `PlanChanges`: header "Plan changes" per pair; hashes via `firstPlanHash`; `useDistilledPlan(hashB, hashB !== null)`, `useDistilledPlan(hashC, hashC !== null && hashC !== hashB)`; states: same plan / not stored / loading skeleton / error / changes (access table "Object | Baseline | Candidate", warnings list with resolved/introduced/changed wording, two small top-5 lists side by side).

- [ ] **Step 4: Wire** below `TableIoComparison` in the Plans and IO tab, one `PlanChanges` per pair.

- [ ] **Step 5: GREEN** — Step 2 command plus `src/components/CompareDetail.test.tsx`.

- [ ] **Step 6: Commit** — `git commit -m "feat(dashboard-ui): plan changes between compare sides"`.

---

### Task 5: Copy for PR (Markdown)

**Files:**
- Create: `ui/src/lib/prMarkdown.ts`, `ui/src/lib/prMarkdown.test.ts`
- Modify: `ui/src/components/CompareDetail.tsx` (+ its test)

**Interfaces:**
- Consumes: `compareRows`, `regressionLabel`, `equivalenceLabel`, `formatChange`, `comparePairs` (`lib/compare.ts`); `tableIoDelta` (T3); `planChanges` (T4); `diffTooLarge`, `normalizeForDiff`; `loadUnifiedDiffText` (T1); `CopyButton` (T2).
- Produces: `buildPrMarkdown(input: PrMarkdownInput): string` with

```ts
export type PrMarkdownInput = {
  subject: string | null
  scope: string | null
  baselineVersion: string | null
  candidateVersion: string | null
  engine: string | null
  status: string
  regression: Record<string, unknown> | null
  equivalence: Record<string, unknown> | null
  isMatrix: boolean
  cases: { label: string; pair: ComparePair | null; regression: Record<string, unknown> | null; equivalence: Record<string, unknown> | null }[]
  plans: Map<string, ReturnType<typeof planChanges> | "same" | "unavailable">   // key: pair label
  sqlDiff: string | null                                                       // unified diff text, already bounded
}
```

- [ ] **Step 1: Failing tests** (`prMarkdown.test.ts`): build an input with one pair (Orders 1000 → 100 reads, elapsed 120 → 30 ms), regression `{ verdict: "pass" }`, equivalence equivalent, plan changes with a scan → seek, `sqlDiff: "@@ -1,1 +1,1 @@\n-SELECT 1\n+SELECT 2"`. Assert the Markdown:
  - starts with `### SQL compare: orders-report / whole (v0 → v3)`;
  - contains a line with `Engine: SQL Server` (map `sqlserver`/`postgres` to display names), the execution status, the regression and equivalence labels (from `regressionLabel`/`equivalenceLabel`), and `Runs: <n>`;
  - contains a Markdown table header `| Metric | Baseline | Candidate | Change |` and a `| Logical reads |` row;
  - contains `| Table | Logical reads (baseline) | Logical reads (candidate) | Change |` with `| Orders | 1,000 | 100 | -90% |` (use `formatNumber`/`formatChange` outputs exactly);
  - contains `**Plan changes**` and `dbo.Orders.PK_Orders: Clustered Index Scan → —`;
  - ends with a fenced block starting ```` ```diff ```` containing the diff;
  - **does not** contain any of: the server name, database name, profile, a var value, a parameter value, setup SQL text, the artifact directory (put distinctive strings into the `OperationDetail` the test builds the input from and assert absence on the final string).
  Matrix input with two cells, one regressing on `Lines`: the Markdown has `#### Cell 0` and `#### Cell 1` sections and the regressing cell's `Lines` row shows the increase. `sqlDiff: null` → no diff block and the line `SQL diff not included (SQL text not stored or over the size limit).`

`CompareDetail.test.tsx`: the header has a button named `Copy` (tooltip "Copy"); clicking it with stubbed `navigator.clipboard.writeText` writes Markdown starting with `### SQL compare:`; with `sqlText: null` it still copies (without the diff block).

- [ ] **Step 2: RED** — `npm run test -- src/lib/prMarkdown.test.ts src/components/CompareDetail.test.tsx`.

- [ ] **Step 3: Implement `buildPrMarkdown`** as a pure function (no React), escaping `|` in table cells (`\|`) and backticks in inline code. Build `PrMarkdownInput` in `CompareDetail` from `detail` exactly the way `Cases` builds its cards (reuse that mapping by extracting it to a small function `compareCards(detail)` in `CompareDetail.tsx` and using it in both places).

- [ ] **Step 4: Copy action.** In the `CompareDetail` header add `<CopyButton label="Copy" getText={copyMarkdown} />` where `copyMarkdown` (async):
  1. For each pair, compute plan changes: if `firstPlanHash` equal → `"same"`; if either null → `"unavailable"`; else `queryClient.fetchQuery({ queryKey: queryKeys.plan(hash), queryFn: ..., staleTime: Infinity })` for both (reuse `useDistilledPlan`'s query function by exporting it from `queries.ts` as `fetchDistilledPlan(hash)`), catching errors → `"unavailable"`.
  2. SQL diff: when both texts exist and `!diffTooLarge(...)`, `normalizeForDiff(..., false)` both and `await loadUnifiedDiffText()`; else `null`.
  3. Return `buildPrMarkdown(...)`.

- [ ] **Step 5: GREEN** — Step 2 command, then `npm run test`.

- [ ] **Step 6: Commit** — `git commit -m "feat(dashboard-ui): copy compare evidence as Markdown for a PR"`.

---

### Task 6: Session version diff

**Files:**
- Modify: `src/SqlHarness.Dashboard/DashboardModels.cs`, `src/SqlHarness.Dashboard/JournalReader.cs`, `src/SqlHarness.Dashboard/DashboardServer.cs`
- Create: `ui/src/components/SessionVersionDiff.tsx`, `ui/src/components/SessionVersionDiff.test.tsx`
- Modify: `ui/src/components/CompareSessionOverview.tsx`, `ui/src/api/{types,queries}.ts`
- Test: `tests/SqlHarness.Tests/Dashboard/JournalReaderTests.cs`, `tests/SqlHarness.Tests/Dashboard/DashboardServerTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record SqlVersionText(string Hash, string? Text, bool TooLarge);`
  - `JournalReader.SessionSqlText(long sessionId, string hash)` → `SqlVersionText?` — null when no operation of that session has the hash as `sql_hash` or `candidate_sql_hash`; `Text` null when every such operation lacks the text (hash-only); `TooLarge` true (and `Text` null) when the text exceeds `JournalReader.MaximumSqlVersionBytes = 256 * 1024` UTF-8 bytes.
  - `GET /api/sessions/{id}/sql?hash=sha256:<64 lowercase hex>` → 200 `SqlVersionText`; 400 for a malformed hash; 404 when not found.
  - TS: `SqlVersionText` type; `useSessionSqlText(sessionId: number, hash: string | null)`.
  - `versionOptions(attempts: CompareAttemptSummary[], subject: string, scope: string): { version: string; hash: string }[]` (exported from `SessionVersionDiff.tsx` or `lib/compare.ts`) — distinct `(version, hash)` from baseline and candidate sides, sorted by the numeric part of `vN`.
  - `SessionVersionDiff({ sessionId, engine, options }: { sessionId: number; engine: string | null; options: { version: string; hash: string }[] })`.

- [ ] **Step 1: Failing C# tests.** Reader: seed (with `storeSensitive: true`) two compare operations in one session whose baseline/candidate hashes and texts differ (use the 062 seed helpers for compare operations if present; otherwise seed `operations` rows with raw SQL setting `sql_hash`, `sql_text`, `candidate_sql_hash`, `candidate_sql_text`), then assert: baseline hash → baseline text; candidate hash → candidate text; a hash from another session → null; hash-only seed → `Text == null, TooLarge == false`; a 300 KiB text → `TooLarge == true, Text == null`. Server: malformed hash (`abc`, `sha256:XYZ`) → 400; unknown → 404.

- [ ] **Step 2: RED** — `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~SessionSqlText|FullyQualifiedName~Session_sql"`.

- [ ] **Step 3: Implement.**

```csharp
    internal const int MaximumSqlVersionBytes = 256 * 1024;

    public SqlVersionText? SessionSqlText(long sessionId, string hash)
    {
        using var connection = OpenSnapshot();
        if (connection is null)
            return null;
        var rows = Query(connection, """
            SELECT CASE WHEN sql_hash = $hash THEN sql_text ELSE candidate_sql_text END
            FROM operations
            WHERE session_id = $session AND (sql_hash = $hash OR candidate_sql_hash = $hash)
            ORDER BY (CASE WHEN sql_hash = $hash THEN sql_text ELSE candidate_sql_text END) IS NULL, id DESC
            LIMIT 1;
            """, [("$session", sessionId), ("$hash", hash)], r => NullableString(r, 0));
        if (rows.Count == 0)
            return null;
        var text = rows[0];
        if (text is not null && System.Text.Encoding.UTF8.GetByteCount(text) > MaximumSqlVersionBytes)
            return new SqlVersionText(hash, null, true);
        return new SqlVersionText(hash, text, false);
    }
```

Route (next to `/sessions/{id:long}/compares`):

```csharp
        MapRead(api, "/sessions/{id:long}/sql", (long id, string? hash) =>
            hash is null || !SqlHashPattern().IsMatch(hash)
                ? BadRequest("hash must be sha256:<64 lowercase hex>.")
                : reader.SessionSqlText(id, hash) is { } text ? Results.Json(text, Json) : Results.NotFound());
```

with `[GeneratedRegex("^sha256:[0-9a-f]{64}$")] private static partial Regex SqlHashPattern();` (`DashboardServer` is already `partial`). Add the record to `DashboardModels.cs` and a names test to `DashboardContractTests` (`hash`, `text`, `tooLarge`).

- [ ] **Step 4: GREEN (C#)** — Step 2 filter plus `FullyQualifiedName~Dashboard`.

- [ ] **Step 5: Failing UI tests** (`SessionVersionDiff.test.tsx`, mock `SqlEditors` as in Task 2): with options `v0, v1, v3` the selects default to `v0` and `v3`, both texts are fetched from `/api/sessions/5/sql?hash=…`, and the merge view shows both; changing the right select to `v1` refetches only that hash; a `{ text: null, tooLarge: false }` response shows "SQL text for v0 is not stored (journal.storeSensitive was off)."; `tooLarge: true` shows "v3 is larger than 256 KiB; open a compare to see a bounded preview."; a 404 shows "v0 is no longer in the retained history." In `CompareSessionOverview.test.tsx`: each scope group with ≥ 2 versions has a "Diff versions" button that reveals `SessionVersionDiff`; a group with one version has no button.

- [ ] **Step 6: Implement UI.** `useSessionSqlText` (`useQuery`, key `["session-sql", sessionId, hash]`, `enabled: hash !== null`, `staleTime: Infinity`, 404 → `NotFoundError` handled by the component). `SessionVersionDiff`: two labelled `<select>`s ("From version", "To version"), then `LazySqlMergeView` (mode from the same `sqlharness.sqlDiff.mode` preference, with the Side by side / Unified radios and Ignore formatting from Task 2 — extract those controls into a small `DiffControls` component in `SqlDiff.tsx` and reuse it), `diffTooLarge` guard, and `CopyButton label="Copy diff"`. Under it the muted note "SQL only. Metrics live on each compare; different compares are not compared with each other." In `CompareSessionOverview`, inside each scope `<details>`, add the "Diff versions" toggle button and render `SessionVersionDiff` with `versionOptions(attempts, subject, scope)` and the session engine (pass `engine` down from `SessionPage`: use the most recent compare operation's `engine` from `SessionDetail.operations`, or `null`).

- [ ] **Step 7: GREEN** — `npm run test -- src/components/SessionVersionDiff.test.tsx src/components/CompareSessionOverview.test.tsx src/pages/SessionPage.test.tsx`, then `npm run check`.

- [ ] **Step 8: Commit** — `git commit -m "feat(dashboard): diff SQL versions within a session"`.

---

### Task 7: Fixture check, documentation, gates

**Files:**
- Modify: `README.md`, `plans/README.md`, `docs/superpowers/specs/2026-10-14-dashboard-compare-review-design.md` (status line)

- [ ] **Step 1: Manual fixture check.** Build (`npm run build`) and run the dashboard against a **temporary** SQLHarness home seeded by a throwaway script or test helper (not the operator's home, no real data): one plain compare, one matrix compare with a regressing cell, one hash-only compare, one compare with a 300 KiB SQL. In a browser at desktop width and at 390 px: tab through SQL tabs, diff mode radios, Ignore formatting, Copy (check the clipboard text), Plans and IO cards, session "Diff versions"; switch light/dark via the theme menu. Open the browser devtools console: no CSP violations. Record what was checked in the task report; do not commit screenshots or the fixture home.
- [ ] **Step 2: README.** Dashboard section: compare review (SQL tabs, side-by-side/unified diff, Ignore formatting, copy icons), Table IO and Plan changes cards, **Copy** Markdown contents and its exclusions (server, database, profile, vars, parameters, setup, artifact paths), session version diff (SQL only), new endpoint `GET /api/sessions/{id}/sql?hash=` in the dashboard API list if README lists endpoints. Not agent-facing: `Capabilities.cs`, `skills/sqlharness/SKILL.md` unchanged.
- [ ] **Step 3: Gates.** `npm run check` and `npm run build` in `ui/`; then `pwsh ./scripts/verify.ps1`; after it finishes `pwsh ./scripts/verify-linux.ps1`. Record exit codes and pass lines; search logs case-sensitively for `FAILED`.
- [ ] **Step 4: Plan row and spec status** — 071 DONE with commit range, gate results, the CodeMirror decision and the bundle sizes from Task 1 Step 7; spec `Status:` "implemented by Plan 071".
- [ ] **Step 5: Commit** — `git commit -m "docs: dashboard compare review (plan 071)"`.

Integration into `main` and any push only on a separate operator instruction.
