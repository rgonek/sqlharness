# Plany po audycie SQLHarness 2026-09-28

Zapis: 2026-09-29, skill improve. Baza: `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`.
Źródło: [audyt](2026-09-28-project-audit.md). Użytkownik zlecił zapis wszystkich ustaleń; plany nie są jeszcze wdrożone.

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
| [013 — Doprecyzuj i przygotuj wdrożenie decyzji regresji CI](013-regression-policy-design.md) | propozycja funkcji regresji | P2 | L | 003, 006, 009 | DONE (branch docs/plan-013-regression-policy, 983f7b6..f6c8d7a; dowód: [013-regression-contract](013-regression-contract.md), [013-regression-implementation](013-regression-implementation.md); tylko etap projektowy, funkcja PLANNED; brak suite i live DB; nie scalono; push czeka) |
| [014 — Przygotuj wykonalny kontrakt diagnostyki pg_stat_statements](014-postgres-diagnostics-design.md) | propozycja diagnostyki PG | P2 | L | 003, 004, 009 | DONE (branch docs/plan-014-postgres-diagnostics, f0934b3..7d82bf1; dowód: [014-pg-statements-contract](014-pg-statements-contract.md), [014-pg-statements-implementation](014-pg-statements-implementation.md); tylko etap projektowy, funkcja pgstop PLANNED, nie zaimplementowana; brak suite i live DB; nie scalono; push czeka) |
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
| [017](017-tsql-hash-named-cte-bypass.md) | A `#`-named CTE can no longer pass off persistent DML as `#temp` work | P1 | S | 016 | TODO |
| [018](018-tsql-external-access-fail-closed.md) | T-SQL external-access and cross-database checks fail closed; parser to TSql180 | P1 | M | 016, 017 | TODO |
| [019](019-pg-classifier-holes.md) | The PG classifier denies self-named temp CTE writes, SQL-string executors and deny-list gaps | P1 | S | 016 | TODO |
| [020](020-azure-cli-process-hardening.md) | The Azure CLI token fetch no longer runs binaries from the CWD, hangs, or reads MCP stdin | P1 | S | 016 | TODO |
| [021](021-unrecognized-statistics-unavailable.md) | Non-English STATISTICS output is reported as unavailable, not as measured zeros | P1 | M | 016 | TODO |
| [022](022-dotnet10-and-npgsql-migration.md) | .NET 10 LTS plus a supported Npgsql before the 2026-11-10 end of support | P1 | M | 016 | TODO |
| [023](023-tsql-reject-multi-batch-go.md) | `GO`-separated T-SQL is rejected instead of misclassified | P2 | S | 016, after 017/018 | TODO |
| [024](024-canonical-results-common-types.md) | Arrays/inet/NaN/spatial/hierarchyid results no longer fail whole operations | P2 | M | 016 (coordinate with 022) | TODO |
| [025](025-redaction-consistency.md) | Consistent secret sets, password value registered, byte[] fixed, no word corruption | P2 | M | 016 | TODO |
| [026](026-watch-condition-matching.md) | `watch --until` matches bit/bool, quoted strings and exponents; keeps polling while no row exists | P2 | S | 016 | TODO |
| [027](027-gain-store-robustness-and-mcp-gain.md) | Gain never fails a successful command; tolerant reads; MCP records gain | P2 | S–M | 016 | TODO |
| [028](028-release-pipeline-hardening.md) | Releases are versioned, gated on CI, least-privilege, with provenance | P2 | S | 016 | TODO |
| [029](029-agent-docs-and-capabilities-drift.md) | Skill/README/AGENTS/capabilities match the shipped CLI; a sync test guards them | P2 | S | 016 (after 021/023/024/026/027 if scheduled) | TODO |
| [030](030-datetime-params-and-pg-plan-depth.md) | Date/time params never shift silently; deep PG plans parse | P2 | S | 016 (after 022 if both) | TODO |
| [031](031-pg-session-hardening.md) | PG refuses non-standard string lexing; SCRAM only on unauthenticated transports | P2 | S–M | 019 (and 022) | TODO |
| [032](032-ci-matrix-and-test-hygiene.md) | CI on Windows and Linux; honest skips; no env races; no publish leak; Pester runs | P2 | M | 016, 028 | PARTIAL (local Linux gate + parity guard landed on ci/plan-032-matrix; CI matrix withheld on the plan-005 MCP flake; Steps 1-3, Pester, .gitattributes remain) |
| [033](033-sqlserver-target-identity.md) | SQL Server identity accepts FQDN/listeners/db casing without weakening detection | P2 | M | 016 | TODO |
| [034](034-private-sqlharness-home.md) | Unix: home/data owner-only; `doctor` warns on an exposed home | P2 | M | 016 (proof via 032) | TODO |
| [035](035-implement-regress-decider.md) | Direction: implement plan 013 `regress`, Phase A (`MeasuredRunCount`) first | P2 | M–L | 016 | TODO |
| [036](036-live-database-ci-lane.md) | Direction: opt-in CI lane against throwaway PG/SQL Server containers | P2 | M | 016 (028 for pins) | TODO |
| [037](037-rescope-pgstop-current-pg-versions.md) | Direction: extend the `pgstop` design to PostgreSQL 17/18 before implementing it | P2 | S–M | — | TODO |
| [038](038-agent-session-friction-fixes.md) | Live-session friction: one-item/matrix/artifact truncation, setup `#temp` scope, variable diagnostics/batch guard, redaction, input-path hint, per-statement metrics, token guard, XML false positives | P1–P3 | S–M per item | — | TODO (extended by accepted session retro P1–P3) |

Status values: TODO | IN PROGRESS | DONE | BLOCKED (with one-line reason) | REJECTED (with one-line rationale).

## Dependency notes

- **016 first.** Every later plan's done criteria run `scripts/verify.ps1`, which 016 creates, and need a green gate.
- **017 → 018 → 023** all edit `src/SqlHarness.Core/SqlSafety.cs`. Run them sequentially in that order, never in parallel worktrees.
- **018 Step 5** adds the guard test that forces every future ScriptDom upgrade to classify new node types. The switch to `TSql180Parser` (018 Step 6) comes after that guard.
- **019 → 031**: 031's `standard_conforming_strings` check assumes `set_config` cannot be reached through `query_to_xml` (019).
- **022** changes the TFM and Npgsql. If 024, 030 or 031 land after it, re-run their PG tests. 022 STOPs if Npgsql changes `date`/`time` result mappings, because that changes snapshot hashes.
- **028 before 032**: both edit `ci.yml` (028 adds `workflow_call` and permissions; 032 adds the OS matrix).
- **029 last among the docs-touching plans**: its `DocsSyncTests` then enforces documentation for any new command (e.g. 035's `regress`).
- **035 Phase A** should land early: artifacts written before it can never get a non-`inconclusive` verdict.
- **036** provides the live proof that 017, 019, 021, 022, 024 and 037 list as pending.

## Policy decisions needed (not planned yet)

- **Locking reads.** T-SQL `SELECT … WITH (TABLOCKX|XLOCK|UPDLOCK, HOLDLOCK)` and PostgreSQL `SELECT … FOR UPDATE/NO KEY UPDATE/SHARE/KEY SHARE` are classified read-only. They change no data, but they can block writers on production for up to timeout × repeats, and PG row locks write tuple headers and WAL. Options: deny exclusive/row-lock hints, route them to the mutation-approval path, or keep and document them. Evidence: `SqlSafety.cs:399-401`, `PostgresSafetyClassifier.cs:354-361`, `PostgresBenchmark.cs:88-96`.
- **Equivalence and nullability.** Compare's schema hash includes `allowNull`, so a rewrite that only changes column nullability (e.g. `LEFT`→`INNER JOIN` with identical rows) reports `equivalent=false` with 0/0 differing rows. Options: leave nullability out of the hash, or report it as a separate "metadata differs" field. Evidence: `ResultEquivalence.cs:173-174,420-445`.

## Direction verdict recorded

- **Plan 08 (Windows approval tray for MCP mutations) is deferred, not rejected.** It is multi-week and Windows-only, adds a new trust surface, and its own spec admits weak isolation against same-user processes. The CLI already has an approved mutation path. Revisit after 035/036. If resumed, start with a spike of the platform-neutral approval state machine before any UI.

## Findings considered and rejected (2026-10-03)

- xunit v2 → v3 now: low value before 022.
- Splitting `Contracts.cs`, removing the legacy text-parameter adapters: cosmetic, and the adapters are kept for characterization tests (012/final F3).
- Micro-costs (`SessionMessageBuffer` `RemoveAt(0)`, per-call secret sorting, per-resolve compiled regex in `TargetResolver`): not worth doing without a profile showing them.
- PG sidecar warming the cache for the next repetition: document it if it ever matters; total reads are unaffected.
- `Microsoft.SqlServer.Types` native spatial DLL on Linux/macOS: LOW confidence and already documented; at most a misleading exit code.
- `--password-env-var` accepting any variable name on `--unsafe-direct`: a CLI agent with a shell can read env vars anyway, and MCP has no direct path.
- Missing `packages.lock.json`: direct versions are exact-pinned centrally.
- **Not planned by user choice (P3, available for a later run):** double row hashing and repeated plan/SQL parsing (perf); extracting the duplicated single-SQL pipeline from `SqlHarnessModule` and adding one command-descriptor table (tech debt); playground script hardening (password on the `docker exec` argv, 0.0.0.0 port binding, unpinned downloads); index overlap ignoring clustered coverage; `PlanDistiller` rejecting plan-less `StmtSimple`; missing `OperationFailureMapper`/JWT/`PlanIdentity` tests; CLI `qstop`/`indexes` on PG refusing only after connect; the STATISTICS TIME double count for `sp_executesql` (needs a live check).

## Calendar

- 2026-11-10: .NET 8 and .NET 9 end of support (plan 022).
- About 2027-11: next LTS move (.NET 12).
- Each new PostgreSQL major (September/October each year): extend the `pgstop` matrix (plan 037) and review the PG deny list (plan 019).
