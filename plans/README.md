# Plany po audycie SQLHarness 2026-09-28

Zapis: 2026-09-29, skill improve. Baza: `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`.
Źródło: [audyt](2026-09-28-project-audit.md). Użytkownik zlecił zapis wszystkich ustaleń; plany nie są jeszcze wdrożone.

## Kolejność i status

| Plan | Pokrycie | Priorytet | Nakład | Zależności | Status |
|---|---|---|---|---|---|
| [001 — Zamknij wyciek SQL i parametrów przez stderr](001-mcp-safe-logging.md) | S1 | P1 | S | — | DONE (branch fix/plan-001-mcp-safe-logging, 48e33e5..4423386; merge/push czeka na decyzję) |
| [002 — Napraw zakres i normalizację ścieżek wejściowych](002-mcp-input-roots.md) | S3, A1; badanie tożsamości pliku | P1 | M | — | DONE (branch fix/plan-002-mcp-input-roots, f343d2b..c241921) |
| [003 — Powiąż dostęp do artefaktów z zakresem MCP](003-mcp-artifact-scope.md) | S2; przestrzeń nazw snapshotów | P1 | L | 001, 002 | TODO |
| [004 — Obejmij inspect wspólną blokadą operacji DB](004-mcp-inspect-gate.md) | R1 | P1 | S | 001 | TODO |
| [005 — Ustal przyczyny timeoutów testów MCP](005-mcp-verification.md) | T1 | P2 | M | 001, 004 | TODO |
| [006 — Ogranicz pamięć i czas porównywania wyników](006-equivalence-budget.md) | R2 | P1 | L | 005 | TODO |
| [007 — Zbuduj wspólny preflight query/setup/benchmark](007-mode-aware-validation.md) | A2; funkcja preflight | P2 | L | 005 | TODO |
| [008 — Udostępnij wersjonowany schemat odpowiedzi MCP](008-mcp-output-schema.md) | A3 | P2 | M | 005, 007 | TODO |
| [009 — Uczyń granicę statycznego safety widoczną dla klientów](009-safety-contract.md) | A4; świadome ograniczenia funkcji PG | P2 | M | 007, 008 | TODO |
| [010 — Usuń powielone pętle watch i czyste reguły adapterów](010-shared-runtime-contracts.md) | D1; ocena interfejsów | P3 | L | 004, 007, 008, 009 | TODO |
| [011 — Zaplanuj i dodaj wąskie rozszerzenia bezpiecznej składni](011-safe-sql-extensions.md) | nadmiarowe blokady SET, table variables, PG TEMP TRUNCATE i ANALYZE | P2 | L | 007, 009 | TODO |
| [012 — Zastąp tekstowy most MCP typowanym modelem parametrów](012-typed-matrix.md) | matrix z przecinkami/pustą wartością; funkcja typowanych wejść | P2 | L | 007, 009 | TODO |
| [013 — Doprecyzuj i przygotuj wdrożenie decyzji regresji CI](013-regression-policy-design.md) | propozycja funkcji regresji | P2 | L | 003, 006, 009 | TODO |
| [014 — Przygotuj wykonalny kontrakt diagnostyki pg_stat_statements](014-postgres-diagnostics-design.md) | propozycja diagnostyki PG | P2 | L | 003, 004, 009 | TODO |
| [015 — Zdiagnozuj rozjazd zainstalowanego CLI i repozytorium](015-installation-alignment.md) | obserwacja PATH i nieaktualnego help | P3 | S | 005 | TODO |

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
| 002 | f343d2b..c241921 (6749163 T1 testy, f71ed8f T1 fix1, 8654c23 T2 normalizacja, c241921 T3 ocena) | gate McpInputReader/McpStartup: 24 passed, 0 failed (SqlHarness.Tests: 0 dopasowanych); build -warnaserror: 0 warn, 0 error; pełny suite bez Integration: Core 1895/0/0, MCP 136/0/4 (2 foreign-RID + 2 live opt-in); git diff --check: 0; doc: plans/002-file-identity-assessment.md; final review: PENDING | Brak dowodu na case-sensitive FS Linuksa (gałąź Ordinal + test Case_only_sibling nie wykonane na takim FS); brak live DB (plan zabrania); smoke publish-timeout nie zaobserwowany (win-x64 przeszedł; znany baseline plan-005) |
| — | — | Pozostałe plany oczekują wykonania | Brak live DB |
