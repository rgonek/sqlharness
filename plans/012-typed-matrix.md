# Plan 012: Zastąp tekstowy most MCP typowanym modelem parametrów

Status: **TODO**  
Data: 2026-09-29. Baza: `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`.  
Priorytet: P2; nakład: L; ryzyko zmiany: MED.  
Pokrycie: **matrix z przecinkami/pustą wartością; funkcja typowanych wejść**. Zależności: **007, 009**.

## Kontekst i instrukcja wykonania

Czytaj cały plan. Pracuj w izolowanym worktree, zachowaj cudze zmiany. Zapis planu nie jest wdrożeniem. Po ukończeniu zaktualizuj plans/README.md wraz z dowodem; nie pushuj ani nie publikuj bez zlecenia.

Drift check:
```powershell
git status --short
git diff --stat 8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b..HEAD -- "src/SqlHarness.Core/Contracts.cs" "src/SqlHarness.Core/SqlParameterMatrix.cs" "src/SqlHarness.Core/SqlSafety.cs" "src/SqlHarness.Core/Postgres/PostgresParameters.cs" "src/SqlHarness.Core/CompareMatrixRunner.cs" "src/SqlHarness.Mcp/McpOperationMapper.cs" "src/SqlHarness.Mcp/Tools/McpToolArguments.cs" "tests/SqlHarness.Tests/SqlParameterMatrixTests.cs" "tests/SqlHarness.Tests/CompareMatrixTests.cs" "tests/SqlHarness.Mcp.Tests/McpMappingTests.cs" "tests/SqlHarness.Mcp.Tests/McpSecretRedactionTests.cs"
```

Zmiany poprzedników są oczekiwane: przeczytaj je i dopasuj plan przed kodowaniem. Niewyjaśniony rozjazd wymaga aktualizacji założeń, nie mechanicznego wklejania starego kodu.

MCP ma JSON array Values, ale FormatMatrix składa go z powrotem do name:type=v1,v2 i odrzuca przecinki oraz puste stringi. Parametry także wracają do deklaracji tekstowych. To zadeklarowana ograniczona reprezentacja, nie luka bezpieczeństwa.

Punkt odniesienia z kodu/komendy:
```text
return $"{matrix.Name}:{matrix.Type}={string.Join(",", matrix.Values)}";
```

.NET8/C#/xUnit. Dopasuj wzorce do testów wymienionych w zakresie. Core pozostaje wspólnym miejscem safety; adaptery nie tworzą własnego klasyfikatora. Stosuj istniejące fake reader/session/module i syntetyczny SQLHARNESS_HOME. Nie czytaj rzeczywistych sekretów ani artefaktów użytkownika.

## Zakres

- `src/SqlHarness.Core/Contracts.cs`
- `src/SqlHarness.Core/SqlParameterMatrix.cs`
- `src/SqlHarness.Core/SqlSafety.cs`
- `src/SqlHarness.Core/Postgres/PostgresParameters.cs`
- `src/SqlHarness.Core/CompareMatrixRunner.cs`
- `src/SqlHarness.Mcp/McpOperationMapper.cs`
- `src/SqlHarness.Mcp/Tools/McpToolArguments.cs`
- `tests/SqlHarness.Tests/SqlParameterMatrixTests.cs`
- `tests/SqlHarness.Tests/CompareMatrixTests.cs`
- `tests/SqlHarness.Mcp.Tests/McpMappingTests.cs`
- `tests/SqlHarness.Mcp.Tests/McpSecretRedactionTests.cs`

Dodatkowo: nowe pliki nazwane w krokach, dokumenty wynikowe plans/012-*.md oraz status w indeksie. Inne pliki wymagają jawnej korekty zakresu i uzasadnienia przed zmianą.

Poza zakresem: live DB, deploy, profile i hasła użytkownika, instalacja, push, globalne wyłączenie walidacji i niepowiązane refaktoryzacje. Zachowaj kody 0/2/3/4/5/6/7/8, legacy JSON i oba silniki. Pola addytywne tylko zgodnie z krokami.

## Zadania

### 012/T1

- [ ] Dodaj addytywny model Core dla nazwy, typu i wartości z jawnym null. CLI parser deklaracji ma produkować ten model; zachowaj istniejące publiczne konstruktory przez adapter kompatybilności.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~SqlParameterMatrixTests|FullyQualifiedName~CompareMatrixTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~McpSecretRedactionTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 012/T2

- [ ] Przenieś współdzieloną walidację typu i wiązania do jednego toru; MCP przekazuje strukturę bez join/split. Zdefiniuj puste stringi jako wartości tekstowe, null jako null, a przecinek jako znak wartości; nadal jedna dimensja i co najmniej dwa elementy.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~SqlParameterMatrixTests|FullyQualifiedName~CompareMatrixTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~McpSecretRedactionTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 012/T3

- [ ] Testuj przecinki, równość, dwukropek, Unicode, pusty string, null, decimal(p,s), błędną liczbę, duplikaty nazw i oba silniki. Legacy CLI zachowuje dotychczasowe znaczenie przecinka; nowa reprezentacja CLI wymaga osobnego jawnego argumentu/pliku, bez cichej zmiany parsera.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~SqlParameterMatrixTests|FullyQualifiedName~CompareMatrixTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~McpSecretRedactionTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 012/T4

- [ ] Zachowaj nową sesję/setup per cell, kolejność, first-failure i dotychczasowy kontrakt poufności matrix versus param-set. Test redakcji obejmuje nowe błędy typowane. Nie kopiuj param-set wartości/ścieżek do raportu.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~SqlParameterMatrixTests|FullyQualifiedName~CompareMatrixTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~McpSecretRedactionTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

## Testy i zakończenie

MCP obsługuje legalne wartości tekstowe z przecinkami i puste stringi bez zmiany legacy CLI; ten sam binder i te same odmowy typów dla obu ścieżek.

- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~SqlParameterMatrixTests|FullyQualifiedName~CompareMatrixTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~McpSecretRedactionTests' --verbosity minimal` → exit 0.
- [ ] `dotnet build SqlHarness.sln --no-restore -warnaserror` → exit 0.
- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName!~Integration' --verbosity minimal` → exit 0; zapisz passed/failed/skipped.
- [ ] Testy obejmują zachowanie właściwego adaptera/runnera, a nie tylko listę nazw lub stałą.
- [ ] `git diff --check` → exit 0.
- [ ] `git status --short` pokazuje wyłącznie autorskie zmiany w zakresie.
- [ ] Indeks zawiera status, commit i dowód; lokalne ścieżki istnieją lub są oznaczone jako nowe.
- [ ] Brak dowodu live/platformowego jest jawny.

Baseline audytu: 1895 Core/CLI passed; MCP 122 passed, 2 timeouty, 4 skipped. Plan005 diagnozuje timeouty. Wcześniejsza naprawa bezpieczeństwa może być gotowa do review przy udokumentowanej niezależnej awarii gate; nie ogłaszaj wtedy pełnego PASS.

## Warunki zatrzymania i utrzymanie

Nie koduj wartości przez własne escaping CSV i nie dodawaj drugiego binder'a MCP.

Jeśli krok wymaga rzeczywistego celu DB, sekretów lub wyjścia poza autoryzację, zatrzymaj ten krok i zapisz brak. Dokończ niezależne zadania. Nie oznaczaj całości DONE bez wszystkich wymaganych wyników.

Reviewer sprawdza bezpieczeństwo negatywnych przypadków, kompatybilność i prawdziwość dowodów. Po zmianie parsera/SDK/operacji wróć do tych testów. Małe commity typu fix/test/docs, bez push. Wymagane testy charakterystyczne muszą poprzedzać refaktoryzację.

