# Plan 009: Uczyń granicę statycznego safety widoczną dla klientów

Status: **TODO**  
Data: 2026-09-29. Baza: `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`.  
Priorytet: P2; nakład: M; ryzyko zmiany: LOW.  
Pokrycie: **A4; świadome ograniczenia funkcji PG**. Zależności: **007, 008**.

## Kontekst i instrukcja wykonania

Czytaj cały plan. Pracuj w izolowanym worktree, zachowaj cudze zmiany. Zapis planu nie jest wdrożeniem. Po ukończeniu zaktualizuj plans/README.md wraz z dowodem; nie pushuj ani nie publikuj bez zlecenia.

Drift check:
```powershell
git status --short
git diff --stat 8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b..HEAD -- "src/SqlHarness.Core/SqlValidation.cs" "src/SqlHarness.Core/Capabilities.cs" "src/SqlHarness.Mcp/McpOperationMapper.cs" "src/SqlHarness.Mcp/Tools/McpToolCatalog.cs" "tests/SqlHarness.Tests/Cli/CapabilitiesCommandTests.cs" "tests/SqlHarness.Tests/Cli/ValidateCommandTests.cs" "tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs" "README.md" "AGENTS.md" "docs/mcp.md" "docs/superpowers/specs/2026-09-26-postgres-safety-policy.md"
```

Zmiany poprzedników są oczekiwane: przeczytaj je i dopasuj plan przed kodowaniem. Niewyjaśniony rozjazd wymaga aktualizacji założeń, nie mechanicznego wklejania starego kodu.

Polityka PG opisuje efekty widoczne w tekście i brak transakcji READ ONLY; funkcje/widoki/operatorzy są poza dowodem AST. MCP opis query może sugerować gwarancję nieistnienia mutacji. To świadoma granica wykonania, a nie nowa luka klasyfikatora.

Punkt odniesienia z kodu/komendy:
```text
ObjectAndPermissionStatus = "unknown"
```

.NET8/C#/xUnit. Dopasuj wzorce do testów wymienionych w zakresie. Core pozostaje wspólnym miejscem safety; adaptery nie tworzą własnego klasyfikatora. Stosuj istniejące fake reader/session/module i syntetyczny SQLHARNESS_HOME. Nie czytaj rzeczywistych sekretów ani artefaktów użytkownika.

## Zakres

- `src/SqlHarness.Core/SqlValidation.cs`
- `src/SqlHarness.Core/Capabilities.cs`
- `src/SqlHarness.Mcp/McpOperationMapper.cs`
- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs`
- `tests/SqlHarness.Tests/Cli/CapabilitiesCommandTests.cs`
- `tests/SqlHarness.Tests/Cli/ValidateCommandTests.cs`
- `tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs`
- `README.md`
- `AGENTS.md`
- `docs/mcp.md`
- `docs/superpowers/specs/2026-09-26-postgres-safety-policy.md`

Dodatkowo: nowe pliki nazwane w krokach, dokumenty wynikowe plans/009-*.md oraz status w indeksie. Inne pliki wymagają jawnej korekty zakresu i uzasadnienia przed zmianą.

Poza zakresem: live DB, deploy, profile i hasła użytkownika, instalacja, push, globalne wyłączenie walidacji i niepowiązane refaktoryzacje. Zachowaj kody 0/2/3/4/5/6/7/8, legacy JSON i oba silniki. Pola addytywne tylko zgodnie z krokami.

## Zadania

### 009/T1

- [ ] Dodaj addytywne, wersjonowane metadane capabilities/validate: rodzaj analizy static-visible-effects, object/permissions unknown, hidden effects not verified. Zachowaj istniejące classification i exit codes.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~CapabilitiesCommandTests|FullyQualifiedName~ValidateCommandTests|FullyQualifiedName~McpToolSchemaTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 009/T2

- [ ] Zmień opisy query/validate na zgodne z rzeczywistą kontrolą. Wskaż rolę ograniczonych uprawnień DB bez twierdzenia, że SQLHarness ją tworzy lub weryfikuje.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~CapabilitiesCommandTests|FullyQualifiedName~ValidateCommandTests|FullyQualifiedName~McpToolSchemaTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 009/T3

- [ ] Testuj stałość pól w CLI/MCP i brak połączenia. Zachowaj denylist funkcji i brak efektów ubocznych przez sam preflight.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~CapabilitiesCommandTests|FullyQualifiedName~ValidateCommandTests|FullyQualifiedName~McpToolSchemaTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 009/T4

- [ ] Zapisz opcję zaostrzonego profilu w plans/009-strict-profile-assessment.md jako projekt: ograniczenia TEMP, funkcji SECURITY DEFINER, operatorów i zewnętrznych efektów. Żadnego wdrożenia READ ONLY lub przełączania ról w tym planie.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~CapabilitiesCommandTests|FullyQualifiedName~ValidateCommandTests|FullyQualifiedName~McpToolSchemaTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

## Testy i zakończenie

Klient potrafi odróżnić allowed syntaktyczne od sprawdzenia uprawnień; opisy i pola zgodne; dotychczasowe klasyfikacje zachowane.

- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~CapabilitiesCommandTests|FullyQualifiedName~ValidateCommandTests|FullyQualifiedName~McpToolSchemaTests' --verbosity minimal` → exit 0.
- [ ] `dotnet build SqlHarness.sln --no-restore -warnaserror` → exit 0.
- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName!~Integration' --verbosity minimal` → exit 0; zapisz passed/failed/skipped.
- [ ] Testy obejmują zachowanie właściwego adaptera/runnera, a nie tylko listę nazw lub stałą.
- [ ] `git diff --check` → exit 0.
- [ ] `git status --short` pokazuje wyłącznie autorskie zmiany w zakresie.
- [ ] Indeks zawiera status, commit i dowód; lokalne ścieżki istnieją lub są oznaczone jako nowe.
- [ ] Brak dowodu live/platformowego jest jawny.

Baseline audytu: 1895 Core/CLI passed; MCP 122 passed, 2 timeouty, 4 skipped. Plan005 diagnozuje timeouty. Wcześniejsza naprawa bezpieczeństwa może być gotowa do review przy udokumentowanej niezależnej awarii gate; nie ogłaszaj wtedy pełnego PASS.

## Warunki zatrzymania i utrzymanie

Nie zwężaj prefiksów lo_/dblink/pg_advisory_ bez nowego dowodu tożsamości funkcji. Nie traktuj kontroli katalogowej jako pełnego rozwiązania ukrytych efektów.

Jeśli krok wymaga rzeczywistego celu DB, sekretów lub wyjścia poza autoryzację, zatrzymaj ten krok i zapisz brak. Dokończ niezależne zadania. Nie oznaczaj całości DONE bez wszystkich wymaganych wyników.

Reviewer sprawdza bezpieczeństwo negatywnych przypadków, kompatybilność i prawdziwość dowodów. Po zmianie parsera/SDK/operacji wróć do tych testów. Małe commity typu fix/test/docs, bez push. Wymagane testy charakterystyczne muszą poprzedzać refaktoryzację.

