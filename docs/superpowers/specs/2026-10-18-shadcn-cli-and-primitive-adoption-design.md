# Design: shadcn CLI and primitive adoption

**Date:** 2026-10-18
**Status:** design, awaiting owner approval
**Scope:** `src/SqlHarness.Dashboard/ui` only. No backend, no MCP, no database behavior.

## Intent

The dashboard UI carries a workaround that no longer has a technical reason to exist. The `shadcn` CLI
was removed as a project dependency in `c5189e77` to silence a `braces` advisory, and the consequences
were kept: a vendored copy of the CLI's stylesheet, and two manual repair steps after every
`shadcn add`. The vendored copy cannot drift from upstream without anyone noticing, and the manual
steps are repeated by every author and every agent.

This work removes the workaround and closes the gap between the components the UI uses and the
primitives shadcn already provides, so the dashboard is built from the generated component set rather
than from hand-written approximations of it. The owner chose the wide scope: every remaining
hand-written approximation is replaced, including the native disclosure elements.

## What was verified, and how

Every factual claim below was checked against the repository or against `shadcn@4.22.0` in an
isolated scratch project on 2026-10-18. Nothing here rests on documentation.

**The advisory is dev-only, and the production audit already excludes dev dependencies.**
`shadcn` depends on `fast-glob`, which depends on `braces@3.0.3`, which has no fixed release. A
scratch project with `shadcn` installed reports 7 high findings under a plain `npm audit`. The same
project reports **0 vulnerabilities** under `npm audit --omit=dev`, because the flag drops
`devDependencies` from the tree it walks. `shadcn` is build-time scaffolding: never imported by
application code, never shipped.

**`shadcn add` never needed the vendored stylesheet.** A dry run of
`add checkbox select textarea radio-group accordion` against this repository reports five new files
and one dependency (`cn`, already present). It does not touch `index.css` and adds `shadcn` to no
dependency list. Only `init` requires the stylesheet, and `init` already ran in `a40caeac`.

**The CLI generates Base UI, not Radix.** `components.json` declares `"style": "base-nova"`, and the
generated `checkbox.tsx` imports from `@base-ui/react/checkbox`, matching the 15 primitives already in
`components/ui`. Adding does not introduce a second primitive library. `add` has no base flag and
follows `components.json`; only `init` takes `--base`.

**The generated `Select` is not a native `<select>`.** It is `SelectPrimitive.Trigger` — a button with
`role="combobox"` — presenting options in a portal. `userEvent.selectOptions` requires a real
`<select>` and throws otherwise. This is the single largest cost in this work and is why the select
step is sequenced separately.

**The import swap is a zero-delta change, proved by building it.** `shadcn@4.22.0` ships
`package/dist/tailwind.css` with an explicit `"./tailwind.css"` export in its `package.json`, and
Tailwind v4's CSS resolver handles the bare specifier from `node_modules` with no configuration. That
file was extracted and line-diffed against the vendored copy: **byte-identical except the five-line
header comment**, confirming the vendored file is indeed a frozen `4.21.4` copy and that `4.22.0` has
not drifted. A real `npm run build` with `@import "shadcn/tailwind.css"` produced output CSS
byte-identical to the vendored-import build at 71,761 bytes.

**The icon transform is already configured.** The live registry items for `base-nova` reference an
`IconPlaceholder` module that does not exist here, but `components.json` declares
`"iconLibrary": "lucide"` and the dry-run output imports `ChevronDownIcon`, `CheckIcon` and
`ChevronUpIcon` directly from `lucide-react`, matching the existing generated components. `shadcn add`
will not produce dangling imports.

## Goals

- `shadcn` is a declared `devDependency`, pinned by `package-lock.json` at `4.22.0`.
- The vendored `src/styles/shadcn-tailwind.css` is deleted and its content is reached through the real
  `shadcn/tailwind.css` import that resolves from `node_modules`.
- No manual repair step remains after `shadcn add`.
- All 14 hand-written `<select>`, all 3 hand-written `type="checkbox"`, both hand-written `<textarea>`,
  both `SqlDiff` radio inputs, all 10 native `<details>` and the one hand-written dialog trigger are
  replaced by generated primitives.
- `npm --prefix src/SqlHarness.Dashboard/ui audit --omit=dev` reports no findings other than the two
  pre-existing `dompurify` advisories reached through `monaco-editor`.

## Non-goals

- No restyling. Existing layout utilities, theme tokens and the `neutral` base color stay as they are.
  The 2026-10-06 design's "no custom CSS and no theme changes" rule still holds.
- No domain components are rewritten. `PlanTree`, `SessionTable`, `OperationTable`, `SqlBlock`, `Kpi`,
  `VariantPanel` and `BrandLogo` encode product logic, not UI primitives.
- **`optimization/VersionTable.tsx:21,22` keeps its two native radio inputs.** They render once per
  version inside `<td>` cells, grouped only by a `name` attribute, with no fieldset and no
  `radiogroup` anywhere in the component. `RadioGroup` renders a container and cannot span table rows,
  so adopting it would mean dismantling the table or shipping a broken grid. Seven assertions in
  `OptimizationSection.test.tsx` (lines 57, 58, 76, 77, 114, 124, 126) target these radios and are
  scoped with `within(table)`. This is a table, not a form, and it is the one deliberate exception to
  full adoption.
- No change to `dompurify` or the monaco editor. Those advisories are transitive, low severity,
  unreachable through this application, and have no non-breaking fix. This work documents them as
  pre-existing; it does not resolve them.
- No upgrade of `shadcn` past `4.22.0`.

## Constraints

- Base UI primitives only. Any component importing from `@radix-ui/*` is wrong for this repository.
- Generated files in `components/ui/` are not hand-edited. Changes go through `shadcn add`, or through
  a reviewed diff against upstream.
- Accessible names and roles are a test contract. Existing queries must keep resolving, or be updated
  deliberately in the same change that alters the markup, with the reason recorded.

## The stylesheet problem, stated precisely

An earlier draft of this design proposed deleting the vendored stylesheet after checking that
`index.css` still declared the theme tokens the components consume. **That check is wrong and would
have passed while the build silently broke.**

`src/styles/shadcn-tailwind.css` declares **no theme tokens at all**. It declares nine Tailwind v4
custom-variant blocks — `data-open`, `data-closed`, `data-checked`, `data-unchecked`, `data-selected`,
`data-disabled`, `data-active`, `data-horizontal`, `data-vertical` — plus the `accordion-down` and
`accordion-up` keyframes and the `no-scrollbar`, `scroll-fade` and `shimmer` utilities.

`src/index.css` declares exactly one custom variant, `dark`. Those nine variants are load-bearing in
**64 usages across 17 lines in 6 existing generated components**: `dropdown-menu.tsx` (21 usages),
`tabs.tsx` (12), `switch.tsx` (11), `dialog.tsx` (10), `tooltip.tsx` (6), `separator.tsx` (4).
`data-selected` currently has no usage and is needed only by newly generated components.

Deleting the file therefore removes checked and disabled styling from every switch, open and close
animation from every dialog, dropdown and tooltip, active-tab styling, and separator orientation. No
type error results. No unit test necessarily results. The build stays green and the dashboard ships
visibly broken.

The accordion added in this work animates through exactly those two keyframes, so a newly added
accordion would arrive with no animation and no open/close variants either.

**Resolution.** `src/index.css` imports `shadcn/tailwind.css` from the restored `devDependency`, which
is what the CLI itself writes. The vendored copy is deleted and the nine variants, both keyframes and
the utilities reach the build through a resolvable import from `node_modules`. This removes the
divergence risk that motivated vendoring in the first place, because the import is now real rather
than a frozen copy.

After the change, confirm the nine variants, both `accordion-*` keyframes and the utility set are
present in `node_modules/shadcn/dist/tailwind.css` and resolve through the import. Do **not** try to
confirm this by grepping the built CSS: `accordion-down` and `accordion-up` appear zero times in the
emitted stylesheet, because `@theme inline` emits a keyframe only when a utility references it. The
accordion keyframes can only be confirmed visually once Part 7 lands.

The build's output CSS must remain byte-identical to the pre-change build. That is the strongest
available evidence that nothing was lost in the swap, and it should be checked rather than assumed.

## Work

### Part 1 — return the CLI to the project

`npm install --save-dev shadcn@4.22.0` from `ui/`, keeping the caret range npm writes.

In `src/index.css`, replace `@import "./styles/shadcn-tailwind.css";` with
`@import "shadcn/tailwind.css";`. Delete `src/styles/shadcn-tailwind.css`. Confirm the nine custom
variants, both accordion keyframes and the utility set all resolve, per the section above.

Update the documentation that now gives wrong instructions:

- `README.md:575` tells the reader to point the CLI's import back at the vendored file. Remove that
  instruction rather than leave it to mislead the next author.
- `docs/superpowers/plans/2026-10-08-dashboard-settings-errors-theme.md` and the plan 071 convention
  note carry the same instruction.
- `README.md:546` mentions the **Show internal operations** control. Its wording stays accurate.

Part 1 alone closes the workaround the owner asked to remove. Parts 2 and 3 are separable and can
land independently.

### Part 2 — add the missing primitives

`npx shadcn@latest add checkbox select textarea radio-group accordion`

Verified by dry run to create five files, modify none of the existing fifteen, and add no dependency
beyond `cn`. The version skew against the `4.21.4` that generated the existing set (per the vendored
file's header) is therefore not expected to produce a generated-file diff; if one appears, review it
rather than accepting it silently.

### Part 3 — checkbox

Sites: `SessionsPage.tsx:45`, `InternalOperationsToggle.tsx:2`, `SqlDiff.tsx:24`.

Base UI's checkbox root carries `role="checkbox"`, so these existing queries keep resolving:
`SessionsPage.test.tsx:94`, `OperationsPage.test.tsx:43,49`, `LivePage.test.tsx:152`, and
`SqlDiff.test.tsx:29,59` for "Ignore formatting". Note `OperationPage.test.tsx` contains no checkbox
query and is unaffected.

The controlled API changes from `onChange` to `onCheckedChange`. `InternalOperationsToggle` keeps its
text verbatim, so the persisted local-storage behavior in `lib/internalOperations.ts` is untouched;
only rendering changes.

For the accessible name, prefer an explicit `<Label htmlFor>` plus `id` over the existing wrapping
`<label>`. Base UI's root is a `<button>`, which is a labelable element, so the wrapping form would
still work — but it depends on a subtlety worth not depending on.

### Part 4 — textarea

`SessionNoteCard.tsx:30` and `CopyButton.tsx:49`. Straight substitution; `CopyButton`'s `onFocus`
select-all behavior is preserved. No test queries a textarea by role.

### Part 5 — radio-group

`SqlDiff.tsx:19-23`. The hand-written `<fieldset role="radiogroup">` already matches the primitive's
role contract, so `DiffControls` keeps its shape and only the two inputs change. `DiffMode` and the
`MODE_KEY` preference are unchanged.

One risk to confirm empirically rather than assume: `SessionVersionDiff.test.tsx:73-76` asserts
arrow-key roving focus — focus "Side by side", press `{ArrowRight}`, expect "Unified" to be checked.
That is a native radio contract. Base UI implements arrow navigation, but whether it selects on arrow
or on focus must be observed before this step is called done. `SqlDiff.test.tsx:26` also exercises
these radios.

The two radio inputs in `optimization/VersionTable.tsx:21,22` are explicitly out of scope; see
Non-goals.

### Part 5b — StatusBadge dialog trigger

`StatusBadge.tsx:24` is `TooltipTrigger render={<button type="button" aria-haspopup="dialog"
onClick={...} />}`, a bare button that is simultaneously a tooltip trigger and a dialog trigger, driven
by a local `useState` open flag at line 12.

Replace it with `DialogTrigger` and drop the manual open state, keeping the tooltip composition
intact. The owner accepted this: it removes hand-written controlled-open logic rather than restyling
anything. The hazard is that Base UI's `mergeProps` will contend over `role`, `aria-haspopup` and
`ref` when a trigger sits inside another trigger's render target. Resolve it explicitly and confirm the
accessible name and `aria-haspopup` survive. Five cases in `StatusBadge.test.tsx` assert
`getByRole("dialog")`.

### Part 6 — select

Fourteen hand-written sites:

| File | Lines |
|---|---|
| `ProfileDimensionsPanel.tsx` | 78, 220, 370, 377, 384 |
| `SettingsPage.tsx` | 139, 342 |
| `StatsPage.tsx` | 162 |
| `CompareDetail.tsx` | 60, 61 |
| `SessionVersionDiff.tsx` | 88, 93 |
| `optimization/OptimizationSection.tsx` | 67, 73 |

Because the trigger is a button, every `userEvent.selectOptions` and every `toHaveValue` on a combobox
must be re-expressed as open-then-choose and as an assertion on the trigger's rendered value.
Approximately 27 such assertions, concentrated in `StatsPage.test.tsx` (8 `selectOptions` plus 8
`toHaveValue`), `SettingsMutations.test.tsx` (2 plus 1), `CompareDetail.test.tsx` (2 `selectOptions`,
1 `toHaveValue`, and 1 `fireEvent.change`), `SessionVersionDiff.test.tsx` (1 plus 2) and
`OperationPage.test.tsx` (1).

`SettingsPage.test.tsx` has no select assertions; its `toHaveValue` calls target spinbuttons and are
unaffected.

Four specific hazards:

- **`ProfileDimensionsPanel.tsx:78` is a per-dimension filter**, rendered once per dimension via
  `displayedDimensions.map`, so it is *N* conversion sites rather than one. Its accessible name comes
  only from `<Label htmlFor>`; there is no `aria-label`, and `StatsPage.test.tsx:121,237` target it by
  name. It carries a **tri-state** that must survive: `""` means *undefined* (All values), `"missing:"`
  means `null`, `"value:<x>"` is a concrete value, parsed back through `parseValueOption` at line 82.
  Collapsing `undefined` and `null` while rewriting `onChange` into the Base UI value callback is the
  easiest way to break this.
- **`optimization/OptimizationSection.tsx:67,73`** are the hardest pair in the codebase. Both render
  only when `groups.length > 1` and `scopes.length > 1` respectively, both compute **dynamic**
  accessible names from `subject.subject`, and changing the group select **side-effects** the scope
  select by resetting scope to the new group's first scope at line 69. This chained behavior must be
  preserved deliberately; its tests are `OptimizationSection.test.tsx` and
  `optimization/ComparisonPanel.test.tsx`.
- **`StatsPage.tsx:162`** carries `id="stats-profile"`, a `<Label htmlFor>` **and**
  `aria-label="Statistics profile"` simultaneously — two competing accessible names, with tests using
  the aria-label path. A Base UI trigger computes its name differently from a native select; keep both
  and confirm which one `getByRole("combobox", { name })` resolves, or the query falls through and
  matches nothing.
- **Portal-rendered options.** `screen` queries still resolve them because the portal lands in
  `document.body`, but any assertion scoped with `within(container)` will not find them.

**Trigger sizing.** The generated trigger is `h-8` at `size="default"` and `h-7` at `size="sm"`. There
is **no `h-9`**, so `SettingsPage.tsx:139,342`, which are currently `h-9`, must take a `className`
override. The trigger also bakes in `w-fit` and `bg-transparent` against the current `min-w-52`,
`min-w-40` and `bg-background`. This collides with the "no restyling" non-goal, so the rule for this
work is: preserve each site's existing height and width classes through `className` where the variant
does not match, and do not otherwise adjust appearance. Decide that once, apply it uniformly, and
note it in the commit rather than re-deciding per site.

The generated trigger is 32px tall by default, matching the existing `h-8` in `ProfileDimensionsPanel`
and `h-9` in `SettingsPage`. Check each site rather than assuming; prefer the `size` variant where it
matches and `className` only where it does not.

### Part 7 — accordion, all ten sites

Every native `<details>` becomes a shadcn accordion item:

| File | Lines |
|---|---|
| `CompareDetail.tsx` | 94, 95, 96 |
| `OperationPage.tsx` | 93, 199, 281 |
| `optimization/OptimizationSection.tsx` | 28 |
| `ProfileDimensionsPanel.tsx` | 394 |
| `SessionPage.tsx` | 57 |
| `TableIoComparison.tsx` | 18 |

This is the highest-cost step and the owner accepted it knowingly.

**The existing tests bind to the `<details>` element itself, not to its content.** Approximately 18
assertions in `OperationPage.test.tsx` (about 13) and `CompareDetail.test.tsx` (5) use
`.closest("details")`, `not.toHaveAttribute("open")`, or filter on
`tagName.toLowerCase() === "summary"`. An accordion renders no `details`, no `summary` and no `open`
attribute, so every one of these is a rewrite, not a verification. The replacements assert the
observable behavior instead: the trigger carries the expanded state, clicking it reveals the panel,
and content is reachable once open.

**`ProfileDimensionsPanel.tsx:394` is the structural exception.** Its `<details>` renders **once per
dimension with `open` set**, so every dimension starts expanded. Preserving that requires an accordion
configured for multiple simultaneous opens with controlled state and an open-set in the component,
not a `defaultOpen` per item. This is a rewrite of that panel's disclosure state management, and it is
the one place in this work where the risk is behavioral rather than merely structural.

`CompareDetail`'s three disclosures are adjacent and form a natural accordion group. The others are
scattered — `OperationPage`'s three sit in different regions of the page, and `SessionPage`'s and
`ProfileDimensionsPanel`'s are unrelated to anything else. Where disclosures are unrelated, each is an
independent single-item accordion; grouping them would impose a mutual-exclusion semantic that the
current markup does not have, and that would be a behavior change rather than a substitution.

Whether accordion content stays mounted while collapsed affects tests that assert inside a panel after
opening it. Confirm the observed behavior per component rather than assuming parity with `<details>`.

**Nesting stays.** `TableIoComparison.tsx:18` renders `<details>More</details>` inside the "Plans"
disclosure at `CompareDetail.tsx:95`, so the result is an accordion nested in an accordion item. That
is accepted deliberately: it preserves the current component tree and the existing UX, whereas
hoisting "More" out of the Plans panel would change both and would also invalidate
`CompareDetail.test.tsx:88`. A nested accordion must be confirmed to open and close independently of
its parent, and must not collapse the panel that contains it.

## Risks

- **Silent visual breakage in Part 1** if the custom-variant resolution is not confirmed by hand. This
  is the highest-impact risk in the work precisely because typecheck, lint and build all pass anyway.
  The mitigation is the explicit visual confirmation listed above.
- **Test breakage in Part 6**, mechanical and loud rather than subtle: tests fail rather than pass
  wrongly. Sequenced as its own step with its own test run.
- **Behavioral rewrite in `ProfileDimensionsPanel`**, the only place where semantics genuinely change.
- **Regression in collapsed-content behavior** across all ten accordion conversions.
- **Portal focus and scoping** in Part 6, described above.

## Verification

The scoped checks in `docs/agents/verification.md` apply, plus:

- `npm run typecheck`, `npm run lint`, `npm test` from `ui/`, all green.
- `npm --prefix src/SqlHarness.Dashboard/ui audit --omit=dev` reports only the two pre-existing
  `dompurify` advisories. This command exits non-zero on those two, so the criterion is "no findings
  beyond dompurify", not a clean exit. It is a convention rather than a `package.json` script.
- `npm run build` succeeds, confirming the removed vendored import is not required and the new
  `shadcn/tailwind.css` import resolves.
- `grep` confirms no `<select`, no `type="checkbox"`, no `<textarea`, no `<details` and no bare
  `<summary` remains outside the seven domain components listed as non-goals and
  `optimization/VersionTable.tsx`. This criterion is only satisfiable because the owner accepted full
  accordion adoption; it was unsatisfiable at the earlier scope.
- The build's emitted `index-*.css` is byte-identical to the pre-change build, proving the stylesheet
  swap lost nothing.
- **Manual, in both light and dark theme**, because the primary change is visual and no automated check
  here can see it: a switch toggles and shows its checked and disabled states; a dialog, dropdown and
  tooltip animate open and close; tabs show an active state; separators orient correctly; an accordion
  expands and animates; a nested accordion opens without collapsing its parent; every converted select
  opens and its options are reachable; every converted disclosure opens; the `StatusBadge` tooltip and
  its dialog still open from the same control.
- `node_modules/shadcn/dist/tailwind.css` contains all nine custom variants and both `accordion-*`
  keyframes. Do not attempt this against the built CSS.

## Documentation to update

- `README.md:575` — the vendored-stylesheet instruction, now wrong. Note the working tree has
  uncommitted changes that shift these line numbers by two; confirm against `HEAD` before editing.
- `docs/superpowers/plans/2026-10-08-dashboard-settings-errors-theme.md` and the plan 071 convention
  note — the same instruction.
- `README.md:546` — accurate as written; no change needed.
- `plans/README.md` — add the resulting plan to the queue when implementation lands, per `AGENTS.md`.
- A note recording that the `dompurify` advisories through `monaco-editor` are known, pre-existing,
  low severity and unreachable, so the next agent running an audit does not re-report them as new.

## Left to the builder

- Whether `ProfileDimensionsPanel`'s per-dimension disclosure uses an accordion with controlled
  multiple-open state or independently configured single-item accordions. The controlled version
  preserves the "all dimensions start expanded" contract more directly.
- The exact `className` overrides that keep each select site's existing height and width, decided by
  comparison against current markup rather than by convention, then applied uniformly.
- Whether `OptimizationSection`'s chained group and scope selects stay visible inline or move into a
  disclosure; the current markup is not a disclosure, so this is a judgment about density rather than
  a substitution.