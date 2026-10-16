# Plan history

Snapshot of the previous index, archived on 2026-10-14. Status and queue notes below are historical; use [the current queue](README.md) for pending work.

## Plan 076 completion — 2026-10-16

Plan [076](076-partial-matrix-journal-runs.md) is DONE and integrated locally into `main` by fast-forward to `6a50e3f` (branch `plan-076`, product commit `57a1e7d`). Completed cells of a partial matrix comparison are journaled; the failing cell contributes no metrics. Feature-branch Windows `just verify` exited 0 on the working tree that product commit records (parent `1d9dbfe` plus the journal test): `verify: OK  stages=6  3m47s` (ui-install 27,8s; ui-check 31,1s, 238 tests; restore 7,7s; build 15,5s, 0 warnings/errors; test 1m35s, Core 3,843 passed/11 skipped, MCP 386 passed/4 skipped; format 49,9s, 1 changed C# file). Post-merge Windows `just verify` on `6a50e3f` exited 0: `verify: OK  stages=6  2m25s` (ui-install skipped; ui-check 36,9s, 238 tests; restore 10,8s; build 14,7s; test 1m22s, Core 3,843 passed/11 skipped, MCP 386 passed/4 skipped; format skipped, 0 changed C# files). An earlier post-merge run failed the unrelated `DashboardHousekeepingTests.External_journal_commit_during_matching_refreshes_the_idle_window` race; that test passed alone and in the green gate. Linux was not run. No push, installation, or publication.

## Plan 071 completion — 2026-10-15

Integrated locally into `main` on 2026-10-16 at `6fd982ca762ada4f2a4d6a42a720eaa6b4c3d674`, preserving setup-SQL replay, Plan 072, and Plan 074. Final review found incomplete version choices beyond the first compare-history page; fix `904b04d` supplies complete per-scope version/hash inventories and disambiguates repeated labels. Luna xhigh task/integration reviews and Sol low whole-branch/scoped re-review approved. Post-integration Windows `just verify` on `6fd982c` exited 0, six stages in 3m00s: UI 238 passed; Core 3,839 passed/11 skipped; MCP 386 passed/4 skipped; build passed; format skipped on the clean tree after the resolved merge tree had passed formatting for 26 C# files. No `FAILED` stage-log markers. Full UI runs print non-failing localhost:3000 EventSource errors and DEP0190; isolated page suites pass. Linux omitted per policy; no push. Summary log: `D:/temp/sqlharness-plan071-main-verify.log`. Earlier feature-only evidence follows; SDD scratch reports are temporary and removed after completion.

Plan [071](071-dashboard-compare-review.md) is DONE on feature branch `plan-071`, implementation range `47a303d..218f39e` (Task 7 documentation follows the tested product commit). `npm run check` passed 231 tests; `npm run build` passed with separate main/editor/worker assets at 1,329.06/2,722.79/303.93 kB. Windows `just verify` passed all six stages on product commit `218f39e`. The isolated synthetic Chromium fixture passed 18/18 checks, including keyboard controls, copy contents and fallback selection, plan and IO cards, matrix regression, hash-only and 300 KiB SQL cases, session SQL diff, 390 px layout, same-origin worker (200), and zero console/page/CSP errors. Monaco remains behind the provider-neutral contract; the diff widget now uses Monaco's API to detach before model disposal, fixing a reproduced disposal error. Linux was omitted under `docs/agents/verification.md`. No live database, operator home, integration, or push. The comprehensive Task 7 report is retained locally under `.superpowers/sdd/071-dashboard-compare-review/task-7-report.md` and is intentionally not committed.
## Plan 072 completion — 2026-10-15

Plan [072](072-mutation-risk-tiers.md) is DONE and integrated locally into `main` at `90d32c7` (merge commit `a500d62`), implementation range `7a2482c..2aafd6a`. Integrated revision `90d32c7f44b1af1bac2759285a7d95f191f0f5cf` passed Windows `just verify` (exit 0; six-stage gate; Core 3,835 passed/11 skipped; MCP 386 passed/4 skipped; UI checks and build passed). Format was skipped with 0 changed C# files; UI install was skipped because the lockfile was unchanged. Linux was skipped at the operator's direction and remains unverified; it is not a completion criterion while CI runs locally. Final scoped review findings were resolved and the last re-review was clean. SqlParserCS 0.6.5 parser limits remain fail-closed for `DO`, `CREATE/ALTER USER`, canonical `ANALYZE`, tested `ALTER VIEW ... RENAME`, and `CREATE PROCEDURE` forms. [Verification evidence](072-mutation-risk-tiers.md#final-verification-evidence). No push, installation or publication.
## Plan 075 completion — 2026-10-15

Plan [075](075-dashboard-stop-restart.md) is DONE on `feat/plan-075-dashboard-lifecycle`; local integration pending. Final product revision `1524b44` passed Windows `just verify` (UI 178; Core/CLI 3587 passed/11 skipped; MCP 386 passed/4 skipped; build 0 warnings/errors; six stages in 5m14s). Luna high implementation, Luna xhigh task reviews and Sol low whole-branch review completed; final scoped review approved. Linux was explicitly waived by the operator during its run; no full Linux pass is claimed. [Evidence](../docs/verification/plan-075.md). No merge, push, installation, publication or operator-dashboard shutdown.

## Plan 064 completion — 2026-10-15

Plan [064](064-engine-scoped-agent-guidance.md) is DONE and integrated locally into `main` from `feat/plan-064-engine-guidance`. Windows `just verify` passed on the merge of current main (with plan 067) into the branch, `1b1c0b4`: six stages in 6m25s. The final review built the pre-change and post-change catalogs and found the served MCP schemas byte-identical with descriptions stripped. Evidence is in the plan's verification section. Linux omitted per docs/agents/verification.md; no push, installation or publication.

## Plan 068 completion — 2026-10-15

Plan [068](068-operation-phase-timings-and-diagnostics.md) is DONE and integrated locally into `main` at `9904dfea18ab6b0fd89ac839304d2c26fa3bc998`. Windows `just verify` passed on the resolved merge tree before the merge commit: six stages in 7m41s; UI 181 passed; Core 3,589 passed/11 skipped; MCP 386 passed/4 skipped; build 0 warnings/errors; formatting passed for 34 C# files. Linux was skipped at the operator's direction. No push, installation or publication. See [verification evidence](../docs/verification/plan-068.md).

## Plan 065 completion — 2026-10-15

Plan 065 is DONE and integrated locally into `main` at `cf0e7c4d45c64105677c14563aed910aefe47788`. Post-merge verification passed on that exact revision: Windows `just verify` exit 0 (Core 3,511 passed/11 skipped; MCP 374 passed/4 skipped; format skipped with 0 changed C# files) and Linux `just verify-linux` exit 0 (9 stages; UI 163 tests; Core 3,516 passed/6 skipped; MCP 375 passed/3 skipped; build 0 warnings/errors; format passed for 7 changed C# files). Full stage details: [Plan 065 verification](../docs/verification/plan-065.md). This is local integration evidence; no push, install or publication was performed.
## Completed since the 2026-10-14 snapshot

Plan 067 was subsequently integrated locally into `main` at `0fdd9db78e91698f0641f6ed7f3e5a4db396bb57`. Windows `just verify` passed on the resolved merge tree before the merge commit: Core/CLI 3547 passed/11 skipped, MCP 381 passed/4 skipped, UI 178 passed (36 files), and format passed for 16 C# files; six stages in 5m45s. Logs: `D:/temp/sqlharness-plan067-main-verify`. Linux omitted by operator instruction; no push, installation or publication. The table below preserves earlier feature-branch evidence.

| Plan | Evidence |
|---|---|
| [067](067-cell-limit-parity-and-default.md) | DONE on feature branch `feat/plan-067-cell-limit` at implementation HEAD `3ae4dc5`; final Windows `just verify` passed on that exact revision (UI 163; Core/CLI 3519 passed, 11 skipped; MCP 381 passed, 4 skipped; build 0 warnings/errors; format skipped because no C# files changed). Linux was explicitly waived and remains unverified. No integration, installation or publication. See durable [final completion report](../final-completion-report.md). |

# Plany po audycie SQLHarness 2026-09-28

Zapis: 2026-09-29, skill improve. Baza: `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`.
Źródło: [audyt](2026-09-28-project-audit.md). Użytkownik zlecił zapis wszystkich ustaleń; plany nie są jeszcze wdrożone.

## Aktualna kolejka wykonania — 2026-10-13

Odświeżono na `a296e91`: sprawdzono main, statusy planów i worktree. Preferencja: ogólne i SQL Server przed wyłącznie PostgreSQL. **Każdy plan jest wykonywany w całości**, bez osobno odkładanych faz.

### W wykonaniu

| Plan | Kategoria | Dowód |
|---|---|---|
| [047](047-postgres-temp-target-proof.md) | PostgreSQL: uproszczenie dowodu TEMP dla setupu | DONE 2026-10-14 (branch `feat/plan-047-temp-proof`; dowód w [047-readonly-proof.md](047-readonly-proof.md); oba wiersze Grupy 4 i wiersz kolejki poniżej) |

### Zakończone od poprzedniej kolejki

028, 029, 031, 034, 049–060 są DONE według aktualnego indeksu. Ograniczenia weryfikacji pozostają zapisane w wierszach historycznych. 049 jest już w main (sprawdzono przodka `58fab51`); starsze „not merged” jest nieaktualne. 024/045/046 i ceny modeli również zakończone. Zachowano źródła i ograniczenia dowodów w tabeli historycznej; przy tym odświeżeniu nie uruchamiano bramek.

### Grupa 1 — kontrakt i skuteczność pracy agenta

| Kolejność | Plan | Kategoria | Warunek |
|---|---|---|---|
| 1 | [061](061-unified-parameter-sets.md) | Ogólne / P3: wspólne zestawy parametrów measure/compare | DONE — feature branch gates green; evidence: [061 verification](061-verification-evidence.md) |
| 2 | [065](065-artifact-plan-pages-and-clipping.md) | Ogólne / P1–P2: licznik obcięć i strony planu dopasowane do budżetu | TODO — zaakceptowany zapis planu; zachować 14 odczytów artifact: 6 pierwszych stron + 6 kontynuacji + 2 powtórzenia agenta; implementacja wymaga osobnego zlecenia |

### Grupa 2 — historia optymalizacji i dashboard

| Plan | Kategoria | Warunek |
|---|---|---|
| [062](062-compare-context-dashboard.md) | Ogólne / P2: kontekst compare, historia prób, dashboard | 051 jest DONE. Nie wymaga całej fali 055–061; uzgodnić aktualną bazę po 055 i realizować sekwencyjnie względem zmian MCP w 060/061 |
| [069](069-dashboard-session-curation.md) | Ogólne / P2: porządkowanie sesji w dashboardzie | Po 062; potem plany dla specu C (dopasowanie transkryptów) i B (compare dla reviewera) |
| [070](070-dashboard-agent-transcript-matching.md) | Ogólne / P2: tytuły i model z transkryptów agentów (opt-in) | Po 069 |
| [071](071-dashboard-compare-review.md) | Ogólne / P2: compare dla reviewera (diff SQL, IO per tabela, zmiany planu, Copy) | Po 062; przed startem potwierdzić u operatora CodeMirror 6 |

062 jest odrębnym wyborem produktowym. Nie uruchamiać na podstawie samego numeru planu równolegle ze zmianami wspólnego katalogu MCP i kontraktów compare. Plan obejmuje cały kontekst, journal, API, ekrany i dokumentację.

### Grupa 3 — dostarczanie i utrzymanie

| Kolejność | Plan | Kategoria | Warunek |
|---|---|---|---|
| 2 | [032](032-ci-matrix-and-test-hygiene.md) | CI Windows/Linux i stabilność testów | DONE — lokalnie scalono do main w `d4ea971` (`15c1b9c`, `0fd0958`); zintegrowane bramki Windows i Linux zielone; dowód CI oczekuje na pierwszy push; [weryfikacja](032-verification.md) |
| 3 | [036](036-live-database-ci-lane.md) | Izolowane testy SQL Server + PG | TODO; oba silniki w jednym wykonaniu |

028 jest DONE i lokalnie scalone do main (`8ed77af`); post-merge bramki Windows i Linux zielone, exit 0. Bez push; zależność 032 od 028 jest spełniona.

Pomysł szerszej automatyzacji PR/release/updater pozostaje odłożony zgodnie z decyzją użytkownika do czasu wejścia 028/032; nie jest nowym zleceniem implementacji.

### Grupa 4 — wyłącznie PostgreSQL

| Kolejność | Plan | Kategoria | Warunek |
|---|---|---|---|
| 1 | [031](031-pg-session-hardening.md) | standard_conforming_strings | DONE (branch fix/plan-031-pg-session, fdd788e..2ffe8ae; Windows verify: OK; Krok 4 (playground live) NIEuruchomiony — brak zgody operatora w tej sesji; bez push) |
| 2 | [047](047-postgres-temp-target-proof.md) | Uproszczenie dowodu TEMP dla setupu | DONE (dowód w [047-readonly-proof.md](047-readonly-proof.md); fazy read-only dostarcza 052, więc nie dodano przełącznika sesyjnego; DML bez dowodu nazwy w query i measured SQL, setup DDL i ścieżka zatwierdzonej mutacji zachowują dowód; bramki: Windows verify: OK, Linux verify-linux: OK na f3f7dd2; branch feat/plan-047-temp-proof, `d46a4c9..13e87ad`) |

031 i 047 są DONE. Brak innych oczekujących planów w tej grupie.

033 zastąpiony przez 050, 035 przez 051; 014/037 odrzucone; 048 wycofany. Draft report API nie jest planem implementacji. Nie przywracać usuniętego gain ani historycznych nazw i liczby narzędzi MCP.

## Kolejność i status

| Plan | Pokrycie | Priorytet | Nakład | Zależności | Status |
|---|---|---|---|---|---|
| [001 — Zamknij wyciek SQL i parametrów przez stderr](001-mcp-safe-logging.md) | S1 | P1 | S | — | DONE (branch fix/plan-001-mcp-safe-logging, 48e33e5..4423386; merge/push czeka na decyzję) |
| [002 — Napraw zakres i normalizację ścieżek wejściowych](002-mcp-input-roots.md) | S3, A1; badanie tożsamości pliku | P1 | M | — | DONE (branch fix/plan-002-mcp-input-roots, f343d2b..c241921) |
| [003 — Powiąż dostęp do artefaktów z zakresem MCP](003-mcp-artifact-scope.md) | S2; przestrzeń nazw snapshotów | P1 | L | 001, 002 | DONE (branch fix/plan-003-mcp-artifact-scope, 637a59b..c5ce1ad; merge lokalny do main, push czeka na decyzję) |
| [004 — Obejmij inspect wspólną blokadą operacji DB](004-mcp-inspect-gate.md) | R1 | P1 | S | 001 | DONE (branch fix/plan-004-mcp-inspect-gate, ff5d0b6..bc5758c; merge/push czeka na decyzję) |
| [005 — Ustal przyczyny timeoutów testów MCP](005-mcp-verification.md) | T1 | P2 | M | 001, 004 | DONE (branch fix/plan-005-mcp-verification, 6c41f91..0bc179d; dowód: [005-verification-evidence](005-verification-evidence.md); merge/push czeka na decyzję) |
| [006 — Ogranicz pamięć i czas porównywania wyników](006-equivalence-budget.md) | R2 | P1 | L | 005 | DONE (branch fix/plan-006-equivalence-budget, 6671ba1..0286f5d; merge/push czeka na decyzję) |
| [007 — Zbuduj wspólny preflight query/setup/benchmark](007-mode-aware-validation.md) | A2; funkcja preflight | P2 | L | 005 | DONE (merge lokalny do main 039bb7e; dowód: [007-mode-aware-validation-proof](007-mode-aware-validation-proof.md); filtr 82/0/0, pełny suite bez Integration Core 1962/0/0 + MCP 164/0/4, build -warnaserror 0 warn; push czeka na decyzję) |
| [008 — Udostępnij wersjonowany schemat odpowiedzi MCP](008-mcp-output-schema.md) | A3 | P2 | M | 005, 007 | DONE (branch feat/plan-008-mcp-output-schema, 70a4ca2..b1e1985; dowód: [008-mcp-output-schema-proof](008-mcp-output-schema-proof.md); merge/push czeka na decyzję) |
| [009 — Uczyń granicę statycznego safety widoczną dla klientów](009-safety-contract.md) | A4; świadome ograniczenia funkcji PG | P2 | M | 007, 008 | DONE (main, 0227180..29b3102; dowód: [009-safety-contract-proof](009-safety-contract-proof.md); push czeka na decyzję) |
| [010 — Usuń powielone pętle watch i czyste reguły adapterów](010-shared-runtime-contracts.md) | D1; ocena interfejsów | P3 | L | 004, 007, 008, 009 | DONE (branch fix/plan-010-shared-runtime-contracts, 20a2aaf..f64a3da; dowód: [010-shared-runtime-contracts-proof](010-shared-runtime-contracts-proof.md); merge/push czeka na decyzję) |
| [011 — Zaplanuj i dodaj wąskie rozszerzenia bezpiecznej składni](011-safe-sql-extensions.md) | nadmiarowe blokady SET, table variables, PG TEMP TRUNCATE i ANALYZE | P2 | L | 007, 009 | DONE — kod i dokumentacja kompletne, NIE pełny PASS (branch feat/plan-011-safe-sql-extensions, zakres 0fad2a8.. do commita z końcową wersją dowodu, poprzedza go d9b7479; projekt testów MCP jest niestabilny na czubku brancha i na bazie 0fad2a8 — niezależna awaria gate, właściciel: plan 005; jeden test ProcessRunnerTests w SqlHarness.Tests zawiódł w 2 z 10 przebiegów fali końcowej; T5 ANALYZE DESIGN COMPLETE, nie IMPLEMENTED; dowód: [011-safe-sql-extensions-proof](011-safe-sql-extensions-proof.md); merge/push czeka na decyzję) |
| [012 — Zastąp tekstowy most MCP typowanym modelem parametrów](012-typed-matrix.md) | matrix z przecinkami/pustą wartością; funkcja typowanych wejść | P2 | L | 007, 009 | DONE (merge lokalny do main 1f312a6; branch feat/plan-012-typed-matrix, 3d56a81..da73e14 + final fix wave F1-F10 e906fbd..6760a96; dowód: [012-typed-matrix-proof](012-typed-matrix-proof.md); po merge: build -warnaserror 0 warn, pełny suite bez Integration Core 2811/0/0 + MCP 212/0/4; push czeka na decyzję) |
| [013 — Doprecyzuj i przygotuj wdrożenie decyzji regresji CI](013-regression-policy-design.md) (progi użyte w 051; część offline-reader zastąpiona przez 051, zaimplementowane na `feat/plan-051-regression-verdict`) | propozycja funkcji regresji | P2 | L | 003, 006, 009 | DONE (branch docs/plan-013-regression-policy, 983f7b6..f6c8d7a; dowód: [013-regression-contract](013-regression-contract.md), [013-regression-implementation](013-regression-implementation.md); tylko etap projektowy, funkcja PLANNED; brak suite i live DB; nie scalono; push czeka) |
| [014 — Przygotuj wykonalny kontrakt diagnostyki pg_stat_statements](014-postgres-diagnostics-design.md) (REJECTED 2026-10-11: pgstop nie powstanie) | propozycja diagnostyki PG | P2 | L | 003, 004, 009 | DONE (branch docs/plan-014-postgres-diagnostics, f0934b3..7d82bf1; dowód: [014-pg-statements-contract](014-pg-statements-contract.md), [014-pg-statements-implementation](014-pg-statements-implementation.md); tylko etap projektowy, funkcja pgstop PLANNED, nie zaimplementowana; brak suite i live DB; nie scalono; push czeka) |
| [015 — Zdiagnozuj rozjazd zainstalowanego CLI i repozytorium](015-installation-alignment.md) | obserwacja PATH i nieaktualnego help | P3 | S | 005 | DONE (branch docs/plan-015-installation-alignment, d2a0729..8e00431; dowód: [015-installation-evidence](015-installation-evidence.md); jest follow-up guard procedury; tylko diagnoza; binarka PATH nie zastąpiona; rozjazd zostaje; nie scalono; push czeka) |

Statusy: TODO / IN PROGRESS / DONE / BLOCKED (konkretny powód) / RESOLVED EXTERNALLY (dowód). Dla 013–015 DONE oznacza dokumentację/diagnozę, nie implementację przyszłej funkcji. W011 ANALYZE może zakończyć się projektem zależności parsera — osobno DESIGN COMPLETE, nie IMPLEMENTED.

## Zależności

- 001 i002 niezależne; 003 korzysta z bezpiecznych błędów i granic plików.
- 004 domyka gate DB; 005 diagnozuje jego weryfikację. Historyczny timeout nie blokuje przygotowania pilnej naprawy bezpieczeństwa, ale ograniczenie dowodu musi zostać zapisane.
- 006: testy charakterystyczne przed optymalizacją equivalence.
- 007 ustala preflight, 008 publikuje schema, 009 opisuje granicę safety.
- 010 konsoliduje ustalone kontrakty. 011/012 nie wymagają ukończenia całego refaktoru010.
- 013/014 doprecyzowują istniejące specyfikacje, w tym wykryte sprzeczności, przed pisaniem kodu.
- Nie wykonuj równoległych zmian wspólnych plików w jednym worktree: McpOperationMapper, McpToolCatalog, Contracts, Capabilities.
- Brak zgody na live DB, konfigurację użytkownika, instalację, publikację lub push.

## Mapa wszystkich ustaleń

| Ustalenie | Plan |
|---|---|
| S1 logger | 001 |
| S2 scope artefaktów i snapshotów | 003 |
| S3 case sensitivity, A1 separator | 002 |
| R1 inspect concurrency | 004 |
| R2 pamięć/CPU/cancellation equivalence | 006 |
| A2 usage i funkcja preflight | 007 |
| A3 outputSchema | 008 |
| A4 granica read-only PG | 009 |
| T1 timeouty testów/publish | 005 |
| D1 watch i reguły adapterów | 010 |
| SET scalar, table variables, TEMP TRUNCATE | 011 |
| ANALYZE/parser | 011/T5 |
| Świadome prefiksy PG i projekt strict profile | 009 |
| Matrix przecinki/puste wartości, typowane wejścia | 012 |
| Regresja CI | 013 |
| pg_stat_statements | 014 |
| Stara binarka PATH | 015 |
| Podmiana pliku/tożsamość uchwytu — do zbadania | 002/T3 |
| Zachowanie dobrych granic interfejsów | 010 i ograniczenia wszystkich planów |

## Decyzje zachowane bez naprawy

CLI/MCP, query/measure/compare, param-set/matrix, schema/space/indexes i writery domenowe mają odrębne role. Nie scalać ich mechanicznie. Object Report ma ADR i OperationReportContract. Dwie reprezentacje MCP liczą się do budżetu wire. Validate może poprawnie zakończyć analizę z allowed=false. Brak trwałych mutacji i force snapshot w MCP to granica v1. Historycznych napraw OUTPUT INTO/parametrów PG/buforów nie otwierać bez dowodu regresji.

## Dowody realizacji

Przy zapisie sprawdzono dokumenty i ścieżki. Historyczny wynik audytu nie jest dowodem przyszłej implementacji.

| Plan | Commit | Komendy i wyniki / dokument wynikowy | Braki |
|---|---|---|---|
| 001 | 48e33e5..4423386 (a21c8d1 T1 oracle, 8cc8481 T2 logger, 4423386 T3 piny) | gate McpSecretRedaction/McpStdioProcess/McpStderrLeak: 11 passed, 0 failed, 2 skipped (foreign-RID); build -warnaserror: 0 warn; pełny suite bez Integration: Core 1895/0/0, MCP 129/0/4 (2 foreign-RID + 2 live opt-in); git diff --check: 0; final review: Ready to merge, 0 Critical/Important | Brak live DB (jawny; live testy skip) |
| 002 | f343d2b..c241921 (6749163 T1 testy, f71ed8f T1 fix1, 8654c23 T2 normalizacja, c241921 T3 ocena) | gate McpInputReader/McpStartup: 24 passed, 0 failed (SqlHarness.Tests: 0 dopasowanych); build -warnaserror: 0 warn, 0 error; pełny suite bez Integration: Core 1895/0/0, MCP 136/0/4 (2 foreign-RID + 2 live opt-in); git diff --check: 0; doc: plans/002-file-identity-assessment.md; final review: Ready to merge, 0 Critical/Important | Brak dowodu na case-sensitive FS Linuksa (gałąź Ordinal + test Case_only_sibling nie wykonane na takim FS); brak live DB (plan zabrania); smoke publish-timeout nie zaobserwowany (win-x64 przeszedł; znany baseline plan-005) |
| 003 | 637a59b..c5ce1ad (54e4eba T1 kontrakt, 999b632/1de3b2a/708eff1 T2 RED+fix, 930dfa3/0d02470/2ae6949 T3 owner threading, 45833e1/6b6acf7/913542b T4 snapshot scope, c5ce1ad T5 docs) | gate McpScope/McpMapping/ArtifactReader/SnapshotStore: Mcp 41/41 + Tests 57/57, exit 0; build -warnaserror: 0 warn, 0 error; pełny suite bez Integration: Core 1915/0/0, MCP 147/0/4 (2 foreign-RID + 2 live opt-in); git diff --check: 0; docs: plans/003-scope-contract.md; final review: Ready to merge, 0 Critical/Important | Brak live DB (jawny; live testy skip); flake McpStdioProcessTests pod obciążeniem przeszedł w rerun i pełnym suite (znany baseline plan-005) |
| 004 | ff5d0b6..bc5758c (ff5d0b6 T1 testy RED, 32f0cc2 T2 gate, 9735b1a T3 release, bc5758c T3 docs) | fokus McpLifecycle/McpCancellation: 28 passed, 0 failed; build -warnaserror: 0 warn, 0 error; pełny suite bez Integration: Core 1915/0/0, MCP 154/0/4 (2 foreign-RID + 2 live opt-in); git diff --check: 0; doc: plans/004-inspect-gate-proof.md; final review: Ready to merge, 0 Critical/Important | Brak live DB (jawny; live testy skip); flake Progress pre-existing zielony w tych runach (baseline plan-005); T3 RED-phase zaparkowany rulingiem (RED-run nie dyskryminuje; dowód T1 RED 3/24 + T2 GREEN 24/24) |
| 006 | 6671ba1..0286f5d (9 commits: 6671ba1/3bbc0b3 T1 oracle+charakterystyka, 7f55cde T2 kontrakt, ac38e4c/a1a28e3 T3 dedup+budżet+token, fe55b80 T4 sweep/limit/cancel/alokacje, f069d42/54f86d8 T5 capabilities+matrix, 0286f5d fix byte-pin) | gate ResultEquivalence/Compare/McpCancellation: 68+8 passed, 0 failed; build -warnaserror: 0 warn, 0 error; pełny suite bez Integration: Core 1934/0/0, MCP 154/0/4 (2 foreign-RID + 2 live opt-in, bez timeoutów); git diff --check: 0; doc: plans/006-resource-contract.md; final review: Ready to merge, 0 Critical/Important (3 minory T3 non-blocking, M1/M2 zaparkowane) | Brak live DB (jawny; live testy skip); brak dowodu na innej platformie niż Windows (jak 001–004) |
| 008 | 70a4ca2..b1e1985 (5cc5f84 T1 wiring, 14df2a2 T2 transport, e456af4 T3 budget+race, 837fa11 T4 guard, b1e1985 fix M2/M3/R5-sweep) | gate McpToolSchema/McpProtocol/McpOutput/McpTokenBudget: 48 passed, 0 failed; build -warnaserror: 0 warn, 0 error; pełny suite bez Integration: Core 1962/0/0, MCP 172/0/4 (2 foreign-RID + 2 live opt-in); git diff --check: 0; doc: plans/008-mcp-output-schema-proof.md; final review: With fixes → fix wave → re-review all addressed, 0 Critical/Important | Brak live DB (jawny; live testy skip); R5 ctx-race: containment (McpScopeHome) + tracked remainder (McpStdioProcess seam tests, theoretical) |
| 009 | 0227180..29b3102 (13477be/a58c15c/1cbafab T1 metadane, 993e3c4/84ec9f8/1d3052b T2 opisy, 7493ad9/361280b T3 stałość+denylist, 914238c T4 assessment, 86c3b3e/29b3102 final watch-fix) | gate Capabilities/Validate/McpToolSchema: Tests 73/73 + Mcp 33/33, exit 0; build -warnaserror: 0 warn, 0 error; pełny suite bez Integration: Core 1997/0/0, MCP 181/0/4 (2 foreign-RID + 2 live opt-in); git diff --check: 0; docs: plans/009-strict-profile-assessment.md + plans/009-safety-contract-proof.md; final review: With fixes → fix wave → re-review ADDRESSED, 0 Critical/Important | Brak live DB (jawny; live testy skip); zaparkowana fraza watch/read-only w AGENTS.md (docs-only, follow-up) |
| 012 | 3d56a81..da73e14 (8bf6331..7b6afd4 T1 model+binder, a451014..879526e T2 mapper bez join/split, e57b1fc..30950eb T3 testy, c06e940/da73e14 T4 inwarianty runnera + redakcja) + final fix wave e906fbd..6760a96 (F1-F10 z finalnego review) | gate SqlParameterMatrix/CompareMatrix/McpMapping/McpSecretRedaction: Tests 137/0/0 + Mcp 73/0/0; build -warnaserror: 0 warn, 0 error; pełny suite bez Integration: Core 2811/0/0, MCP 212/0/4 (2 foreign-RID + 2 live opt-in, bez timeoutów w tej sesji, dwa przebiegi MCP); git diff --check: 0; doc: [012-typed-matrix-proof](012-typed-matrix-proof.md) | Brak live DB (jawny; live testy skip); brak dowodu na innej platformie niż Windows; dwa znane niestabilne testy hosta MCP (właściciel plan 005) nie zaobserwowane w tej sesji, ale nie wygaszone; R2/R3 oczekują potwierdzenia użytkownika (zob. dowód) |
| 013 | 983f7b6..f6c8d7a (983f7b6 T1, 14bde0a/16f1094 T2, 837cb54/057bcb0 T3, d5f994f T4, f6c8d7a follow-up) | dowód: [013-regression-contract](013-regression-contract.md), [013-regression-implementation](013-regression-implementation.md); tylko etap projektowy, funkcja PLANNED; suite nieuruchomiony (plan zabrania testów aplikacji przy zapisie dokumentów); git diff --check: 0 | Brak live DB; brak dowodu z innej platformy; nie scalono; push czeka |
| 014 | f0934b3..7d82bf1 (f0934b3 T1, 281a634 T2, 905d336/cac1df8 T3, 7334c17 T4, 6627b0a/08e7526/7d82bf1 T5) | dowód: [014-pg-statements-contract](014-pg-statements-contract.md), [014-pg-statements-implementation](014-pg-statements-implementation.md); tylko etap projektowy, funkcja pgstop PLANNED, nie zaimplementowana; suite nieuruchomiony (plan zabrania testów aplikacji przy zapisie dokumentów); git diff --check: 0 | Brak live DB; brak dowodu z innej platformy; nie scalono; push czeka |
| 015 | d2a0729..8e00431 (d2a0729 T1, 783842e T2, 22c0644 T3, 8e00431 follow-up guard) | dowód: [015-installation-evidence](015-installation-evidence.md); jest follow-up guard procedury; tylko diagnoza; binarka PATH nie zastąpiona; rozjazd zostaje; git diff --check: 0; suite aplikacji nieuruchomiony (plan zabrania testów aplikacji przy zapisie dokumentów) | Brak live DB; brak dowodu z innej platformy; instalacja nie wykonana; nie scalono; push czeka |

---

# Plans after the 2026-10-03 deep audit (016–037)

Generated by the improve skill (`deep`) on 2026-10-03 against commit `5280f78`. The user selected every P1+P2 finding plus the direction items, and asked for the plans in English. Execute them in the order below unless the dependencies say otherwise. Each executor reads its plan fully first, honors its STOP conditions, and updates its row here when done.

Baseline at planning time: the build had 0 warnings and local tests passed (2811 Core, 212 MCP, 4 skipped). **CI on `main` has been red since 2026-09-28**: two tests fail only on Linux, and `dotnet format --verify-no-changes` fails on about 50 files. That is why 016 comes first.

## Execution order & status

| Plan | Title | Priority | Effort | Depends on | Status |
|---|---|---|---|---|---|
| [016](016-restore-green-ci.md) | Main is green again in CI; one local command reproduces the gate | P1 | S | — | DONE (branch fix/plan-016-green-ci; Linux proof pending first CI run) |
| [017](017-tsql-hash-named-cte-bypass.md) | A `#`-named CTE can no longer pass off persistent DML as `#temp` work | P1 | S | 016 | DONE (branch fix/plan-017-hash-cte; Windows verify.ps1 and Linux verify-linux.ps1 OK) |
| [018](018-tsql-external-access-fail-closed.md) | T-SQL external-access and cross-database checks fail closed; parser to TSql180 | P1 | M | 016, 017 | DONE (branch fix/plan-018-tsql-external-access; Windows verify.ps1 and Linux verify-linux.ps1 OK) |
| [019](019-pg-classifier-holes.md) | The PG classifier denies self-named temp CTE writes, SQL-string executors and deny-list gaps | P1 | S | 016 | DONE (branch fix/plan-019-pg-classifier; Windows verify.ps1 and Linux verify-linux.ps1 OK) |
| [020](020-azure-cli-process-hardening.md) | The Azure CLI token fetch no longer runs binaries from the CWD, hangs, or reads MCP stdin | P1 | S | 016 | DONE (branch fix/plan-020-azure-cli-hardening; commits a652835, c04f88d, bd38a2a, 2d07bc4; Windows and Linux gates green) |
| [021](021-unrecognized-statistics-unavailable.md) | Non-English STATISTICS output is reported as unavailable, not as measured zeros | P1 | M | 016 | DONE (branch fix/plan-021-unrecognized-statistics; Windows scripts/verify.ps1 OK) |
| [022](022-dotnet10-and-npgsql-migration.md) | .NET 10 LTS plus a supported Npgsql before the 2026-11-10 end of support | P1 | M | 016 | DONE (branch chore/plan-022-net10; SDK 10.0.303; Npgsql 10.0.3; scripts/verify.ps1 OK; scripts/verify-linux.ps1 OK; live PG not run). Stored snapshots that contain cidr values need a one-time recapture because Npgsql 10 returns IPNetwork. |
| [023](023-tsql-reject-multi-batch-go.md) | `GO`-separated T-SQL is rejected instead of misclassified | P2 | S | 016, after 017/018 | DONE (branch fix/plan-023-go-batches; Windows verify.ps1 and Linux verify-linux.ps1 OK) |
| [024](024-canonical-results-common-types.md) | Arrays/inet/NaN/spatial/hierarchyid results no longer fail whole operations | P2 | M | 016 (coordinate with 022) | DONE — Phase A (`8b942d9`) and Phase B (`3d097ff`) complete; whole-branch snapshot-shape fix (`0b9f659`); final-fix Windows `verify: OK` and Linux `verify-linux: OK`; scoped re-review addressed the finding, with one accepted Minor for branch-local dimensionless array snapshots; no live SQL Server/PostgreSQL reads exercised. Evidence: [durable proof](024-canonical-results-proof.md) |
| [025](025-redaction-consistency.md) | Redact credentials (password value) in every operation; stop redacting caller-supplied SQL, parameters, vars and user (supersedes 038/W3 threshold) | P2 | M | — | DONE (branch fix/plan-025-redaction; Windows verify.ps1 OK; Linux verify-linux.ps1 pending) |
| [026](026-watch-condition-matching.md) | `watch --until` matches bit/bool, quoted strings and exponents; keeps polling while no row exists | P2 | S | 016 | DONE (branch fix/plan-026-watch-condition; docs eb53de4, formatting fix d7d7606; Windows and Linux gates passed) |
| [027](027-gain-store-robustness-and-mcp-gain.md) | Gain never fails a successful command; tolerant reads; MCP records gain | P2 | S–M | 016 | DONE (branch fix/plan-027-gain; plan 040 already recorded one MCP emission per call and kept accounting failures off the tool result; the journal replaced gain.jsonl; f8d1583 counts unknown operations in the total and skips unreadable rows; Windows verify.ps1 and Linux verify-linux.ps1 OK) |
| [028](028-release-pipeline-hardening.md) | Releases are versioned, gated on CI and least-privilege | P2 | S | 016 | DONE — locally merged main `8ed77af` (2026-10-13); post-merge Windows `verify: OK` exit 0 (Core 3803, MCP 400/4 skipped), WSL ext4 Linux `verify-linux: OK` exit 0 (Core 3803, MCP 401/3 skipped); fixture reconciled with 055 at 7566 bytes within 8192; Luna xhigh task/conflict review and Sol low whole review clean; no push, tags or live DB; hosted tag proof awaits operator |
| [029](029-agent-docs-and-capabilities-drift.md) | Skill/README/AGENTS/capabilities match the shipped CLI; a sync test guards them | P2 | S | 016 (after 021/023/024/026/027 if scheduled) | DONE (branch `docs/plan-029-agent-docs`, 1cbdc14..5c07092; merged locally with Plans 051–053 at `5a1eb2a`. Original gates: `verify: OK` Core 3826, MCP 385/4 skipped; `verify-linux: OK` 3826. User decisions 2026-10-12: `capabilities [profile] --json` scoped to the profile's engine (contract v2), `dashboard` not advertised, docs written with the writing-for-agents rules; request-scope `--allow-profile` approves profile switches only. Follow-ups: SKILL parameter-set section → pointer after 053; `indexes` on PostgreSQL should reject offline) |
| [030](030-datetime-params-and-pg-plan-depth.md) | Date/time params never shift silently; deep PG plans parse | P2 | S | 016 (after 022 if both) | DONE (A: `8feaf14` and docs follow-up `54224ff`; B: PostgreSQL UTC binding `48ed3a1` and deep-plan JSON `bb78c87`; Windows `verify.ps1` and isolated ext4 Linux `verify-linux.ps1` exited 0 at `bb78c87`—Core 3585 each, MCP 333 Windows/334 Linux with 4/3 skipped; no live PostgreSQL tests) |
| [031](031-pg-session-hardening.md) | PG refuses non-standard string lexing; SCRAM only on unauthenticated transports (second half dropped 2026-10-11) | P2 | S–M | 019 (and 022) | DONE (branch fix/plan-031-pg-session, fdd788e..2ffe8ae; Step 1 at `72d10b8`: `PostgresSessionScope.EnsureStandardStrings` runs before every classified command and throws `SqlHarnessSafetyException` (exit 2) unless the server reports `standard_conforming_strings` as `on` (any casing), refusing a missing parameter too; 6 offline test cases (4 methods, one a 3-case theory). Step 3 records the rule in the transport policy spec, README PostgreSQL engine notes, the SKILL digest and the capabilities engine summary; AGENTS.md deliberately untouched — it no longer hosts the PostgreSQL engine notes. Step 2 dropped 2026-10-11. Step 4 (opt-in playground live check) NOT run — no operator approval this session, so no live PostgreSQL run; Windows `verify: OK`; not pushed) |
| [032](032-ci-matrix-and-test-hygiene.md) | CI on Windows and Linux; honest skips; no env races; no publish leak; Pester runs | P2 | M | 016, 028 | DONE; followup merged locally into main in `d4ea971`; integrated Windows and Linux gates passed. Original implementation merged locally into main in `0cb5038` from `ci/plan-032-complete` (`846f4fa..f241496`). Original Steps 1-4: `WindowsFact`/`UnixFact` skips replace silent returns, unique per-test PG password variables, best-effort publish temp cleanup, CI OS matrix + `concurrency` + `timeout-minutes` + trx upload + Windows `pwsh` Pester step, `.gitattributes`. Prior platform gates were green on pre-followup `f241496`: Windows `verify: OK` (Core 3794/11 skipped, MCP 400/4 skipped), Linux `verify-linux: OK` (3801/4 skipped), Pester 21/21, GateParity 28/28, `git add --renormalize .` empty. Followup branch `fix/plan-032-env-followup` (`15c1b9c`, `0fd0958`) changes shared Build/Reject env names in `PostgresConnectionStringTests`; focused repeats passed (3 x 621). The followup Windows full gate then failed at ProcessRunnerTests cleanup deleting locked child.stderr.log (SqlHarness.Tests 3909 passed/1 failed/11 skipped; MCP 403 passed/4 skipped); the log does not identify the handle owner or show a surviving PID. Test-only bounded cleanup retry and two lock regressions pass ProcessRunnerTests 8/8. Fresh gates on `0fd0958`: Windows `verify: OK` (UI 144, Core 3912/11 skipped, MCP 403/4 skipped), Linux `verify-linux: OK` (UI 144, Core 3917/6 skipped, MCP 404/3 skipped). Original final whole-branch review approved to merge. Task and whole-branch Luna xhigh followup reviews found no major findings; one minor generic `IOException` retry ruling retained. Integrated Windows gate on the pre-merge-commit source tree: `verify: OK`, exit 0 (UI 144, Core 3413/11 skipped, MCP 358/4 skipped); integrated Linux gate on `d4ea971`: `verify-linux: OK`, exit 0 (UI 144, Core 3418/6 skipped, MCP 359/3 skipped). CI proof pending first push; not pushed. [Verification](032-verification.md) |
| [033](033-sqlserver-target-identity.md) | Explicit SQL Server identity for aliases/listeners; preserve DNS labels and exact database confirmation | P2 | M | 016 | REJECTED (2026-10-11: server-name check cannot catch a wrong profile; superseded by 050) |
| [034](034-private-sqlharness-home.md) | Private creation before sensitive writes; current journal DB/WAL/SHM and artifact lifecycle | P2 | M | 016 (existing Linux gate provides proof; coordinate 032) | DONE (branch fix/plan-034-private-home, 0d30771; one 0700 home via `HomeDirectory.EnsureFor`, wired at all `CreateDirectory` sites in Core and Dashboard; doctor warns on a broad home; Unix-only tests execute on the Linux gate; Windows `verify: OK`, Linux `verify-linux: OK` on 0d30771 — 3739 Tests + 384 MCP passed, 3 live opt-in skipped, `dotnet format` clean; no live DB checks; not pushed) |
| [035](035-implement-regress-decider.md) | Offline latency regression decision; explicit counts first, optional cost diagnostics | P2 | M–L | 016; 046 before execution | SUPERSEDED by 051 (2026-10-11: verdict computed inside compare; implemented on `feat/plan-051-regression-verdict`) |
| [036](036-live-database-ci-lane.md) | Direction: opt-in CI lane against throwaway PG/SQL Server containers | P2 | M | 016 (028 for pins) | TODO |
| [037](037-rescope-pgstop-current-pg-versions.md) | PG17/18 capability fixtures and source evidence; versions do not gate compatible runtime shapes | P2 | S–M | Revised 014 contract | REJECTED (2026-10-11: pgstop not built; query reads pg_stat_statements) |
| [038](038-agent-session-friction-fixes.md) | Live-session friction: one-item/matrix/artifact truncation, setup `#temp` scope, variable diagnostics/batch guard, redaction, input-path hint, per-statement metrics, token guard, XML false positives | P1–P3 | S–M per item | — | DONE (merged locally; Windows and Linux verify gates green after MCP host log synchronization) |
| [039](039-busy-result-hint.md) | BUSY rejection carries a hint naming the running tool; gate stays non-blocking | P2 | S | — | SUPERSEDED by 060 (the busy gate is removed; MCP calls run concurrently). Was DONE on branch fix/plan-039-busy-hint |
| [040](040-profile-dimensions-ui.md) | Dynamic profile dimensions, target matrix, and MCP emitted estimates | P2 | L | — | DONE (implementation complete; Windows and Linux verification confirmed by the user on 2026-10-10) |
| [041](041-query-projection-preserve-shape.md) | Oversized query projections preserve useful result-set and row/schema structure | P2 | M | 038/W1 | DONE (merged locally 498c3dd from feat/plan-041-query-projection; commits 5370a2a, beb1cfe, bb025b6, 75e2477; Windows and Linux gates green on merged main) |
| [042](042-mcp-plan-diagnostic-text.md) | Useful bounded MCP plan statement text and predicates, with an explicit metadata mode | P2 | M | 043 | DONE (merged locally to main 3f425ef from feat/plan-042-mcp-plan-text; Windows `verify: OK` (3619 + 382 passed) and Linux `verify-linux: OK` on merged 3f425ef; branch 8a088fb..1938f5f: 82ad046 includeText + handler-owned policy, 8676769 docs, 1938f5f final-review fix: MCP-local credential-column literal rule, e.g. `[Password]=N'x'`); Windows verify.ps1 `verify: OK` (Mcp.Tests 382 passed/4 skipped, SqlHarness.Tests 3611 passed) and Linux verify-linux.ps1 `verify-linux: OK` on 1938f5f; an earlier Windows run failed only McpStderrLeakRegressionTests(2026-07-28) handshake flake, which passed 4/4 alone; git diff --check: 0; no live DB checks; known gap: literals behind CONVERT_IMPLICIT/`::text` wrappers, IN lists and `[PasswordHash]` are not masked (follow-up: [048](048-plan-text-credential-column-coverage.md)); not pushed) |
| [043](043-credential-only-pattern-redaction.md) | Credential-only patterns preserve server/database/user/path diagnostics | P2 | S | 025 (DONE) | DONE (fast-forwarded locally to main ab0e137 (+ ddf6285) from feat/plan-043-credential-redaction; commits bcea9d8, 2901126, 4b62150, 42baa78, ab0e137; Windows verify.ps1 `verify: OK` and Linux verify-linux.ps1 `verify-linux: OK` on ab0e137 (Linux after removing a corrupt gate-clone node_modules); build -warnaserror clean after merge; focused SecretRedactor/CredentialRedaction/McpSecretRedaction tests ran on both gates; no live DB checks; unterminated quoted secret-field values fixed in ddf6285 (Windows and Linux gates green, fast-forwarded); not pushed) |
| [044](044-mcp-input-root-discovery.md) | MCP callers discover the actual configured file-input roots | P2 | S | — | DONE (merged locally 8a088fb from feat/plan-044-input-root-discovery; commits ba00213, 64c82dc, 2be2f67, 48a85f3, 542173b, f8f1393; branch: `verify: OK` and `verify-linux: OK`; merged main: `verify: OK`, Linux gate on merged main stopped by host memory pressure, not re-run) |
| [045](045-scalar-set-in-benchmark-setup.md) | Proven same-batch scalar SET works in SQL Server benchmark setup | P2 | S | — | DONE (implementation merged to main `3e8236f`; branch `eb64089`: Windows exit 0, `verify: OK`, Core 3705 passed, MCP 390 passed/4 skipped; Linux WSL ext4 exit 0, `verify-linux: OK`, Core 3705 passed, MCP 391 passed/3 skipped; task and whole-branch reviews approved; no live DB checks; gates do not cover later main changes) |
| [046](046-result-nullability-diagnostics.md) | Nullability diagnostics are separate from observed data equivalence; optional strict comparison | P2 | M | — | DONE (merged locally to main fdfc072 from feat/plan-046-nullability (3f425ef..66e70b6: 45bd63a core, 878bcd7 CLI/MCP/journal, 7a4fab1 docs, d4259ab/7500da7/5d9b5de final-review fixes incl. provider-unknown nullability → unavailable, 66e70b6 format); Windows verify.ps1 `verify: OK` (Mcp.Tests 384 passed/4 skipped, SqlHarness.Tests 3678 passed) and Linux verify-linux.ps1 `verify-linux: OK` (388 total/385 passed/3 skipped; 3678 passed) on 66e70b6; git diff --check: 0; snapshots unchanged; no live DB checks; not pushed) |
| [047](047-postgres-temp-target-proof.md) | Explicit pg_temp write targets and a bounded proof before simplifying TRUNCATE | P2 | M | Live proof required for TRUNCATE relaxation | DONE (narrowed 2026-10-11; proof in [047-readonly-proof.md](047-readonly-proof.md) — 052's per-execution `SET TRANSACTION READ ONLY` already delivers the read-only phases, so no session-level switch was added; an unqualified DML target in query and measured SQL needs no name proof, while setup DDL and the approved mutation path keep it; proved live by opt-in `PostgresTempTargetProofIntegrationTests`; Windows verify: OK and Linux verify-linux: OK at f3f7dd2; branch `feat/plan-047-temp-proof`, commits `d46a4c9..13e87ad`; docs shortened to the new rule) |
| [048](048-plan-text-credential-column-coverage.md) | MCP plan text masks credential-column literals behind wrappers, PG operators, lists, reversed order and compound names | P2 | S | 042 (DONE) | REVERTED (merged a74b485, reverted cadde67: caller already holds the plan file; follow-up 049) |
| [049](049-plan-text-without-literal-masking.md) | MCP plan text is returned as supplied, within size bounds; no literal masking | P2 | S | 048 reverted | DONE (feature branch `feat/plan-049-plan-text`; commits `932d4ad`, `c12aeb4`, `58fab51`; Windows `verify: OK` Core 3722, MCP 383 passed/4 skipped; Linux `verify-linux: OK` Core 3722, MCP 384 passed/3 skipped on WSL ext4; task, whole-branch, fixture and formatting reviews clean; no live DB checks; merged into current main (ancestry checked 2026-10-13)) |
| [050](050-sqlserver-server-name-informational.md) | SQL Server and PostgreSQL report connected server identity without blocking | P2 | M | — | DONE (commits e6b7d01, 1ecc7d6, e87882a, 7f6f1d0; locally merged in 761f2ab; post-merge Windows `verify: OK` Core 3713, MCP 383/4 skipped; Linux `verify-linux: OK` Core 3713, MCP 384/3 skipped; no push/live DB proof) |
| [051](051-compare-regression-verdict.md) | `compare` reports a latency regression verdict from its own samples | P2 | M | 046 | DONE (merged to main `e05da9b` from `feat/plan-051-regression-verdict`; `regression` verdict in compare JSON, summary, matrix cells, MCP and CLI text; supersedes 035 and the offline-reader part of 013; Windows and Linux gates green, confirmed by the user 2026-10-12) |
| [052](052-postgres-read-only-transactions.md) | Every non-approved PostgreSQL command uses a per-command READ ONLY transaction | P1 | M | Disposable PostgreSQL 18.6 proof | DONE at frozen `c07f001`; integrated tip `9082149` (Windows and isolated Linux gates passed; see [052-verification](052-verification.md)); locally merged to main by fast-forward at `f09a614`; not pushed |
| [053](053-specific-errors-for-the-caller.md) | Errors name what went wrong when the reader already holds the data | P2 | M | — | DONE — locally merged to main `5a1eb2a`; proof: [053-verification](053-verification.md); Windows/Linux gates passed; not pushed |
| [054](054-simplify-redaction-and-mcp-input-guards.md) | Redaction is exact known-secret replacement; MCP input guards match a same-user threat model | P2 | M | 049 (DONE) | DONE (local merge main 6ff68d3, journal docs fix f5aef96; post-merge Windows verify: OK Core3787/MCP389+4 skipped; Linux verify-linux: OK Core3787/MCP390+3 skipped; duplicate JSON guard retained for proven SDK differential; no push/live DB) |
| [055](055-agent-output-budget-and-envelope.md) | Text-only budgeted output, inventory-first matrix cases, honest query value types and clipping metadata | P1 | M | — | DONE — locally merged to main `4bb783a`; implementation `52bdc23..f5ac90c`; Windows/Linux gates passed, whole-branch re-review clean; see [055 verification](055-verification.md); no post-merge gates after docs/plans-only main drift; no push/live DB |
| [056](056-parameter-contract-and-dry-run.md) | Parameter normalization, binding parity and dry runs of exact operation arguments | P1 | M | 055 | DONE — locally merged to main `1bfac9e`; implementation `8275304..8e50fb5`, Task 5 `f3d48ff..6e8232e`; task, whole-branch and scoped integration reviews complete; post-merge Windows/Linux gates green (UI 148, Core 3844, MCP 400/4 skipped on Windows and 401/3 skipped on Linux); no push or live DB; see [056 verification](056-verification.md) |
| [057](057-tsql-control-flow-classification.md) | Recursive IF/BEGIN classification under existing statement rules | P2 | S | 056 | DONE; locally merged `9c6b663`; final fix `e05d44d`; Sol low whole-review's 3 Important findings fixed in one Luna high wave; Luna xhigh re-review clean; post-merge Windows/Linux gates green; no push/live DB; see [057 verification](057-verification.md) |
| [058](058-benchmark-plan-evidence.md) | Stored-plan reader, repetition identity, node/operator continuation and matrix values | P1 | M | 055 | DONE — implementation and final-review correction through `6fa9c7f`; Windows/Linux gates pass; [verification](058-verification.md) |
| [059](059-journal-workflow-cost.md) | Explicit producer references and honest measured/estimated workflow cost | P2 | M | 055, 056, 058 | DONE — final correction `233207c`; Windows/Linux gates green; [verification](059-verification.md); no live database calls or push |
| [060](060-mcp-tool-surface.md) | Eight MCP tools with CLI/MCP parity, `--dry-run` on the CLI, compact runtime capabilities and concurrent calls | P2 | L | 055, 056, 059 | DONE — locally merged to main; Windows `verify: OK` after merging main (031): UI 144, Core 3411/11 skipped, MCP 358/4 skipped; Linux gate accepted by the operator (run reaped by the low-memory reaper; an earlier run was disturbed by a concurrent 047 gate in the shared WSL clone); no push. Revised 2026-10-13 (operator): CLI/MCP parity and no recipes; `inspect`/`validate` left MCP and `ping`/`schema`/`counts`/`space`/`qstop`/`indexes`/`validate` left the CLI; busy gate removed; capabilities without `includeDetails`; spec decisions 12 and 15 amended |
| [061](061-unified-parameter-sets.md) | Shared measure/compare parameter-set inputs | P3 | L | 056, 058, 060 | DONE on feature branch `feat/plan-061-parameter-sets`; implementation at `84ff1f3`, docs `04537bf`, formatting fix `c844064`, integrated with main `a9a835b` at `51fab48`; Windows gate pass (UI 144, Core 3457/11 skipped, MCP 364/4 skipped), Linux gate pass (UI 144, Core 3462/6 skipped, MCP 365/3 skipped); build 0 warnings/errors; see [verification evidence](061-verification-evidence.md). Feature-to-main local merge awaits controller final review; no push or live DB. |
| [062](062-compare-context-dashboard.md) | Compare context, SQL/scope/attempt history and comparison dashboard | P2 | L | 051 (DONE); coordinate with 060/061 | DONE — locally merged to main in `818f1e4`; Windows/Linux feature gates green on `3ca11c8`; identical product tree after merge, operator waived repeat gates for docs-only main drift; T7 Luna xhigh and whole-branch Sol low approved, two deferred Minor; no push; see [verification evidence](062-verification-evidence.md) |
| [063](063-missing-index-overlap.md) | Missing-index hints in the saved `plan` artifact section carry an overlap verdict against the table's existing indexes (inventory captured after measurement) | P2 | M | 060 | TODO — planned 2026-10-14 at `472d5b7` |
| [064](064-engine-scoped-agent-guidance.md) | Profile/process-scoped engine guidance in CLI capabilities and MCP descriptions; neutral help and concise operator skills | P2 | M | Current operator-skill/description changes; coordinate shared capabilities/catalog files | TODO — planned 2026-10-14 at `8a08859`; separate-session implementation |
| [065](065-artifact-plan-pages-and-clipping.md) | Accurate clipping counts and budget-aware saved-plan pages; preserve artifact-read cost | P1–P2 | M | 055/058 DONE; coordinate projection drift | TODO — planning accepted 2026-10-14 at `ed44649`; no implementation/publication authorization; observed 14 artifact reads retained |
| [066](066-regression-resolution-floor-and-cost-thresholds.md) | Regression policy v2: 2 ms elapsed spread floor; documented reads/CPU diagnostic thresholds | P2 | S | 051 DONE; coordinate 064 descriptions | DONE — merged locally to main (`36f2213`..`aba5e34`, incl. dashboard `(policy vN)` label); Windows `verify: OK` on `aba5e34`; Linux gate waived by operator (shared-clone interference); no push/live DB |
| [067](067-cell-limit-parity-and-default.md) | MCP `maxCellChars` 0..4096 with CLI parity; shared default 2048 | P2 | S–M | 060 DONE; coordinate 064/065 | TODO — planning accepted 2026-10-14 at `ed44649`; no implementation/publication authorization |
| [068](068-operation-phase-timings-and-diagnostics.md) | Journal phase timings (connect/setup/execute/artifact) and opt-in `diagnostics.enabled` connection events | P2 | M–L | Coordinate 062 (operation page, journal reader) | TODO — planning accepted 2026-10-14 at `ed44649`; no implementation/publication authorization |
| [069](069-dashboard-session-curation.md) | Dashboard session curation: title, note, star/pin, archive, search (journal `session_annotations`, guarded PATCH) | P2 | M | 062 DONE first; coordinate journal schema version with 062/068 | DONE — locally merged to main from `feat/plan-069-session-curation` (implementation `563e484`..`dd2b2de`); journal v9 `session_annotations`; per-task reviews, whole-branch review and one fix wave clean; Windows `verify: OK stages=6` on `dd2b2de`; Linux gate waived by the operator; no push/live DB |
| [070](070-dashboard-agent-transcript-matching.md) | Opt-in agent transcript matching: Claude/Codex conversation titles, Claude model/effort per operation (journal v11 derived tables, housekeeping cycle) | P2 | L | 062 and 069 DONE first; re-verify transcript formats | DONE — feature branch `feat/plan-070-agent-transcripts`, implementation range `54dc621..64bbec5`, passed its Windows gate (Core 3,598/11 skipped; MCP 381/4 skipped) and branch formatter (25 C# files); Sol low whole-branch review passed with two minor coverage gaps. Locally integrated at `cebef32d52678e16bd1e6e54b5ec9ba4f8511dd6`; integrated Windows `just verify` passed, six stages (UI 189; Core 3,677/11 skipped; MCP 386/4 skipped; formatter 27 C# files). Integration preserved Plan 068 schema v10 and moved transcript tables to v11; Plan 075 lifecycle routes remain. Sol's integration review approved after scoped fixes. Linux gate remains waived and unrun. See [verification evidence](070-verification-evidence.md). |
| [071](071-dashboard-compare-review.md) | Compare review: provider-neutral Monaco SQL tabs + side-by-side/unified diff, per-table IO and plan changes, Copy Markdown for PRs, session version diff | P2 | M–L | 062 DONE; editor decision recorded before implementation | DONE — feature branch `plan-071`, implementation `47a303d..218f39e`; Windows `just verify` passed six stages; UI 231 tests; synthetic browser fixture 18/18; Linux omitted per repository verification policy. No integration or push at feature completion; local integration is recorded separately. |
| [074](074-session-search-case-folding.md) | Session search ignores letter case beyond ASCII via `sqlh_fold` (NFC + invariant lower case); accent-sensitive | P3 | S | 069 DONE | DONE — locally merged into `main` at `a093b8df` (feature `de721be`..`9b5cbd39`); integrated Windows `just verify` exit 0, `verify: OK  stages=6  5m03s`; independent review PASS, no blockers. Linux interrupted and waived explicitly by the operator on 2026-10-15. |

Status values: TODO | IN PROGRESS | DONE | BLOCKED (with one-line reason) | REJECTED, SUPERSEDED or REVERTED (with one-line rationale).

## Dependency notes

- **016 first.** Every later plan's done criteria run `scripts/verify.ps1`, which 016 creates, and need a green gate.
- **017 → 018 → 023** all edit `src/SqlHarness.Core/SqlSafety.cs`. Run them sequentially in that order, never in parallel worktrees.
- **018 Step 5** adds the guard test that forces every future ScriptDom upgrade to classify new node types. The switch to `TSql180Parser` (018 Step 6) comes after that guard.
- **019 → 031**: 031's `standard_conforming_strings` check assumes `set_config` cannot be reached through `query_to_xml` (019).
- **022** changes the TFM and Npgsql. If 024, 030 or 031 land after it, re-run their PG tests. 022 STOPs if Npgsql changes `date`/`time` result mappings, because that changes snapshot hashes.
- **028 before 032**: both edit `ci.yml` (028 adds `workflow_call` and permissions; 032 adds the OS matrix).
- **029 last among the docs-touching plans**: its `DocsSyncTests` then enforces documentation for any new command.
- **051** (which supersedes 035) executes after 046 as one plan.
- **036** provides the live proof that 017, 019, 021, 022 and 024 list as pending; 047's READ ONLY proof has since been run and recorded (2026-10-14) on a disposable PostgreSQL 18.6 cluster in this lane, not the playground — see [047-readonly-proof.md](047-readonly-proof.md).

## Policy decisions needed (not planned yet)

- **Locking reads.** T-SQL `SELECT … WITH (TABLOCKX|XLOCK|UPDLOCK, HOLDLOCK)` and PostgreSQL `SELECT … FOR UPDATE/NO KEY UPDATE/SHARE/KEY SHARE` are classified read-only. They change no data, but they can block writers on production for up to timeout × repeats, and PG row locks write tuple headers and WAL. Options: deny exclusive/row-lock hints, route them to the mutation-approval path, or keep and document them. Evidence: `SqlSafety.cs:399-401`, `PostgresSafetyClassifier.cs:354-361`, `PostgresBenchmark.cs:88-96`.
- **Missing-index hint overlap** is now planned as [063](063-missing-index-overlap.md).
- **Equivalence and nullability.** Scheduled as [046](046-result-nullability-diagnostics.md): separate nullability diagnostics from default observed data equivalence and expose an explicit strict option. Implemented (046 DONE): `--compare-nullability` / `compareNullability`.

## Direction verdict recorded

- **Plan 08 (Windows approval tray for MCP mutations) is deferred, not rejected.** It is multi-week and Windows-only, adds a new trust surface, and its own spec admits weak isolation against same-user processes. The CLI already has an approved mutation path. Revisit after 051/036. If resumed, start with a spike of the platform-neutral approval state machine before any UI.

## Findings considered and rejected (2026-10-03)

- xunit v2 → v3 now: low value before 022.
- Splitting `Contracts.cs`, removing the legacy text-parameter adapters: cosmetic, and the adapters are kept for characterization tests (012/final F3).
- Micro-costs (`SessionMessageBuffer` `RemoveAt(0)`, per-call secret sorting, per-resolve compiled regex in `TargetResolver`): not worth doing without a profile showing them.
- PG sidecar warming the cache for the next repetition: document it if it ever matters; total reads are unaffected.
- `Microsoft.SqlServer.Types` native spatial DLL on Linux/macOS: LOW confidence and already documented; at most a misleading exit code.
- `--password-env-var` accepting any variable name on `--unsafe-direct`: a CLI agent with a shell can read env vars anyway, and MCP has no direct path.
- Missing `packages.lock.json`: direct versions are exact-pinned centrally.
- **Not planned by user choice (P3, available for a later run):** double row hashing and repeated plan/SQL parsing (perf); extracting the duplicated single-SQL pipeline from `SqlHarnessModule` and adding one command-descriptor table (tech debt); playground script hardening (password on the `docker exec` argv, 0.0.0.0 port binding, unpinned downloads); index overlap ignoring clustered coverage (moot: 060 removed `indexes`); `PlanDistiller` rejecting plan-less `StmtSimple`; missing `OperationFailureMapper`/JWT/`PlanIdentity` tests; CLI `qstop`/`indexes` on PG refusing only after connect (moot: 060 removed both); the STATISTICS TIME double count for `sp_executesql` (needs a live check).

## Critical policy review, 2026-10-11

Planning base: `729d88f`. The user requested plans/corrections for all findings from the critical review. Application code is unchanged by this planning pass. Completed plans 025/038 remain historical DONE; follow-ups have separate numbers. Revisions below supersede conflicting earlier planning instructions for the listed work.

| Review finding | Executable owner | Decision |
|---|---|---|
| 1: MCP removes statement text and predicates | 042, after 043 | Caller-supplied plans have bounded diagnostic text by default and an explicit metadata-only option; raw artifact access is separate |
| 2: remaining server/database/user masking | 043 | Protect credentials; preserve useful nonsecret diagnostics |
| 3: hidden input roots and az executable path | 044; revised 020 | Expose the actual enforced file boundary and filtered subprocess diagnostics |
| 4: local scalar SET rejected in setup | 045 | Reuse the proven same-batch scalar assignment rule |
| 5: regression pass despite stable latency regression | Revised 013 contract/design and 035 | Time decides latency independently; reads/CPU provide optional diagnostics |
| 6: delta that never returns differences | Revised 014 contract/design | Initial pgstop is cumulative ranking; observed delta is explicitly deferred |
| 7: PG server/extension version allowlist | Revised 014 and 037 | Runtime required-column/type capabilities decide support; versions guide fixtures/live coverage |
| 8: first DNS label used as server identity | Revised 033 | Preserve domains; aliases/listeners use explicit expectedServerName |
| Minor: nullability versus data equivalence | 046; coordinated 013/035 | Separate diagnostics and an explicit strict comparison option |
| Minor: inconsistent TEMP/TRUNCATE proof and search_path | 047 | Qualification addresses target ambiguity; do not relax uncertain TRUNCATE semantics without proof |
| Minor: documentation guard checks backticks | Revised 029 | Verify real command/option/examples contracts and concise skill pointers |
| Minor: private-home plan describes removed gain store | Revised 034 | Cover the actual journal and sensitive artifact creation paths |
| Minor: mechanical drift STOP and unnecessary repeated full gates | Revised pending plan headers and 030/032 | Inspect and adapt drift; use focused repeats for an actual flake and final Windows/Linux gates |

Current execution order is in **Aktualna kolejka wykonania — 2026-10-13** above. Execute whole plans; no separately deferred A/B scopes. 060 and 061 are DONE, as recorded in the status table below. Preserve actual dependencies of 056–061 and 028 → 032. Plan 062 remains a separate product choice; account for the completed 060/061 MCP and compare contract changes when selecting its base and execution order. Do not infer a dependency solely from plan numbering.

For pending execution plans, historical snippets and tests are evidence to inspect. A changed file alone is not a reason to stop or request permission. Keep the approved intent and update obsolete details; stop for an incompatible contract, missing required proof or actions beyond authorization. Run the final Windows and Linux gates after relevant code changes. Repeat complete gates only for a concrete remaining risk. Documentation-only revisions use diff/link/consistency checks; they do not require application builds or live database calls.

## Critical review 2, 2026-10-11 (evening)

Question applied to every rule: who is the adversary, and do they have another path to the same data? Protection the caller can bypass with `query` or by reading their own file is removed or reframed as an output-size bound.

| Finding | Decision | Owner |
|---|---|---|
| Plan-text literal masking hides the caller's own file | 048 reverted; remaining 042 rule removed | 049 |
| SQL Server server-name comparison cannot catch a wrong profile; false positives for FQDN/listeners/Azure | Report the name, keep the database check; TLS is the identity check | 050 (033 rejected) |
| Offline `regress` needs run counts, spread and legacy proofs that compare already has in memory | Verdict computed inside compare | 051 (035 superseded) |
| `pgstop` duplicates a `query` over `pg_stat_statements` | Rejected; example query in docs | 029 (014, 037 rejected) |
| Per-file private modes repeat at every writer | One 0700 home | 034 revised |
| Name-based TEMP proof and its `search_path` hole | Test server-enforced READ ONLY first | 047 revised |
| SCRAM-only on transports that already expose data | Dropped; recommend `verify-full` | 031 revised |
| SHA pinning, Dependabot, provenance for a single-maintainer tool | Dropped | 028 revised |
| `qstop` SQL text kept out of stdout; `snapshot --diff` hides cells | Kept as output-size bounds, no longer described as protection | AGENTS.md rewrite |

## Rule review 3, 2026-10-11 (subagent, read-only)

Framing: in CLI mode the agent shares the shell's environment, so closed profiles, mutation flags and credential hiding prevent accidents; a least-privilege database principal is the enforcement. MCP without a shell is the case where they are a boundary.

| Finding | Decision | Owner |
|---|---|---|
| PG "read-only" runs any non-denylisted function (`SELECT app.purge()`, `pg_stat_statements_reset()`), also over MCP | Per-command READ ONLY transaction, not a session default | 052 (P1) |
| PG endpoint identity: DNS hosts always fail on `require`; reflection into Npgsql internals on `verify-full` | Informational, like SQL Server | 050 extended |
| Parameter values, param-set paths, profile var names and MCP stderr text hidden from their author | Specific errors; BOM/comments/trailing commas accepted; journal keeps redacted error text by default | 053 |
| Credential field patterns, input-root reparse/ADS/TOCTOU checks, duplicate-JSON-key guard, constant `az` argument validator | Removed or rationale proven; Azure token added to known secrets | 054 |
| Verbatim doc pins, "user approval" wording for agent-supplied flags, owner checks described as access control | Structure pins; honest framing | 029 step 5 |
| `standard_conforming_strings` | Kept: a lexing split could hide `COMMIT;` and escape 052 | 031 |
| T-SQL server-filesystem denylist incomplete (`fn_dump_dblog`) | Not chased; server permissions are the control (029 framing) | — |

Kept as right: T-SQL classifier and its closed bypass shapes, typed parameter binding, exact `--confirm-database`, MCP budgets and busy gate (the busy gate was later removed by 060), dashboard host/cookie/origin/CSP, owner-only journal files, exits 7/8, offline validate.

## Calendar dates

- 2026-11-10: .NET 8 and .NET 9 end of support (plan 022).
- About 2027-11: next LTS move (.NET 12).
- Each new PostgreSQL major: extend diagnostic test/source coverage (037) when useful; compatible runtime shapes do not wait for a version allowlist. Review known dangerous-call checks (019) against concrete evidence rather than widening name prefixes automatically.
