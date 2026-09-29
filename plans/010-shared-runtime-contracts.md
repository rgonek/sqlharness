# Plan 010: Usuń powielone pętle watch i czyste reguły adapterów

Status: **TODO**  
Data: 2026-09-29. Baza: `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`.  
Priorytet: P3; nakład: L; ryzyko zmiany: MED.  
Pokrycie: **D1; ocena interfejsów**. Zależności: **004, 007, 008, 009**.

## Kontekst i instrukcja wykonania

Czytaj cały plan. Pracuj w izolowanym worktree, zachowaj cudze zmiany. Zapis planu nie jest wdrożeniem. Po ukończeniu zaktualizuj plans/README.md wraz z dowodem; nie pushuj ani nie publikuj bez zlecenia.

Drift check:
```powershell
git status --short
git diff --stat 8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b..HEAD -- "src/SqlHarness.Core/WatchRunner.cs" "src/SqlHarness.Core/WatchNdjson.cs" "src/SqlHarness.Core/Capabilities.cs" "src/SqlHarness.Mcp/McpOperationMapper.cs" "src/SqlHarness.Cli/Commands/WatchCommand.cs" "src/SqlHarness.Cli/Commands/QueryStoreTopCommand.cs" "tests/SqlHarness.Tests/WatchTests.cs" "tests/SqlHarness.Tests/WatchNdjsonTests.cs" "tests/SqlHarness.Mcp.Tests/McpMappingTests.cs"
```

Zmiany poprzedników są oczekiwane: przeczytaj je i dopasuj plan przed kodowaniem. Niewyjaśniony rozjazd wymaga aktualizacji założeń, nie mechanicznego wklejania starego kodu.

WatchRunner.ExecuteAsync i ExecuteNdjsonAsync mają dwie pętle budżetu/read/condition/delay. Mapper MCP powtarza parsowanie czasu i bounds, capabilities opisuje je osobno. Dwa transporty i odrębne writery nie są zbędnymi interfejsami.

Punkt odniesienia z kodu/komendy:
```text
while (true)
```

.NET8/C#/xUnit. Dopasuj wzorce do testów wymienionych w zakresie. Core pozostaje wspólnym miejscem safety; adaptery nie tworzą własnego klasyfikatora. Stosuj istniejące fake reader/session/module i syntetyczny SQLHARNESS_HOME. Nie czytaj rzeczywistych sekretów ani artefaktów użytkownika.

## Zakres

- `src/SqlHarness.Core/WatchRunner.cs`
- `src/SqlHarness.Core/WatchNdjson.cs`
- `src/SqlHarness.Core/Capabilities.cs`
- `src/SqlHarness.Mcp/McpOperationMapper.cs`
- `src/SqlHarness.Cli/Commands/WatchCommand.cs`
- `src/SqlHarness.Cli/Commands/QueryStoreTopCommand.cs`
- `tests/SqlHarness.Tests/WatchTests.cs`
- `tests/SqlHarness.Tests/WatchNdjsonTests.cs`
- `tests/SqlHarness.Mcp.Tests/McpMappingTests.cs`

Dodatkowo: nowe pliki nazwane w krokach, dokumenty wynikowe plans/010-*.md oraz status w indeksie. Inne pliki wymagają jawnej korekty zakresu i uzasadnienia przed zmianą.

Poza zakresem: live DB, deploy, profile i hasła użytkownika, instalacja, push, globalne wyłączenie walidacji i niepowiązane refaktoryzacje. Zachowaj kody 0/2/3/4/5/6/7/8, legacy JSON i oba silniki. Pola addytywne tylko zgodnie z krokami.

## Zadania

### 010/T1

- [ ] Dodaj testy parytetu końcowych decyzji watch raport/NDJSON z fake clock: condition, unchanged, timeout connect/read/delay, late result, caller cancellation i błąd. Zachowaj brak historii w NDJSON.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~WatchTests|FullyQualifiedName~WatchNdjsonTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~OperationLimitsTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 010/T2

- [ ] Wydziel jeden silnik polling emitujący zdarzenia do dwóch małych odbiorników. Obecne publiczne ExecuteAsync i ExecuteWatchNdjsonAsync pozostają adapterami; jeden monotoniczny budżet obejmuje całą operację.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~WatchTests|FullyQualifiedName~WatchNdjsonTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~OperationLimitsTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 010/T3

- [ ] Wydziel wyłącznie identyczne czyste reguły bounds/duration do nowego pliku Core OperationLimits.cs, z nowym testem OperationLimitsTests.cs. Transportowe formaty i błędy pozostają w adapterach. Capabilities korzysta z tych samych stałych.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~WatchTests|FullyQualifiedName~WatchNdjsonTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~OperationLimitsTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 010/T4

- [ ] Nie scalać CLI/MCP, query/measure/compare, param-set/matrix, writerów domenowych ani zegarów tylko z powodu wspólnej metody. Zachować ADR object Report i OperationReportContract. Zapisz w plans/010-interface-decisions.md decyzje zachowania granic.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~WatchTests|FullyQualifiedName~WatchNdjsonTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~OperationLimitsTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

## Testy i zakończenie

Jeden silnik polling, identyczne terminal outcomes przed/po, brak wzrostu retencji NDJSON; bounds CLI/MCP zgodne.

- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~WatchTests|FullyQualifiedName~WatchNdjsonTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~OperationLimitsTests' --verbosity minimal` → exit 0.
- [ ] `dotnet build SqlHarness.sln --no-restore -warnaserror` → exit 0.
- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName!~Integration' --verbosity minimal` → exit 0; zapisz passed/failed/skipped.
- [ ] Testy obejmują zachowanie właściwego adaptera/runnera, a nie tylko listę nazw lub stałą.
- [ ] `git diff --check` → exit 0.
- [ ] `git status --short` pokazuje wyłącznie autorskie zmiany w zakresie.
- [ ] Indeks zawiera status, commit i dowód; lokalne ścieżki istnieją lub są oznaczone jako nowe.
- [ ] Brak dowodu live/platformowego jest jawny.

Baseline audytu: 1895 Core/CLI passed; MCP 122 passed, 2 timeouty, 4 skipped. Plan005 diagnozuje timeouty. Wcześniejsza naprawa bezpieczeństwa może być gotowa do review przy udokumentowanej niezależnej awarii gate; nie ogłaszaj wtedy pełnego PASS.

## Warunki zatrzymania i utrzymanie

Nie ujednolicaj reguł, które mają świadomie odmienne jednostki/defaulty. Nie zamieniaj refaktoryzacji w zmianę publicznego API.

Jeśli krok wymaga rzeczywistego celu DB, sekretów lub wyjścia poza autoryzację, zatrzymaj ten krok i zapisz brak. Dokończ niezależne zadania. Nie oznaczaj całości DONE bez wszystkich wymaganych wyników.

Reviewer sprawdza bezpieczeństwo negatywnych przypadków, kompatybilność i prawdziwość dowodów. Po zmianie parsera/SDK/operacji wróć do tych testów. Małe commity typu fix/test/docs, bez push. Wymagane testy charakterystyczne muszą poprzedzać refaktoryzację.

