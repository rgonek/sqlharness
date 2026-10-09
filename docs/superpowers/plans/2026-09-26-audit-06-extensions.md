# Safe extensions and follow-up design Implementation Plan

**Status (2026-10-08):** DONE — T1 b3b2d06/814267c/5b3ddd9/5ed9970/141f497/0d98173 (dalej `plans/011`), T2 845570c, T3 e268c38, T4 50ba1db/e363e3b (tylko specyfikacja; implementacja = `plans/035`), T5 be70c8f (tylko specyfikacja; implementacja = `plans/037`). Checkboxy poniżej nie były odhaczane w trakcie wykonania.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Zmniejszyć nadmiarowe blokady oraz przygotować funkcje skracające pracę agenta bez rozszerzania niejawnie uprawnień.

**Architecture:** Wykorzystać poprawione granice bezpieczeństwa, odpowiedzi i zasobów. Wdrożenia T1–T3 mają konkretne kontrakty; T4–T5 najpierw dostarczają specyfikacje, ponieważ progi CI i pg_stat_statements wymagają odrębnych decyzji produktowych.

**Tech Stack:** .NET 8, ScriptDom, SqlParserCS, Spectre.Console.Cli, PostgreSQL, xUnit.

**Spec:** [audyt B4 i propozycje funkcji](../specs/2026-09-26-project-audit.md), [roadmapa](2026-09-26-audit-roadmap.md).

## Global Constraints

- T1 po 01 i 02/T1; T2 po 03/T1–T3 i 05/T3; T3 po 04/T1–T2; T4 po 02/T3.
- Bez nowego unsafe bypass, trwałego DDL lub automatycznego tworzenia rozszerzeń PostgreSQL.
- Nowa składnia jest wspierana tylko wtedy, gdy analiza obejmuje wszystkie efekty; parse failure pozostaje odmową.
- Plany i tekst SQL nie stają się automatycznie publiczne przez komendę artefaktową.
- Nie scalać semantyki Query Store z pg_stat_statements pod identycznym raportem bez opisania różnic.

## Review Focus

- DECLARE initializer i EXPLAIN ANALYZE z efektami ubocznymi: T1.
- ALTER/TRUNCATE celu innego niż prawdziwy temp: T1.
- Path traversal, symlink i artefakt poufny: T2.
- Zatrzymanie procesu w połowie strumienia NDJSON: T3.
- Niestabilny wynik/metryka niedostępna oraz reset statystyk: T4–T5.

## T1 — bezpieczne rozszerzenia składni

**Files:** `src/SqlHarness.Core/SqlSafety.cs`, `Postgres/PostgresSafetyClassifier.cs`, `Postgres/PostgresBenchmark.cs`; testy SqlSafetyTests, PostgresSafetyTests; `AGENTS.md`, `README.md`.

**Interfaces:** istniejące Classify i diagnostyka 03/T1; w spec zachować listę dokładnie wspieranych konstrukcji, bez globalnego „allow all DDL on temp”.

- [ ] T-SQL: dopuścić DECLARE scalar i SELECT w query z analizą initializer; TRUNCATE wyłącznie jednoznacznej local #temp; ALTER TABLE #temp tylko ADD/DROP COLUMN i obsługiwane lokalne constraints. Każdy wariant ma test pozytywny i trwały/cross-database negatywny. Nie dopuścić EXEC/dynamic SQL przez deklarację.
- [ ] PostgreSQL: rozważyć EXPLAIN bez ANALYZE nad bezpiecznym SELECT oraz SELECT INTO TEMP z jednoznaczną lokalnością. EXPLAIN ANALYZE wykonuje SQL — podlega pełnej analizie efektów i nie jest automatycznie odczytem.
- [ ] ANALYZE tylko ustalonej tabeli sesyjnej: sprawdzić wsparcie parsera. Jeśli go brak, ocenić aktualizację biblioteki z pełną regresją; nie zastępować parsera regexem. Zapisać konkretny blocker, jeśli nie ma bezpiecznej ścieżki.
- [ ] Obsługę funkcji użytkownika lo_ rozwiązać polityką tożsamości/efektów 01/T3; nie usuwać blokady prefiksowej bez zastępczej ochrony.
- [ ] Uruchomić testy obu classifierów; autoryzowane testy live sprawdzają faktyczny temp oraz brak trwałych skutków. Wdrożone możliwości opisać w capabilities; lokalne commity per konstrukcja.

## T2 — selektywne czytanie istniejących artefaktów

**Files:** nowe `src/SqlHarness.Core/ArtifactReader.cs`, `src/SqlHarness.Cli/Commands/ArtifactCommand.cs`, `tests/SqlHarness.Tests/ArtifactReaderTests.cs`, `tests/SqlHarness.Tests/Cli/ArtifactCommandTests.cs`; rejestracja CLI i capabilities.

**Interfaces:** planowana komenda `artifact <id> --section summary|metrics|operators --json` lub --output agent; identyfikator z manifestu, nie dowolna ścieżka. Nie dodawać w pierwszej wersji odczytu queries.jsonl, raw plan XML, snapshot cells ani source SQL.

- [ ] Zapisać wersjonowany manifest artefaktu i mapowanie do bezpiecznych sekcji. Zdefiniować błąd dla starego artefaktu bez manifestu; żadnego zgadywania poufności po rozszerzeniu.
- [ ] Testy traversal, symlink/reparse point wychodzący z root, nieznany id/sekcja, uszkodzony manifest i zbyt duży plik. Odmowa bez ujawnienia zawartości i bez połączenia DB.
- [ ] Zaimplementować limit odczytu i projekcję z budżetem 03/T3; operator po identyfikatorze może zostać wskazany bez wczytywania wszystkich run arrays do wyniku.
- [ ] Test workflow compare→artifact: brak ponownego benchmarku i ta sama metryka co zapisany raport. Uruchomić nowe testy i writer tests; lokalny commit.

## T3 — opcjonalne zdarzenia watch

**Files:** `src/SqlHarness.Core/WatchRunner.cs`, `Contracts.cs`; `src/SqlHarness.Cli/Commands/WatchCommand.cs`, `Commands/Renderer.cs`; `tests/SqlHarness.Tests/WatchTests.cs`, `Cli/AgentOutputTests.cs`.

**Interfaces:** `watch --output ndjson` odrębny od agent/json/json-summary. Rekordy `started`, `changed`, `completed`, `failed`, każdy z schemaVersion, sequence i elapsed; stan danych podlega tej samej redakcji i budżetom.

- [ ] Zdefiniować pojedynczy rekord końcowy i kolejność sequence. Nie generować changed dla niezmienionego wyniku. Puste przerwanie transportu nie może być interpretowane jako sukces.
- [ ] Dodać testy flush po zdarzeniu, anulowania w połowie, błędu serializacji, deadline oraz wolnego odbiorcy. Nie akumulować pełnej historii przed emisją.
- [ ] Zachować istniejący final JSON dla dotychczasowych flag; NDJSON jawnie ogłaszać w capabilities. Zliczanie gain uwzględnia cały strumień.
- [ ] Uruchomić testy watch/CLI, lokalny commit.

## T4 — specyfikacja progów regresji CI

**Files:** nowy `docs/superpowers/specs/2026-09-26-benchmark-regression-policy.md`; przyszłe pliki implementacji wskazać w tej specyfikacji, nie tworzyć stubów.

**Interfaces:** rezultat tego zadania to zatwierdzalna macierz decyzji, nie nowy nieuzgodniony kod wyjścia.

- [ ] Określić metryki, próg względny i minimalną różnicę bezwzględną, bazę zero, brak metryki CPU PG, próbę niestabilną i brak equivalence. Wynik domenowy pass/fail/inconclusive ma być odrębny od powodzenia SQL.
- [ ] Zapisać tabelę syntetycznych przypadków i oczekiwanych decyzji, politykę szumu pomiarowego, liczby powtórzeń i wersjonowania progów.
- [ ] Zaproponować integrację CI bez redefinicji exit 8; uzgodnić nowy tryb lub osobną komendę. Nie twierdzić o regresji, jeśli wynik technicznie nierównoważny albo pomiar niekompletny.
- [ ] Zakończyć zadanie dokumentem z kryteriami i osobnym planem implementacji. Commit `docs: define benchmark regression decision contract`.

## T5 — specyfikacja pg_stat_statements

**Files:** nowy `docs/superpowers/specs/2026-09-26-postgres-statement-diagnostics.md`; `docs/superpowers/plans/2026-09-26-audit-roadmap.md` aktualizacja statusu projektu podrzędnego.

**Interfaces:** tylko specyfikacja nowej read-only diagnostyki. SQL Server qstop zachowuje obecny kontrakt.

- [ ] Zbadać oficjalną dokumentację PostgreSQL 14+ dla dostępności extension, wersji kolumn, uprawnień, resetów, zakresu bazy i widoczności tekstu SQL. Brak rozszerzenia → jawna niedostępność, bez CREATE EXTENSION.
- [ ] Ustalić ranking, identyfikację query, metryki, snapshot/delta i brak obietnicy okna 24h z samego kumulatywnego licznika. Reset/restart ma unieważniać nieporównywalną deltę.
- [ ] Zapisać kontrakt poufności analogiczny do Query Store: stdout metryki/identyfikatory, SQL tylko w lokalnie wrażliwym artefakcie, brak publikacji bez review.
- [ ] Dostarczyć fixtures dla braku extension/uprawnień, pustego wyniku, resetu i dwóch wersji serwera; przygotować oddzielny plan wdrożenia. Nie testować live bez autoryzowanego celu.
- [ ] Commit dokumentacji i mapowanie w capabilities dopiero po rzeczywistym wdrożeniu.
