# Plan 008: Udostępnij wersjonowany schemat odpowiedzi MCP

Status: **TODO**  
Data: 2026-09-29. Baza: `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`.  
Priorytet: P2; nakład: M; ryzyko zmiany: LOW.  
Pokrycie: **A3**. Zależności: **005, 007**.

## Kontekst i instrukcja wykonania

Czytaj cały plan. Pracuj w izolowanym worktree, zachowaj cudze zmiany. Zapis planu nie jest wdrożeniem. Po ukończeniu zaktualizuj plans/README.md wraz z dowodem; nie pushuj ani nie publikuj bez zlecenia.

Drift check:
```powershell
git status --short
git diff --stat 8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b..HEAD -- "src/SqlHarness.Mcp/McpResultAdapter.cs" "src/SqlHarness.Mcp/Tools/McpToolCatalog.cs" "tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs" "tests/SqlHarness.Mcp.Tests/McpProtocolTests.cs" "tests/SqlHarness.Mcp.Tests/McpOutputTests.cs" "tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs"
```

Zmiany poprzedników są oczekiwane: przeczytaj je i dopasuj plan przed kodowaniem. Niewyjaśniony rozjazd wymaga aktualizacji założeń, nie mechanicznego wklejania starego kodu.

OutputSchema istnieje w adapterze, ale tools/list wszystkich 11 narzędzi zwracało brak outputSchema. Rejestracja McpServerTool.Create nie przypina schematu. Nie jest to dowód niezgodności protokołu, lecz brak odkrywalnego kontraktu.

Punkt odniesienia z kodu/komendy:
```text
public static JsonDocument OutputSchema { get; }
```

.NET8/C#/xUnit. Dopasuj wzorce do testów wymienionych w zakresie. Core pozostaje wspólnym miejscem safety; adaptery nie tworzą własnego klasyfikatora. Stosuj istniejące fake reader/session/module i syntetyczny SQLHARNESS_HOME. Nie czytaj rzeczywistych sekretów ani artefaktów użytkownika.

## Zakres

- `src/SqlHarness.Mcp/McpResultAdapter.cs`
- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs`
- `tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs`
- `tests/SqlHarness.Mcp.Tests/McpProtocolTests.cs`
- `tests/SqlHarness.Mcp.Tests/McpOutputTests.cs`
- `tests/SqlHarness.Mcp.Tests/McpTokenBudgetTests.cs`

Dodatkowo: nowe pliki nazwane w krokach, dokumenty wynikowe plans/008-*.md oraz status w indeksie. Inne pliki wymagają jawnej korekty zakresu i uzasadnienia przed zmianą.

Poza zakresem: live DB, deploy, profile i hasła użytkownika, instalacja, push, globalne wyłączenie walidacji i niepowiązane refaktoryzacje. Zachowaj kody 0/2/3/4/5/6/7/8, legacy JSON i oba silniki. Pola addytywne tylko zgodnie z krokami.

## Zadania

### 008/T1

- [ ] Sprawdź lokalne API zainstalowanego SDK2.2.0 i podłącz istniejący schemat envelope do każdego narzędzia; nie aktualizuj SDK przy okazji.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpProtocolTests|FullyQualifiedName~McpOutputTests|FullyQualifiedName~McpTokenBudgetTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 008/T2

- [ ] Przetestuj rzeczywiste tools/list przez transport, a nie tylko statyczne pole. Każde narzędzie ma schemaVersion1, wymagane pola i zgodny schema dla structuredContent. Uwzględnij error, partial, watch7 i snapshot8.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpProtocolTests|FullyQualifiedName~McpOutputTests|FullyQualifiedName~McpTokenBudgetTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 008/T3

- [ ] Sprawdź cały limit 32768B katalogu i 16384B domyślnego CallToolResult. Jeśli powtarzanie schematu przekracza katalog, zmniejsz redundantne opisy/schema bez usuwania wymaganych semantyk; nie zwiększaj limitu po cichu.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpProtocolTests|FullyQualifiedName~McpOutputTests|FullyQualifiedName~McpTokenBudgetTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 008/T4

- [ ] Zawężanie result per tool potraktuj jako dalszy krok wyłącznie gdy mieści się w budżecie i ma test zgodności; envelope z result free-form jest minimalnym zakresem tego planu.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpProtocolTests|FullyQualifiedName~McpOutputTests|FullyQualifiedName~McpTokenBudgetTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

## Testy i zakończenie

11/11 tools publikuje outputSchema; serializowany katalog mieści się w limicie; controlled outcomes nie stają się isError.

- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpProtocolTests|FullyQualifiedName~McpOutputTests|FullyQualifiedName~McpTokenBudgetTests' --verbosity minimal` → exit 0.
- [ ] `dotnet build SqlHarness.sln --no-restore -warnaserror` → exit 0.
- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName!~Integration' --verbosity minimal` → exit 0; zapisz passed/failed/skipped.
- [ ] Testy obejmują zachowanie właściwego adaptera/runnera, a nie tylko listę nazw lub stałą.
- [ ] `git diff --check` → exit 0.
- [ ] `git status --short` pokazuje wyłącznie autorskie zmiany w zakresie.
- [ ] Indeks zawiera status, commit i dowód; lokalne ścieżki istnieją lub są oznaczone jako nowe.
- [ ] Brak dowodu live/platformowego jest jawny.

Baseline audytu: 1895 Core/CLI passed; MCP 122 passed, 2 timeouty, 4 skipped. Plan005 diagnozuje timeouty. Wcześniejsza naprawa bezpieczeństwa może być gotowa do review przy udokumentowanej niezależnej awarii gate; nie ogłaszaj wtedy pełnego PASS.

## Warunki zatrzymania i utrzymanie

Jeśli SDK nie pozwala na schema przy obecnej rejestracji, udokumentuj sprawdzoną alternatywę przed przebudową hosta.

Jeśli krok wymaga rzeczywistego celu DB, sekretów lub wyjścia poza autoryzację, zatrzymaj ten krok i zapisz brak. Dokończ niezależne zadania. Nie oznaczaj całości DONE bez wszystkich wymaganych wyników.

Reviewer sprawdza bezpieczeństwo negatywnych przypadków, kompatybilność i prawdziwość dowodów. Po zmianie parsera/SDK/operacji wróć do tych testów. Małe commity typu fix/test/docs, bez push. Wymagane testy charakterystyczne muszą poprzedzać refaktoryzację.

