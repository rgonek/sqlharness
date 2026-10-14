# Kolejka planów SQLHarness

Przeczytaj wybrany plan i jego spec przed wykonaniem. Wpis w kolejce nie jest zgodą na implementację, publikację ani live DB. Każdy zlecony plan wykonujemy w całości; pierwszeństwo mają prace ogólne i SQL Server przed wyłącznie PostgreSQL.

## W wykonaniu

| Plan | Zakres | Zależności | Status |
|---|---|---|---|
| [065](065-artifact-plan-pages-and-clipping.md) | Accurate clipping counts and budget-aware saved-plan pages; preserve artifact-read cost | 055/058 DONE; coordinate projection drift | IN PROGRESS — implementation committed as `0b6dce4`; integration verification underway |

## Oczekujące

| Plan | Zakres | Zależności | Status |
|---|---|---|---|
| [036](036-live-database-ci-lane.md) | Direction: opt-in CI lane against throwaway PG/SQL Server containers | 016 (028 for pins) | TODO |
| [063](063-missing-index-overlap.md) | Missing-index hints in the saved `plan` artifact section carry an overlap verdict against the table's existing indexes (inventory captured after measurement) | 060 | TODO |
| [064](064-engine-scoped-agent-guidance.md) | Profile/process-scoped engine guidance in CLI capabilities and MCP descriptions; neutral help and concise operator skills | Current operator-skill/description changes; coordinate shared capabilities/catalog files | TODO |
| [067](067-cell-limit-parity-and-default.md) | MCP `maxCellChars` 0..4096 with CLI parity; shared default 2048 | 060 DONE; coordinate 064/065 | TODO |
| [068](068-operation-phase-timings-and-diagnostics.md) | Journal phase timings (connect/setup/execute/artifact) and opt-in `diagnostics.enabled` connection events | Coordinate 062 (operation page, journal reader) | TODO |
| [069](069-dashboard-session-curation.md) | Dashboard session curation: title, note, star/pin, archive, search (journal `session_annotations`, guarded PATCH) | 062 DONE first; coordinate journal schema version with 062/068 | TODO |
| [070](070-dashboard-agent-transcript-matching.md) | Opt-in agent transcript matching: Claude/Codex conversation titles, Claude model/effort per operation (journal v10 derived tables, housekeeping cycle) | 062 and 069 DONE first; re-verify transcript formats | TODO |
| [071](071-dashboard-compare-review.md) | Compare review: CodeMirror 6 SQL tabs + side-by-side/unified diff, per-table IO and plan changes, Copy Markdown for PRs, session version diff | 062 DONE; **ask the operator to confirm CodeMirror 6 before starting** | TODO |
| [072](072-mutation-risk-tiers.md) | Core risk tiers (`read-only`/`session`/`dml`/`schema`/`privilege`/`opaque`) via `SqlUsage.Mutation` in both engines; `tier` in dry runs; behaviour of existing commands unchanged | — | TODO |
| [073](073-mutation-approval-broker.md) | `mutation` command/tool (CLI+MCP), per-profile modes, dashboard broker executes with writer credential, toast approval, optional WebAuthn; removes `query --allow-mutation`; supersedes tray plan 08 | 072 DONE; next free journal version | TODO |

Przed rozpoczęciem 071 potwierdź u operatora CodeMirror 6. 069 i 071 wymagają zakończonego 062; 070 wymaga także 069. Koordynuj prace zmieniające wspólne kontrakty MCP, journal i dashboard według wybranego planu.

## Historia

Dowody zakończonych planów, dawne kolejki, odrzucone propozycje i decyzje z audytów są w [historii](history.md). Czytaj ją przy badaniu wcześniejszych decyzji lub dowodów, nie przed każdym zadaniem.

Gdy plan zostanie zakończony, uaktualnij jego status, przenieś wpis z kolejki do historii i zachowaj odsyłacz do dowodów.
