# Architecture consolidation Implementation Plan

**Status (2026-10-08):** DONE — T1 1db723e, T2 c3ecffa + [ADR T2](2026-09-26-audit-05-architecture-adr-t2-typed-result.md), T3 4710313. Checkboxy poniżej nie były odhaczane w trakcie wykonania.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Usunąć rozproszone reguły i powtarzalną infrastrukturę bez mnożenia abstrakcji ani zmiany semantyki komend.

**Architecture:** Zachować publiczną fasadę i adaptery sesji. Skonsolidować analizę dialektu, wykonanie rodzin operacji, tworzenie wyników i publikację plików.

**Tech Stack:** .NET 8, C#, xUnit; bez nowego kontenera DI/plugin framework.

**Spec:** [audyt — interfejsy i duplikacja](../specs/2026-09-26-project-audit.md), [roadmapa](2026-09-26-audit-roadmap.md).

## Global Constraints

- Po stabilizacji napraw 01/02 i kontraktów 03/04; nie opóźniać nimi pilnych poprawek.
- Zachować ISqlHarnessModule, ISqlSessionFactory, ISqlSession, ISqlReader, ISnapshotStore i IGainStore jako odrębne role.
- Nie scalać measure-param-set z compare-matrix ani schema/space/indexes.
- Writery domenowe pozostają osobne; współdzielą mechanikę zapisu, nie politykę ujawniania danych.
- Brak zmian CLI/JSON wyłącznie z powodu przenoszenia klas.

## Review Focus

- Bezpośredni konsument Core i stare konstruktory testowe: T1–T2.
- Błąd setup i cleanup maskujący pierwotną przyczynę: T2.
- Częściowo ukończona matrix i zapis artefaktu: T2–T3.
- Błąd przeniesienia katalogu oraz równoczesne wywołania: T3.
- Reparse, martwe człony i różne decyzje dwóch analizatorów: T1.

## T1 — spójny moduł analizy dialektu

**Files:** `src/SqlHarness.Core/Dialect/ISqlDialect.cs`, `Dialect/SqlServerDialect.cs`, `Postgres/PostgresDialect.cs`, `SqlSafety.cs`, `Postgres/PostgresSafetyClassifier.cs`, `Postgres/PostgresBenchmark.cs`; testy SqlSafetyTests, PostgresSafetyTests, SqlParameterReferenceValidatorTests, PostgresBenchmarkTests.

**Interfaces:** kontrakt parametrów z 02/T1 pozostaje jedynym wywołaniem. Wynik analizy ma przenosić klasyfikację, parametry, lokalne obiekty i możliwość benchmarku; szczegółowy typ ustalić z faktycznymi konsumentami, nie jako publiczne API bez potrzeby.

- [ ] Sporządzić listę produkcyjnych użyć każdego członu ISqlDialect; usunąć nieużywane IdentitySql i CollectSessionTempTables albo przenieść ich jedynego faktycznego właściciela. Brak konsumenta oznacza usunięcie, nie nowe wywołanie tylko dla zachowania interfejsu.
- [ ] Wyodrębnić wspólny parsed-document/analysis wewnątrz każdego silnika, żeby Classify/ValidateMeasuredBatch/referencje parametrów nie implementowały różnych reguł CTE/INTO. Nie wymuszać jednego AST dla obu bibliotek.
- [ ] Testy tabelaryczne tych samych SQL w query/setup/benchmark: różnice tylko wynikające z kontraktu trybu. Zachować wszystkie regresje 01/02 i testy poprawnego SELECT z hintami/funkcjami T-SQL.
- [ ] Uruchomić wskazane testy; lokalny commit `refactor: consolidate dialect analysis responsibilities`.

## T2 — fasada i lifecycle operacji

**Files:** `src/SqlHarness.Core/SqlHarnessModule.cs`, `CompareCellRunner.cs`, `WatchRunner.cs`, `SnapshotRunner.cs`, `Contracts.cs`, `SqlExecution.cs`; nowe `BenchmarkRunner.cs`, `BenchmarkCollector.cs`, `OperationFailureMapper.cs`; testy ContractsTests, BenchmarkRunTests, CompareTests, WatchTests, SnapshotTests.

**Interfaces:** zachować `ISqlHarnessModule.ExecuteAsync(SqlHarnessOperation, CancellationToken) -> Task<SqlHarnessOutcome>`. Mapper przyjmuje exception i etap, zwraca błąd/exit code według 03/T1; nie mapuje na podstawie tekstu. Identity sesji docelowo getter-only, nadana w fabryce przed przekazaniem konsumentowi.

- [ ] Przenieść BenchmarkRunner/Collector do plików odpowiedzialności; usunąć zależność runnerów od statycznych helperów fasady przez przeniesienie helperów do właściciela wykonania/raportu.
- [ ] Wydzielić przygotowanie i wykonanie rodzin operacji dopiero tam, gdzie istnieje powtarzalny lifecycle; nie tworzyć klasy na każde kilka linii. Fasada zostaje dispatch/composition.
- [ ] Ujednolicić mapowanie wyjątków między query/watch/snapshot/compare, zachowując różnicę auth/SQL/local storage i pierwotną przyczynę przy błędzie cleanup.
- [ ] Ocenić zastąpienie object? Report wewnętrznym typowanym wynikiem. Testy muszą uniemożliwić połączenie operacji z niepasującym raportem; nie łamać publicznego API bez wersjonowania. Jeśli korzyść wymaga szerokiej migracji, zachować kompatybilny adapter i zapisać ADR zakresu.
- [ ] Dodać testy read-only Identity, setup once, error receipt, częściowej matrix i błędu cleanup. Uruchomić wskazane klasy oraz komplet offline; lokalny commit.

## T3 — wspólna publikacja artefaktów

**Files:** `src/SqlHarness.Core/Artifacts.cs`, `QueryStoreArtifacts.cs`, `IndexAnalysisArtifacts.cs`; nowy `ArtifactDirectoryPublisher.cs`; testy ArtifactWriterTests, QueryStoreArtifactWriterTests, IndexAnalysisArtifactWriterTests. SnapshotStore/GainStore nie przechodzą automatycznie na ten kontrakt.

**Interfaces:** zachować domenowe Write(...) writerów. Wewnętrzny publisher zarządza root/staging/publish/cleanup; writer domenowy odpowiada za zawartość i dopasowanie danych wrażliwych do raportu.

- [ ] Dodać wspólną macierz failure injection: pierwsze/kolejne write, move, cleanup, collision, równoległe zapisy. Nie publikować częściowego final directory ani success path przed zakończeniem.
- [ ] Przenieść powielone generowanie ścieżek, staging i sprzątanie do publishera; ścieżki muszą pozostać w określonym root, a identyfikatory nie mogą ujawniać parametrów.
- [ ] Zachować osobne walidacje Query Store text matching, filtrów indeksów i ochrony planów/param-set. Nie traktować dowolnego report object jako bezpiecznego do zapisania.
- [ ] Uruchomić trzy klasy writerów, testy błędów modułu i komplet offline; lokalny commit. Raport review ma wykazać usunięte duplikacje i brak zmiany treści publicznej.
