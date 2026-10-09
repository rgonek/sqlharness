# Runtime bounds and watch Implementation Plan

**Status (2026-10-08):** DONE — T1 3865a60, T2 0bc4d62/b13ed7c, T3 f0ea5ce/e304d93, T4 015d202. Niestabilność testów hosta MCP przeszła do `plans/005` (wciąż obserwowana). Checkboxy poniżej nie były odhaczane w trakcie wykonania.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Egzekwować deadline watch, ograniczyć historię i pamięć pomocniczą oraz wyjaśnić niestabilność testu procesów.

**Architecture:** Jeden budżet całej operacji, kontrolowane magazynowanie wyników i wiadomości; prezentacja korzysta z kontraktu 03/T3. Pełne przetwarzanie wyniku i ograniczona prezentacja pozostają odrębnymi pojęciami.

**Tech Stack:** .NET 8, CancellationToken, Npgsql/SqlClient, xUnit, kontrolowany zegar.

**Spec:** [audyt B5, A2 i wyniki testów](../specs/2026-09-26-project-audit.md), [roadmapa](2026-09-26-audit-roadmap.md).

## Global Constraints

- Deadline watch obejmuje connect, poll i delay. Nie uruchamiać nowego polecenia po wyczerpaniu budżetu.
- Deadline operacji to exit 7; zewnętrzne anulowanie nie może być mylone z naturalnym końcem czasu.
- Timeout pojedynczej komendy i limit prezentacji nie są gwarancją ograniczenia pracy serwera.
- Snapshot nadal odrzuca niekompletne dane; equivalence nadal obejmuje wymagane wyniki.

## Review Focus

- Deadline osiągnięty podczas connect, delay lub odczytu: T1.
- ConditionMet dokładnie na granicy czasu: T1.
- Zmieniony wynik w każdym poll przez długi czas: T2.
- Wielki string i lawina NOTICE/InfoMessage: T2–T3.
- Wolny start PowerShell, ścieżka ze spacjami i brak drugiego PID: T4.

## T1 — twardy deadline watch

**Files:** `src/SqlHarness.Core/WatchRunner.cs`, `SqlHarnessModule.cs`; `tests/SqlHarness.Tests/WatchTests.cs`, `WatchConditionTests.cs`.

**Interfaces:** zachować `IWatchClock` lub rozszerzyć o monotoniczny pomiar budżetu; linked cancellation musi dać się deterministycznie kontrolować w testach. Wynik nadal `SqlHarnessWatchReport` i `WatchExitReason.MaxDuration`.

- [ ] Testy: delay kończy się na deadline → brak kolejnego ExecuteReaderAsync; blokujący connect/poll otrzymuje anulowanie; command timeout nie przekracza pozostałego budżetu (anulowanie obsługuje precyzję poniżej sekundy).
- [ ] Ustalić tie rule: przy zakończeniu odczytu w deadline najpierw ocenić, czy wynik został ukończony w budżecie; condition-met tylko dla ukończonego na czas wyniku. Przekroczenie → exit 7 z ostatnim kompletnym stanem.
- [ ] Wdrożyć monotoniczny deadline i linked token, poprawne dispose reader/session oraz brak mylenia z tokenem użytkownika. Nie czekać na kolejny poll dla wykrycia końca.
- [ ] Uruchomić `dotnet test --filter 'FullyQualifiedName~WatchTests|FullyQualifiedName~WatchConditionTests'`; lokalny commit.

## T2 — ograniczona historia i wiadomości

**Files:** `WatchRunner.cs`, `Contracts.cs`, `SqlExecution.cs`, `Postgres/NpgsqlSessionFactory.cs`, `QueryResultCollector.cs` pod `src/SqlHarness.Core`; `src/SqlHarness.Cli/Commands/WatchCommand.cs`; `tests/SqlHarness.Tests/WatchTests.cs`, `QueryResultCollectorTests.cs`, `SqlExecutionTests.cs`.

**Interfaces:** planowany `--history-limit` domyślnie 100, zakres 1..10000; raport zawiera totalChangedPolls/omittedPolls i ostatni pełny wynik. Tryb agent dodatkowo stosuje globalny budżet bajtów. Nie mylić zmian historii z wyjściem stream z 06/T3.

- [ ] Test 10000 zmieniających się poll: w pamięci pozostaje najwyżej history-limit raportów; zachować ostatni i licznik pominiętych; kryterium unchanged nie zależy od retencji.
- [ ] Zdefiniować konsumowanie wiadomości per command, aby sesja nie przechowywała notices ze wszystkich poprzednich powtórzeń. Zachować statystyki SQL Server wymagane przez benchmark.
- [ ] Ustalić limit pamięci wiadomości na komendę w spec: dla diagnostyki obcięcie z licznikiem, dla danych niezbędnych do metryk jawne incomplete/error; nie raportować zerowych odczytów po cichym odrzuceniu STATISTICS IO.
- [ ] Dodać testy wielu setów/repetitions oraz lawiny wiadomości, redakcja przed publikacją. Uruchomić wskazane klasy i BenchmarkRunTests; lokalny commit.

## T3 — pamięć, input i koszt query

**Files:** `src/SqlHarness.Cli/Infrastructure/OutputCaptureWriter.cs`, `Commands/SqlHarnessCommands.cs`; `src/SqlHarness.Core/QueryResultCollector.cs`, `CanonicalResults.cs`, `ResultEquivalence.cs`; testy QueryResultCollectorTests, CanonicalResultsTests i nowe `tests/SqlHarness.Tests/Cli/OutputCaptureWriterTests.cs`.

**Interfaces:** zachować Mark/GetAnsiFreeFootprint albo zastąpić ich implementację licznikami różnicowymi; nie gromadzić całego stdout. Projekt wejścia SQL pozostaje file/stdin, bez interpolacji.

- [ ] Test strumienia Unicode i ANSI rozdzielonego między Write calls: footprint poprawny bez pełnego StringBuilder; Mark działa dla wielu kolejnych operacji.
- [ ] Dodać bounded input SQL, proponowany limit 16 MiB UTF-8 na file/stdin; odrzucenie przed parsowaniem i połączeniem. Nie traktować limitu plan XML jako limitu SQL bez jawnej implementacji.
- [ ] Sprawdzić pamięć pojedynczej komórki/LOB: projekcja agent ograniczona, canonical hash kompletny. Jeśli obecny GetValue wymusza pełną materializację, opisać ograniczenie i wydzielić osobny etap streamowania z testem identycznego hasha; nie obiecywać stałej pamięci wcześniej.
- [ ] Wyraźnie opisać --max-rows jako limit prezentacji. Nie dodawać automatycznego TOP/LIMIT ani przerywania wyników zmieniającego hash/equivalence.
- [ ] Uruchomić wymienione testy, zapisać pomiar retencji dla syntetycznych danych i lokalny commit.

## T4 — stabilność testu anulowania procesów

**Files:** `tests/SqlHarness.Tests/Auth/ProcessRunnerTests.cs`; `src/SqlHarness.Core/Auth/AzureCli.cs` wyłącznie jeśli diagnoza wykaże błąd produkcyjny.

**Interfaces:** `IProcessRunner.RunAsync(...)` bez zmian bez dowodu potrzeby.

- [ ] Odtworzyć timeout publikacji PID w kontrolowanych warunkach: opóźniony start, ścieżka temp ze spacjami, błąd potomka. Zachować stderr i stan procesu w diagnostyce testu, bez sekretów.
- [ ] Poprawić synchronizację gotowości i quoting, jeśli to przyczyna; nie ograniczać się do zwiększenia timeoutu. Start-Process testowego potomka używa WindowStyle Hidden.
- [ ] Test nadal ma wykazać zakończenie całego drzewa i wykonać cleanup po timeout/failure. Nie maskować wyjątku ani nie zmieniać go w skip.
- [ ] Uruchomić ProcessRunnerTests w izolacji i wraz z zestawem offline; udokumentować wynik obu przebiegów i lokalny commit tylko jeśli była zmiana.
