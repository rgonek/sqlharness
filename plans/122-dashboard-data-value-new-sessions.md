# Plan 122: Dashboard data value — new sessions only

> **For agentic workers:** Use `superpowers:executing-plans` task-by-task. This plan does not authorize implementation, live DB execution or publication.

**Status:** PLANNED — dokumentacja, bez zmian produktu.

**Goal:** Użyteczne dane wyłącznie nowych sesji oraz usunięcie placeholderów bez planowanego źródła.

**Architecture:** Addytywny marker nowych sesji ogranicza projekcje i retencję. Istniejący journal oraz materiał bieżącego wykonania dostarczają dowodów bez historycznej rekonstrukcji i dodatkowego SQL.

**Tech Stack:** C#/.NET, SQLite, SQL Server Showplan, React/TypeScript, istniejący shadcn/Base UI, xUnit/Vitest.

**Spec:** [Zakres i kontrakty](../docs/superpowers/specs/2026-10-26-dashboard-data-value.md). Czytać łącznie z planem.

## Global constraints

- Tylko nowe rekordy `sessions` z markerem 1; wznowione sesje i nowe operacje starych sesji pozostają na dotychczasowym kontrakcie.
- Zero backfillu, także leniwego przy GET lub z raw planów. Addytywna migracja schematu bez przepisywania historycznych wartości.
- Usunięcie pustych pól działa globalnie w UI. Pozostały UX poza zakresem.
- Brak dodatkowych SQL, duplikacji XML, zmiany storeSensitive, policy, klasyfikacji mutacji i approval za operatora.
- Zachować rzeczywiste batch counters, source identity i evidence warnings; brak nie oznacza zera.

## Review focus

1. Wznowiony session_key i nowe operacje starej sesji nie otrzymują nowego kontraktu (Task 1).
2. Stara/nowa strona A/B i niekompletne case coverage nie tworzą fałszywej delty (Task 2).
3. Duplikaty SQL, UDF/dynamic SQL, wiele dokumentów/repetitions zachowują tożsamość i niepewność (Tasks 2/5).
4. StoreSensitive off, Unicode i pagination nie ujawniają treści ani nie zmieniają zbioru historii (Task 3).
5. Multi-statement mutation, -1 affected rows, unknown outcome i brak request link nie dają fałszywego potwierdzenia (Task 4).

## Podstawa, kolejność i koszt

Punkt odniesienia: Plan 121, korekty produktu `a685e942`, audyt `3eff7808`. Przed wykonaniem sprawdzić rzeczywistą obecność produktu w wybranej gałęzi; sam wpis indeksu nie wystarcza. Nie scalać automatycznie gałęzi.

Kolejność: **1 → 2 → 3 → 4 → 5 → 6**. Tasks 2–5 wymagają 1. Task 6 niezależny poza nowym LOB z 2; końcowe sprawdzenie po 2. Serializować wspólne modele, JournalReader, OperationPage i indeks.

| Task | Szacunek |
|---|---|
| 1. Granica sesji | 1–2 dni |
| 2. CPU/elapsed i LOB logical | 3–5 dni |
| 3. SQL preview | 1–2 dni |
| 4. Affected rows i approval | 2–5 dni |
| 5. Granty i spill warning | 4–8 dni |
| 6. Usunięcie nieobsługiwanych pól | 0,5–1,5 dnia |

Około 12–24 osobodni, plus końcowa bramka/poprawki. Niepewności: kompletność Showplan i istniejące request/operation links. To szacunek pracy developera, nie czasu agenta.

## Task 1: Wersjonowana granica nowych sesji

**Files:** `src/SqlHarness.Core/Journal/{JournalSchema,ActivityJournal}.cs`, `src/SqlHarness.Dashboard/JournalReader.cs`; `tests/SqlHarness.Tests/Journal/{ActivityJournalTests,JournalContractTests}.cs`, `tests/SqlHarness.Tests/Dashboard/JournalReaderTests.cs`.

**Interface:** nullable SQLite `sessions.dashboard_evidence_version`; nowy INSERT zapisuje 1. Nullable `DashboardEvidenceVersion` w modelu operacji, JSON `dashboardEvidenceVersion`. Marker oznacza wersję kontraktu, nie obecność każdego licznika.

- [ ] RED: migracja starej bazy pozostawia NULL i nie zmienia dawnych danych; nowy klucz dostaje 1; wznowiony klucz i jego nowe operacje pozostają NULL. Pokryć fresh DB i konkurencyjny INSERT/upsert.
- [ ] Uruchomić testy zadania i potwierdzić RED; dodać nullable kolumnę bez historycznej wartości domyślnej, marker wyłącznie przy INSERT i kontrolę eligibility readera. GET bez zapisów, bez cutoff po dacie.
- [ ] GREEN: te same testy, niezmienione stare dane, brak historycznych UPDATE. Review diffu i commit tylko zakresu zadania.

## Task 2: CPU/elapsed i LOB logical

**Files:** dashboard `JournalReader.cs`, `Optimization/{OptimizationModels,OptimizationComparer}.cs`; `src/SqlHarness.Dashboard/ui/src/api/types.ts`, `components/optimization/{IoComparison,StatementRows}.tsx`, `lib/ioComparison.ts`; testy `Dashboard/{JournalReaderTests,OptimizationComparerTests}.cs` oraz odpowiadające `IoComparison.test.tsx`, `StatementRows.test.tsx`.

**Interface:** nullable CPU/elapsed before/after i `PlanRepetition`, istniejące exact plan links; LOB logical before/after na obiekcie. Zachować dotychczasowy total reads. Pola dostępne tylko dla eligible operacji i pojedynczego case.

- [ ] RED: zachowane CPU/elapsed pochodzą z runu wybranego według reads, nie mediany czasu ani ostatniego planu; LOB nie zwiększa drugi raz total.
- [ ] RED: zero/NULL, mixed old/new A/B, All cases, partial/unmatched/hidden work, powtarzający się tekst i PostgreSQL. Brak częściowych agregatów lub zastępowania braku zerem.
- [ ] Potwierdzić RED; rozszerzyć SELECT, modele i istniejące szczegóły metryk o jednostki/provenance. Bez zmiany buildera wyboru runu i domyślnego planu. W All cases nowe metryki niedostępne z wyjaśnieniem.
- [ ] GREEN, review diffu i commit. Odczyt starej operacji nie dostaje nowych wartości nawet z istniejących kolumn.

## Task 3: SQL preview w Activity

**Files:** dashboard `JournalReader.cs`; UI `api/types.ts`, `components/ActivityOperations.tsx`; `Dashboard/JournalReaderTests.cs`; `components/ActivityOperations.test.tsx` (utworzyć, jeśli brak).

**Interface:** nullable `sqlPreview`, boolean `sqlPreviewTruncated`; maks. 240 znaków Unicode przed oznaczeniem ucięcia, baseline dla compare. Bez pełnego SQL w odpowiedzi listy, bez nowego zapisu i search.

- [ ] RED: 240/241 znaków, znaki spoza BMP, pusty/NULL SQL, storeSensitive off, stara sesja, compare baseline i niezmieniona paginacja.
- [ ] Potwierdzić RED; dodać bounded projekcję i preview obok istniejącego label/subject, bez fetch per row.
- [ ] GREEN, sprawdzić limity odpowiedzi, review diffu i commit.

## Task 4: Affected rows i approval lifecycle

**Files:** Core `Journal/{JournalModels,OperationJournalDescriber,ActivityJournal,JournalSchema}.cs`; dashboard `Mutations/MutationExecutor.cs`, `JournalReader.cs`; UI `api/types.ts`, `pages/OperationPage.tsx`; testy `Journal/JournalContractTests.cs`, `Dashboard/{MutationExecutorTests,JournalReaderTests}.cs`, `pages/OperationPage.test.tsx`.

**Interface:** nullable `RecordsAffected` w zakończeniu/historii, JSON `recordsAffected`. Dokładne nullable `mutationRequestId` tylko jeśli brak istniejącej równoważnej relacji. Zapis dla eligible sesji; lifecycle z istniejącego journala, bez snapshotu policy.

- [ ] RED: sukces 0/>0, provider -1, wiele instrukcji, odmowa, timeout/unknown outcome, operator/automatic, brak request link, stara sesja. Nie dopasowywać po SQL/czasie, nie wyświetlać bieżących settings jako dawnych.
- [ ] Potwierdzić RED; przekazać istniejący wynik do journala/detailu. Addytywna retencja scalar/relacji tylko jeśli konieczna, bez historycznej rekonstrukcji.
- [ ] Pokazać zapisany lifecycle i link do Mutations w istniejącym obszarze Approval; bez nowych mechanizmów zatwierdzania i bez Estimated.
- [ ] GREEN, review policy/classification i diffu; commit.

## Task 5: Granty i spill warning z nowego wykonania

**Files:** Core `StatementMetrics.cs`, `Journal/{JournalBenchmarkBuilder,BenchmarkJournalRecord,JournalSchema,ActivityJournal}.cs`; dashboard reader/modele/comparer; UI typy i istniejące szczegóły w `IoComparison.tsx`; testy `Journal/{JournalBenchmarkBuilderTests,ActivityJournalBenchmarkTests}.cs`, `Dashboard/OptimizationComparerTests.cs`, `IoComparison.test.tsx`. Małe XML fixture przy istniejących fixture tych testów.

**Interface:** nullable `GrantRequestedKb`, `GrantGrantedKb`, `GrantMaxUsedKb`, `HasSpillWarning`, provenance z Task 2. Normalizacja podczas zapisu eligible operacji, bez parsera starych planów przy GET.

- [ ] RED: dwa statementy z różnymi grantami, wiele dokumentów/runów, brak QueryPlan, omitted/partial, dynamic SQL/UDF i niepewna atrybucja; żadnego kopiowania batch value. True/false/NULL spill zgodnie ze spec.
- [ ] RED: stare raw plany nigdy nie są czytane dla nowych pól; PostgreSQL NULL; storeSensitive off nie zapisuje nowego XML/SQL; All cases nie sumuje grantów.
- [ ] Potwierdzić RED; rozszerzyć istniejący przebieg ekstrakcji i zapis scalarów dla wybranego runu. Bez zmiany formatów artefaktów/MCP, jeśli wystarczy osobny kontrakt journala.
- [ ] GREEN; offline porównać narzut parsera/rozmiar rekordu na małym i wielostatementowym fixture. Opisać wyniki bez live performance claim; review diffu i commit.

## Task 6: Usuń pola bez planowanego źródła

**Files:** UI `components/optimization/IoComparison.tsx`, `lib/ioComparison.ts`, `pages/OperationPage.tsx` i ich istniejące testy.

- [ ] By statement: usunąć opcje/kolumny Physical, Read-ahead, LOB physical i LOB read-ahead, w tym zaznaczenia pozostawione po zmianie widoku. Zachować je i realne dane w Whole batch. Zachować nowe LOB logical w By statement.
- [ ] Usunąć query Columns, mutation Estimated oraz benchmark Rows bez właściwego źródła. Zachować query Rows, nowe Rows affected i rzeczywiste wartości z poprawnym kontraktem.
- [ ] Nie dodawać Best/pustych tabel wyników; zachować Results unavailable i evidence warnings. Pozostałe propozycje UX nietknięte.
- [ ] Focused testy przełączania widoków i OperationPage, desktop/mobile; review diffu i commit. Bez nowej infrastruktury testowej dla prostego usuwania etykiet.

## Końcowa weryfikacja przyszłej implementacji

- [ ] Focused Core: `dotnet test tests/SqlHarness.Tests/SqlHarness.Tests.csproj --filter "FullyQualifiedName~Journal|FullyQualifiedName~OptimizationComparer|FullyQualifiedName~MutationExecutor"`.
- [ ] Focused UI w `src/SqlHarness.Dashboard/ui`: `npm.cmd test -- src/components/optimization/IoComparison.test.tsx src/components/optimization/StatementRows.test.tsx src/pages/OperationPage.test.tsx src/components/ActivityOperations.test.tsx`.
- [ ] Offline mixed-history fixture: stare, wznowione i nowe sesje; stare wartości niezmienione; zero target connections i zapisów przy GET. Nowe operacje starej sesji nadal bez nowego kontraktu.
- [ ] Review całości względem spec, no-backfill i provenance; poprawić blokujące uwagi. Screenshoty nowych danych/usuniętych pól bez live targetu.
- [ ] Windows `just verify` na końcowym produkcie według `docs/agents/verification.md`; wyniki i revision pod `plans/122-evidence/`. Linux poza zakresem. Aktualizować plan/index zgodnie z faktycznym statusem, bez deklaracji instalacji, merge, push lub live acceptance.

## Odłożone

SQL search, schemat/count kolumn, komórki wyników, benchmark rows, nowe physical IO statementów, snapshot policy, Estimated, Best i dowolne wersje planu nie są zadaniami tego planu. Wymagają osobnego wyboru zakresu. Pozostałe propozycje UX są wstrzymane.
