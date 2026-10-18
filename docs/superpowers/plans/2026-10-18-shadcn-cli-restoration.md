# shadcn CLI Restoration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Return the `shadcn` CLI to the dashboard UI as a `devDependency`, replace the vendored copy of its stylesheet with a real resolvable import, and delete the manual repair instructions.

**Architecture:** `shadcn@4.22.0` is added to `devDependencies` so `npm audit --omit=dev` excludes its unreachable `braces` advisory from the production dependency surface. `src/index.css` imports `shadcn/tailwind.css` from `node_modules` instead of the vendored copy, which supplies the nine Tailwind custom variants, two accordion keyframes and 24 utilities that six existing generated components depend on. No application code changes.

**Tech Stack:** npm, Tailwind CSS v4, Vite, shadcn/ui on Base UI primitives.

**Spec:** `docs/superpowers/specs/2026-10-18-shadcn-cli-and-primitive-adoption-design.md` — read Part 1 and "The stylesheet problem, stated precisely" before starting; the plan argues from the spec, so the spec travels with it.

## Global Constraints

- Base UI primitives only. Any component importing from `@radix-ui/*` is wrong for this repository.
- Generated files in `src/components/ui/` are not hand-edited.
- No restyling. No theme changes. The `neutral` base color stays. The 2026-10-06 design's "no custom CSS and no theme changes" rule holds.
- `shadcn` is pinned at `4.22.0`, kept as the caret range npm writes.
- Audit criterion is `npm --prefix src/SqlHarness.Dashboard/ui audit --omit=dev` reporting **no findings other than** the two pre-existing `dompurify` advisories reached through `monaco-editor`. That command exits non-zero on those two, so a clean exit is neither expected nor required.

## Review Focus

The spec implies five failure modes that no automated check in this repository would catch. Each is pinned to a test in the task that owns the code.

1. **A switch's checked and disabled states render.** `src/components/ui/switch.tsx` uses `data-checked:`, `data-unchecked:` and `data-disabled:` — 11 usages across 17 lines. If the variants do not resolve, the switch loses its on/off appearance entirely. Pin: Task 2, Step 5.
2. **A dialog animates open and close.** `dialog.tsx` uses `data-open:animate-in` and `data-closed:animate-out`, 10 usages. If the variants do not resolve, dialogs still open but appear instantly with no transition. Pin: Task 2, Step 5.
3. **An active tab is visually distinct.** `tabs.tsx` uses `data-active:`, `data-horizontal:` and `data-vertical:`, 12 usages. Without the variants, no tab indicates selection. Pin: Task 2, Step 5.
4. **A separator orients correctly.** `separator.tsx` uses `data-horizontal:` and `data-vertical:`, 4 usages. Without them, a vertical separator renders horizontal. Pin: Task 2, Step 5.
5. **A dropdown menu and a tooltip animate.** `dropdown-menu.tsx` has 21 variant usages and `tooltip.tsx` has 6; both are the highest-density consumers and would fail most visibly. Pin: Task 2, Step 5.

---

### Task 1: Guard the current stylesheet

**Files:**
- Test: `src/SqlHarness.Dashboard/ui/scripts/check.mjs` (read only, to learn the gate's shape)
- Create: `src/SqlHarness.Dashboard/ui/scripts/check-tailwind-variants.mjs`

**Interfaces:**
- Consumes: nothing from other tasks.
- Produces: a check script `node scripts/check-tailwind-variants.mjs` that exits 0 when the nine required Tailwind custom variants and both accordion keyframes are resolvable, and exits 1 with a listing of what is missing. Task 2 runs this script as its regression guard.

- [ ] **Step 1: Read `scripts/check.mjs` and note the existing conventions for how this project structures a check script** — how it reports, what it prints on failure, and whether it is wired into the `check` script in `package.json` or invoked separately.

- [ ] **Step 2: Record the current build output size and hash as the baseline**

Run from `src/SqlHarness.Dashboard/ui`:

```bash
npm run build
```

Then find the emitted stylesheet:

```bash
ls -l dist/assets/index-*.css
```

Expected: one file, and **71,761 bytes**. Record the exact byte count in the commit message for Task 2. If the current size is not 71,761, stop and report the difference — the spec's proof was taken on this repository's current state and a mismatch means something changed underneath it.

- [ ] **Step 3: Write `scripts/check-tailwind-variants.mjs`**

It must read `src/index.css`, resolve the `@import` it finds there, and check the resolved file for these exact strings:

- `@custom-variant data-open`
- `@custom-variant data-closed`
- `@custom-variant data-checked`
- `@custom-variant data-unchecked`
- `@custom-variant data-selected`
- `@custom-variant data-disabled`
- `@custom-variant data-active`
- `@custom-variant data-horizontal`
- `@custom-variant data-vertical`
- `@keyframes accordion-down`
- `@keyframes accordion-up`

On success print a one-line summary naming how the variants were resolved (`vendored` when the import points at `./styles/shadcn-tailwind.css`, `package` when it points at `shadcn/tailwind.css`) and exit 0. On failure print each missing string on its own line and exit 1.

Resolve a bare `shadcn/tailwind.css` specifier by reading `node_modules/shadcn/package.json` and following its `./tailwind.css` export field rather than assuming a path — the export exists but the file lives at `dist/tailwind.css`.

- [ ] **Step 4: Run the check against the current vendored state**

```bash
node scripts/check-tailwind-variants.mjs
```

Expected: exit 0, resolving via `vendored`. All eleven strings present. If any is missing, stop — the vendored file is not what the spec describes and the assumption is wrong.

- [ ] **Step 5: Add the script to `package.json` as `check:tailwind-variants`**

Keep the repository's caret-range convention for the other scripts; do not add a dependency.

- [ ] **Step 6: Commit**

```bash
git add src/SqlHarness.Dashboard/ui/scripts/check-tailwind-variants.mjs src/SqlHarness.Dashboard/ui/package.json
git commit -m "test(ui): guard the Tailwind custom variants shadcn components depend on"
```

---

### Task 2: Return the CLI and the real stylesheet import

**Files:**
- Modify: `src/SqlHarness.Dashboard/ui/package.json`
- Modify: `src/SqlHarness.Dashboard/ui/package-lock.json`
- Modify: `src/SqlHarness.Dashboard/ui/src/index.css:3`
- Delete: `src/SqlHarness.Dashboard/ui/src/styles/shadcn-tailwind.css`

**Interfaces:**
- Consumes: the `node scripts/check-tailwind-variants.mjs` script from Task 1, and the 71,761-byte baseline recorded in Task 1 Step 2.
- Produces: `shadcn@4.22.0` in `devDependencies`; `src/index.css` importing `shadcn/tailwind.css`; `src/styles/shadcn-tailwind.css` deleted. Every later task runs `npx shadcn@latest add` against this state.

- [ ] **Step 1: Add the CLI as a development dependency**

Run from `src/SqlHarness.Dashboard/ui`:

```bash
npm install --save-dev shadcn@4.22.0
```

Keep the caret range npm writes (`^4.22.0`). Confirm it landed under `devDependencies`, not `dependencies`.

- [ ] **Step 2: Confirm the production audit is still clean**

```bash
npm --prefix src/SqlHarness.Dashboard/ui audit --omit=dev
```

Expected: exactly the two `dompurify` advisories reached through `monaco-editor`. No `braces`, no `fast-glob`, nothing from `shadcn`. **This is the step that justifies the whole task** — if `shadcn` appears here, `shadcn` was added to `dependencies` rather than `devDependencies`, and the task has failed its purpose.

- [ ] **Step 3: Repoint the stylesheet import**

In `src/index.css`, change line 3:

```css
@import "./styles/shadcn-tailwind.css";
```

to:

```css
@import "shadcn/tailwind.css";
```

Change nothing else in the file. The theme tokens at lines 63–138 stay exactly as they are.

- [ ] **Step 4: Run the guard**

```bash
node scripts/check-tailwind-variants.mjs
```

Expected: exit 0, now resolving via `package`. This is the check that would have caught the silent breakage; it must resolve through `node_modules` before the vendored file is removed.

- [ ] **Step 5: Build and confirm the output is unchanged**

```bash
npm run build
ls -l dist/assets/index-*.css
```

Expected: **71,761 bytes** — byte-identical to the Task 1 baseline. The spec proved this swap produces zero delta. If the size differs, stop and investigate: it means content was lost or gained.

Then delete the vendored file and rebuild:

```bash
git rm src/SqlHarness.Dashboard/ui/src/styles/shadcn-tailwind.css
npm run build
ls -l dist/assets/index-*.css
node scripts/check-tailwind-variants.mjs
```

Expected: still 71,761 bytes, guard still exit 0. Deleting the file must change nothing, because its content is now arriving from `node_modules`.

- [ ] **Step 6: Run the full existing gate before committing**

```bash
npm run typecheck && npm run lint && npm test
```

Expected: all pass, unchanged from before this task. No application code was touched, so no test result may differ.

- [ ] **Step 7: Commit**

```bash
git add src/SqlHarness.Dashboard/ui/package.json src/SqlHarness.Dashboard/ui/package-lock.json src/SqlHarness.Dashboard/ui/src/index.css
git commit -m "build(ui): restore the shadcn CLI and import its stylesheet from node_modules

The vendored copy supplied nine Tailwind custom variants, two accordion
keyframes and 24 utilities, load-bearing in 64 usages across the existing
dropdown-menu, tabs, switch, dialog, tooltip and separator components.
Importing shadcn/tailwind.css resolves the same content: the emitted CSS
is byte-identical at 71,761 bytes. shadcn is a devDependency, so its
unreachable braces advisory stays out of npm audit --omit=dev."
```

---

### Task 3: Correct the instructions that are now wrong

**Files:**
- Modify: `README.md`
- Modify: `docs/superpowers/plans/2026-10-08-dashboard-settings-errors-theme.md`
- Modify: `plans/README.md`

**Interfaces:**
- Consumes: the restored CLI and import from Task 2.
- Produces: no code. Documentation that describes the new workflow accurately, and a queue entry for the follow-on plan.

- [ ] **Step 1: Find every instruction that references the vendored stylesheet**

```bash
grep -rn "shadcn-tailwind" --include=*.md .
```

Expected: at least `README.md`, the 2026-10-08 plan, and the plan 071 convention note. Confirm the working tree's uncommitted changes — these line numbers may sit two lines lower than the spec recorded (`README.md:575` may now be `:577`). Match on text, not line number.

- [ ] **Step 2: Rewrite the `README.md` paragraph**

It currently instructs the reader to point the CLI's import back at the vendored file when the CLI rewrites it. Replace that instruction with the accurate workflow: components are added with `npx shadcn@latest add <component>` from `ui/`; `shadcn` is a declared `devDependency`; its stylesheet is imported from `node_modules` by `src/index.css`; no manual repair step exists or is needed. Drop the advisory rationale, which no longer describes why the CLI is absent from `dependencies`.

- [ ] **Step 3: Fix the same instruction in the two plan documents**

Both the 2026-10-08 plan and the plan 071 convention note carry the identical instruction. Update them to match. These are historical plan records; change the instruction, not the plan's history or status.

- [ ] **Step 4: Record the known dompurify advisories**

Add a note to `README.md` beside the audit information: `npm --prefix src/SqlHarness.Dashboard/ui audit --omit=dev` reports two low-severity `dompurify` advisories reached transitively through `monaco-editor`. They are pre-existing, not reachable through this application, and have no non-breaking fix — `npm audit fix --force` would downgrade monaco across a major version. This exists so the next person to run an audit does not re-report them as new findings.

- [ ] **Step 5: Add the queue entry**

`AGENTS.md` requires the plan index to be updated when plan-backed work lands. Add the follow-on primitive-adoption plan to `plans/README.md` under "Oczekujace", scoped as the `select`, `accordion`, `checkbox`, `textarea`, `radio-group` adoption described in the spec's Parts 2 through 7, with Plan A as its dependency.

- [ ] **Step 6: Commit**

```bash
git add README.md docs/superpowers/plans/2026-10-08-dashboard-settings-errors-theme.md plans/README.md
git commit -m "docs: replace the vendored-stylesheet workaround with the real workflow"
```

---

### Task 4: Confirm the result by hand

**Files:** none.

**Interfaces:**
- Consumes: Tasks 1–3.
- Produces: no code. The human confirmation that no visual regression occurred, recorded in the verification evidence file.

- [ ] **Step 1: Start the dashboard and open every view that consumes a variant-dependent component**

```bash
npm run dev
```

The five Review Focus items name the components to look at. Confirm each, in **both light and dark theme**:

- A switch renders and its checked and disabled states are visible (`SettingsPage`).
- A dialog animates open and close (`StatusBadge` on a failed operation).
- A dropdown menu animates (`ThemeMenu`).
- A tooltip animates (`SessionTable`, or the `StatusBadge` badge itself).
- Tabs show a visually distinct active tab (`StatsPage`, `OperationPage`).
- A separator renders in the correct orientation.

- [ ] **Step 2: Confirm the CLI works without manual repair**

From `src/SqlHarness.Dashboard/ui`:

```bash
npx shadcn@latest add --dry-run accordion
```

Expected: one new file, and **no mention of `index.css`** in the plan of changes. This is the check that the repair instructions removed in Task 3 were obsolete. Do not apply it — adding components is Plan B's work.

- [ ] **Step 3: Record the evidence**

Write a verification evidence file under `plans/` following the convention of `086-verification-evidence.md`, stating the commands run, their results, the two theme confirmations, and the byte-identical CSS size observed in Task 2.