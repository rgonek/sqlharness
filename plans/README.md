# Kolejka planów SQLHarness

Przeczytaj wybrany plan i jego spec przed wykonaniem. Wpis w kolejce nie jest zgodą na implementację, publikację ani live DB. Każdy zlecony plan wykonujemy w całości; pierwszeństwo mają prace ogólne i SQL Server przed wyłącznie PostgreSQL.

## W wykonaniu

| Plan | Zakres | Zależności | Status |
|---|---|---|---|
| [086](086-windows-gate-speedup.md) | Skrócenie bramki Windows: restore wbudowany w build, `ui-check` równolegle z buildem, równoległe projekty testowe, szybsze seedowanie journalu w testach czytających, diagnozowalny flaky housekeeping, usunięcie testu atrybutów xunit | 076 DONE | IMPLEMENTED on `opt-windows-gate` — see [evidence](086-verification-evidence.md) |

## Oczekujące

| Plan | Zakres | Zależności | Status |
|---|---|---|---|
| [036](036-live-database-ci-lane.md) | Direction: opt-in CI lane against throwaway PG/SQL Server containers | 016 (028 for pins) | TODO |
| [077](077-agent-sql-optimization-evidence.md) | Lean MCP JSON and actionable SQL optimization evidence; portable real-MCP fixture requests and paired agent trials verify the complete workflow | 055–061/065–067/076 DONE; coordinate 063/073 | IN PROGRESS — implementation merged locally into main (`4bd57e4`); actual-agent acceptance unverified (nine provider HTTP 402 errors) |
| [078](078-mutation-approval-webauthn.md) | Deferred Windows Hello/WebAuthn verification, enrollment, credential lifecycle and recovery for mutation approvals | 073 DONE | TODO |
| [082](082-statement-io-attribution.md) | Stage C per-statement IO from actual plans reconciled with STATISTICS IO; shared vs changed statements; verdicts on changed statements when confirmed; by-statement view and hidden-work flags | 079, 080, 081, 087 merged; post-081 UAT spike | TODO |
| [083](083-table-and-list-parameters.md) | Stage D table-valued parameters (inline rows or rows from a read-only query, catalog-resolved types, Structured binding) and Dapper list expansion across read-only tools; conditions, labels and SSMS scripts; mutation rejects them | 077, 079 merged | TODO |
| [084](084-agent-sessions.md) | Agent sessions across MCP/CLI sessions (matching on): per-operation membership exact/inferred/unassigned, immediate Codex linking + cursor fix, event-driven Claude matching, agent-session list/page/annotations, Optimization per agent session with frozen version labels, subagents folded into parent | 079, 080, 087 merged | TODO |
| [085](085-operation-annotations.md) | Opt-in agent operation labels/keep at call time + post-hoc annotation tool by operationRef scoped to the agent session; owner operation star/note and Named operations view | 077, 079, 084 merged | TODO |

Plans 063, 070, 071, 072, 073, 074, 076, 079, 080, 081, and 087 are DONE; see their evidence in [history](history.md). Plan 081 is locally integrated into `main` at `e88c7fd`; its integrated gate passed on the exact product tree before commit, and all task, security, whole-branch, and integration reviews are approved ([evidence](081-verification-evidence.md)). Plan 088 implementation, the final-review fix wave, and the final Windows gate are complete on `plan-088`; Task 7 review is approved and the Sol low scoped re-review of the final fixes remains pending. It is archived in [history](history.md) without an integration claim ([evidence](088-verification-evidence.md)). Plan 071 uses Monaco behind a provider-neutral SQL editor contract, with direct Monaco API lifecycle management for the diff widget. Plan 080's scoped re-review approved all findings, and local integration completed at `42a1e365` with the verified product unchanged; the redundant gate was stopped at the owner's request. Coordinate work that changes shared MCP, journal, and dashboard contracts through the selected plan.

## Historia

Dowody zakończonych planów, dawne kolejki, odrzucone propozycje i decyzje z audytów są w [historii](history.md). Czytaj ją przy badaniu wcześniejszych decyzji lub dowodów, nie przed każdym zadaniem.

Gdy plan zostanie zakończony, uaktualnij jego status, przenieś wpis z kolejki do historii i zachowaj odsyłacz do dowodów.
