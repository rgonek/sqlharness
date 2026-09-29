# Plan 007: Zbuduj wspólny preflight query/setup/benchmark

Status: **TODO**  
Data: 2026-09-29. Baza: `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`.  
Priorytet: P2; nakład: L; ryzyko zmiany: MED.  
Pokrycie: **A2; funkcja preflight**. Zależności: **005**.

## Kontekst i instrukcja wykonania

Czytaj cały plan. Pracuj w izolowanym worktree, zachowaj cudze zmiany. Zapis planu nie jest wdrożeniem. Po ukończeniu zaktualizuj plans/README.md wraz z dowodem; nie pushuj ani nie publikuj bez zlecenia.

Drift check:
```powershell
git status --short
git diff --stat 8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b..HEAD -- "src/SqlHarness.Core/SqlValidation.cs" "src/SqlHarness.Core/CompareCellRunner.cs" "src/SqlHarness.Core/SqlHarnessModule.cs" "src/SqlHarness.Core/Dialect/ISqlDialect.cs" "src/SqlHarness.Cli/Commands/ValidateCommand.cs" "src/SqlHarness.Mcp/McpOperationMapper.cs" "src/SqlHarness.Mcp/Tools/McpToolCatalog.cs" "tests/SqlHarness.Tests/Cli/ValidateCommandTests.cs" "tests/SqlHarness.Tests/DialectAnalysisConsistencyTests.cs" "tests/SqlHarness.Mcp.Tests/McpMappingTests.cs"
```

Zmiany poprzedników są oczekiwane: przeczytaj je i dopasuj plan przed kodowaniem. Niewyjaśniony rozjazd wymaga aktualizacji założeń, nie mechanicznego wklejania starego kodu.

MapValidateAsync akceptuje usage, lecz zawsze deleguje do SqlValidation.Validate używającego SqlUsage.Query. PG dwa SELECT-y z usage=benchmark otrzymały allowed=true, choć PostgresBenchmark wymaga jednej instrukcji.

Punkt odniesienia z kodu/komendy:
```text
return SqlValidation.Validate(scope.TargetRequest, sqlText, declarations, scope.Profiles);
```

.NET8/C#/xUnit. Dopasuj wzorce do testów wymienionych w zakresie. Core pozostaje wspólnym miejscem safety; adaptery nie tworzą własnego klasyfikatora. Stosuj istniejące fake reader/session/module i syntetyczny SQLHARNESS_HOME. Nie czytaj rzeczywistych sekretów ani artefaktów użytkownika.

## Zakres

- `src/SqlHarness.Core/SqlValidation.cs`
- `src/SqlHarness.Core/CompareCellRunner.cs`
- `src/SqlHarness.Core/SqlHarnessModule.cs`
- `src/SqlHarness.Core/Dialect/ISqlDialect.cs`
- `src/SqlHarness.Cli/Commands/ValidateCommand.cs`
- `src/SqlHarness.Mcp/McpOperationMapper.cs`
- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs`
- `tests/SqlHarness.Tests/Cli/ValidateCommandTests.cs`
- `tests/SqlHarness.Tests/DialectAnalysisConsistencyTests.cs`
- `tests/SqlHarness.Mcp.Tests/McpMappingTests.cs`

Dodatkowo: nowe pliki nazwane w krokach, dokumenty wynikowe plans/007-*.md oraz status w indeksie. Inne pliki wymagają jawnej korekty zakresu i uzasadnienia przed zmianą.

Poza zakresem: live DB, deploy, profile i hasła użytkownika, instalacja, push, globalne wyłączenie walidacji i niepowiązane refaktoryzacje. Zachowaj kody 0/2/3/4/5/6/7/8, legacy JSON i oba silniki. Pola addytywne tylko zgodnie z krokami.

## Zadania

### 007/T1

- [ ] Dodaj wspólny model ValidationUsage query/setup/benchmark i opcjonalny kontekst setup. Zachowaj istniejącą sygnaturę jako delegację query. Współdziel klasyfikację, analizę parametrów i ValidateMeasuredBatch z prawdziwym preparerem; nie kopiuj blacklist.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~ValidateCommandTests|FullyQualifiedName~DialectAnalysisConsistencyTests|FullyQualifiedName~McpMappingTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 007/T2

- [ ] Testuj odmowę PG wielu instrukcji w benchmark, akceptację query, lokalne temp z setup, brakujące parametry i brak autoryzacji trwałej mutacji. Brak kompletnego kontekstu musi być jawny; nie udawaj dowodu wykonalności.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~ValidateCommandTests|FullyQualifiedName~DialectAnalysisConsistencyTests|FullyQualifiedName~McpMappingTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 007/T3

- [ ] Dodaj CLI --usage (domyślnie query) i wejście setup zgodne z istniejącym czytaniem plików; MCP usage musi trafiać do modelu. Zachowaj oba silniki i offline: zerowe connect/execute w testach.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~ValidateCommandTests|FullyQualifiedName~DialectAnalysisConsistencyTests|FullyQualifiedName~McpMappingTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 007/T4

- [ ] Raportuj bezpieczne reason/location i zakres sprawdzonych warunków. Nie wypisuj SQL ani wartości w błędach. Uzupełnij README.md, docs/mcp.md i capabilities po dodaniu funkcji; te dokumenty są dozwolonym rozszerzeniem scope.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~ValidateCommandTests|FullyQualifiedName~DialectAnalysisConsistencyTests|FullyQualifiedName~McpMappingTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

## Testy i zakończenie

CLI i MCP zwracają zgodne decyzje dla tej samej operacji; benchmark PG nie akceptuje niedopuszczalnego kształtu; validate nigdy nie otwiera sesji.

- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~ValidateCommandTests|FullyQualifiedName~DialectAnalysisConsistencyTests|FullyQualifiedName~McpMappingTests' --verbosity minimal` → exit 0.
- [ ] `dotnet build SqlHarness.sln --no-restore -warnaserror` → exit 0.
- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName!~Integration' --verbosity minimal` → exit 0; zapisz passed/failed/skipped.
- [ ] Testy obejmują zachowanie właściwego adaptera/runnera, a nie tylko listę nazw lub stałą.
- [ ] `git diff --check` → exit 0.
- [ ] `git status --short` pokazuje wyłącznie autorskie zmiany w zakresie.
- [ ] Indeks zawiera status, commit i dowód; lokalne ścieżki istnieją lub są oznaczone jako nowe.
- [ ] Brak dowodu live/platformowego jest jawny.

Baseline audytu: 1895 Core/CLI passed; MCP 122 passed, 2 timeouty, 4 skipped. Plan005 diagnozuje timeouty. Wcześniejsza naprawa bezpieczeństwa może być gotowa do review przy udokumentowanej niezależnej awarii gate; nie ogłaszaj wtedy pełnego PASS.

## Warunki zatrzymania i utrzymanie

Nie sprawdzaj katalogu/uprawnień przez DB w narzędziu offline. Dowód validate nie zastępuje ponownej walidacji wykonania.

Jeśli krok wymaga rzeczywistego celu DB, sekretów lub wyjścia poza autoryzację, zatrzymaj ten krok i zapisz brak. Dokończ niezależne zadania. Nie oznaczaj całości DONE bez wszystkich wymaganych wyników.

Reviewer sprawdza bezpieczeństwo negatywnych przypadków, kompatybilność i prawdziwość dowodów. Po zmianie parsera/SDK/operacji wróć do tych testów. Małe commity typu fix/test/docs, bez push. Wymagane testy charakterystyczne muszą poprzedzać refaktoryzację.

