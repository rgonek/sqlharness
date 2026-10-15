# Kolejka planów SQLHarness

Przeczytaj wybrany plan i jego spec przed wykonaniem. Wpis w kolejce nie jest zgodą na implementację, publikację ani live DB. Każdy zlecony plan wykonujemy w całości; pierwszeństwo mają prace ogólne i SQL Server przed wyłącznie PostgreSQL.

## W wykonaniu

| Plan | Zakres | Zależności | Status |
|---|---|---|---|

## Oczekujące

| Plan | Zakres | Zależności | Status |
|---|---|---|---|
| [036](036-live-database-ci-lane.md) | Direction: opt-in CI lane against throwaway PG/SQL Server containers | 016 (028 for pins) | TODO |
| [063](063-missing-index-overlap.md) | Missing-index hints in the saved `plan` artifact section carry an overlap verdict against the table's existing indexes (inventory captured after measurement) | 060 | TODO |
| [071](071-dashboard-compare-review.md) | Compare review: CodeMirror 6 SQL tabs + side-by-side/unified diff, per-table IO and plan changes, Copy Markdown for PRs, session version diff | 062 DONE; **ask the operator to confirm CodeMirror 6 before starting** | TODO |
| [073](073-mutation-approval-broker.md) | `mutation` command/tool (CLI+MCP), per-profile modes, dashboard broker executes with writer credential, toast approval, optional WebAuthn; removes `query --allow-mutation`; supersedes tray plan 08 | 072 DONE; next free journal version | TODO |
| [076](076-partial-matrix-journal-runs.md) | A partial matrix `compare` journals the completed cells' benchmark metrics (runs carried on the failure exception and partial outcomes) | — | TODO |

Plans 070 and 072 are DONE; see their evidence in [history](history.md). Before starting 071, confirm CodeMirror 6 with the operator; 071 requires 062 DONE. Coordinate work that changes shared MCP, journal, and dashboard contracts through the selected plan.

## Historia

Dowody zakończonych planów, dawne kolejki, odrzucone propozycje i decyzje z audytów są w [historii](history.md). Czytaj ją przy badaniu wcześniejszych decyzji lub dowodów, nie przed każdym zadaniem.

Gdy plan zostanie zakończony, uaktualnij jego status, przenieś wpis z kolejki do historii i zachowaj odsyłacz do dowodów.
