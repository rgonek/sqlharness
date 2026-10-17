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
| [080](080-optimization-dashboard.md) | Stage B optimization dashboard: session Optimization section (version table, two-version comparison, case/aggregate, Mixed, Markdown and scripts), compare-first operation page, de-noised lists/badges, Statistics → Usage | 077, 079 merged | TODO |
| [081](081-session-control-flow-safety.md) | SQL Server read-only paths admit allowlisted SET options, WHILE/TRY/RETURN/THROW/PRINT, transactions with automatic rollback and read-only cursors; mutation path unchanged; EXEC stays denied; opt-in dashboard safety notifications | 079 merged | TODO |
| [082](082-statement-io-attribution.md) | Stage C per-statement IO from actual plans reconciled with STATISTICS IO; shared vs changed statements; verdicts on changed statements when confirmed; by-statement view and hidden-work flags | 079, 080, 081, 087 merged; post-081 UAT spike | TODO |
| [083](083-table-and-list-parameters.md) | Stage D table-valued parameters (inline rows or rows from a read-only query, catalog-resolved types, Structured binding) and Dapper list expansion across read-only tools; conditions, labels and SSMS scripts; mutation rejects them | 077, 079 merged | TODO |
| [084](084-agent-sessions.md) | Agent sessions across MCP/CLI sessions (matching on): per-operation membership exact/inferred/unassigned, immediate Codex linking + cursor fix, event-driven Claude matching, agent-session list/page/annotations, Optimization per agent session with frozen version labels, subagents folded into parent | 079, 080, 087 merged | TODO |
| [085](085-operation-annotations.md) | Opt-in agent operation labels/keep at call time + post-hoc annotation tool by operationRef scoped to the agent session; owner operation star/note and Named operations view | 077, 079, 084 merged | TODO |
| [087](087-measure-in-optimization.md) | measure accepts subject/scope; measurement fingerprint without comparison settings; measure runs as version observations in the Optimization view (pair basis, defaults, results not compared) | 080 merged; before 082 and 084 | TODO |
| [088](088-plan-visualization.md) | SQL Server plan viewer: content-addressed per-repetition plan documents, ScriptDom statement spans (3999-char and PSP-suffix tolerant), server-side plan graph, renderer chosen by benchmark, Monaco two-way linking at statement and table-reference level with confidence states | 080, 087 merged; coordinate 082 | TODO |

Plans 063, 070, 071, 072, 073, 074, 076, and 079 are DONE; see their evidence in [history](history.md). Plan 071 uses Monaco behind a provider-neutral SQL editor contract, with direct Monaco API lifecycle management for the diff widget. Coordinate work that changes shared MCP, journal, and dashboard contracts through the selected plan.

## Historia

Dowody zakończonych planów, dawne kolejki, odrzucone propozycje i decyzje z audytów są w [historii](history.md). Czytaj ją przy badaniu wcześniejszych decyzji lub dowodów, nie przed każdym zadaniem.

Gdy plan zostanie zakończony, uaktualnij jego status, przenieś wpis z kolejki do historii i zachowaj odsyłacz do dowodów.
