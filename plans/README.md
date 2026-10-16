# Kolejka planów SQLHarness

Przeczytaj wybrany plan i jego spec przed wykonaniem. Wpis w kolejce nie jest zgodą na implementację, publikację ani live DB. Każdy zlecony plan wykonujemy w całości; pierwszeństwo mają prace ogólne i SQL Server przed wyłącznie PostgreSQL.

## W wykonaniu

| Plan | Zakres | Zależności | Status |
|---|---|---|---|

## Oczekujące

| Plan | Zakres | Zależności | Status |
|---|---|---|---|
| [036](036-live-database-ci-lane.md) | Direction: opt-in CI lane against throwaway PG/SQL Server containers | 016 (028 for pins) | TODO |
| [077](077-agent-sql-optimization-evidence.md) | Lean MCP JSON and actionable SQL optimization evidence; portable real-MCP fixture requests and paired agent trials verify the complete workflow | 055–061/065–067/076 DONE; coordinate 063/073 | TODO — planning only |
| [078](078-mutation-approval-webauthn.md) | Deferred Windows Hello/WebAuthn verification, enrollment, credential lifecycle and recovery for mutation approvals | 073 DONE | TODO |

Plans 063, 070, 071, 072, 073, 074, and 076 are DONE; see their evidence in [history](history.md). Plan 071 uses Monaco behind a provider-neutral SQL editor contract, with direct Monaco API lifecycle management for the diff widget. Coordinate work that changes shared MCP, journal, and dashboard contracts through the selected plan.

## Historia

Dowody zakończonych planów, dawne kolejki, odrzucone propozycje i decyzje z audytów są w [historii](history.md). Czytaj ją przy badaniu wcześniejszych decyzji lub dowodów, nie przed każdym zadaniem.

Gdy plan zostanie zakończony, uaktualnij jego status, przenieś wpis z kolejki do historii i zachowaj odsyłacz do dowodów.
