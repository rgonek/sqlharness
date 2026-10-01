# Plan 011: Zaplanuj i dodaj wąskie rozszerzenia bezpiecznej składni

Status: **DONE** (branch `feat/plan-011-safe-sql-extensions`, `0fad2a8..e234f4c` kod, docs do `c300cf6`; T5 ANALYZE DESIGN COMPLETE, nie IMPLEMENTED; dowód: [011-safe-sql-extensions-proof](011-safe-sql-extensions-proof.md))
Data: 2026-09-29. Baza: `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`.
Priorytet: P2; nakład: L; ryzyko zmiany: HIGH.
Pokrycie: **nadmiarowe blokady SET, table variables, PG TEMP TRUNCATE i ANALYZE**. Zależności: **007, 009**.

## Kontekst i instrukcja wykonania

Czytaj cały plan. Pracuj w izolowanym worktree, zachowaj cudze zmiany. Zapis planu nie jest wdrożeniem. Po ukończeniu zaktualizuj plans/README.md wraz z dowodem; nie pushuj ani nie publikuj bez zlecenia.

Drift check:
```powershell
git status --short
git diff --stat 8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b..HEAD -- "src/SqlHarness.Core/SqlSafety.cs" "src/SqlHarness.Core/Postgres/PostgresSafetyClassifier.cs" "src/SqlHarness.Core/Postgres/PostgresDocument.cs" "src/SqlHarness.Core/SqlValidation.cs" "src/SqlHarness.Core/Capabilities.cs" "tests/SqlHarness.Tests/SqlSafetyTests.cs" "tests/SqlHarness.Tests/SqlParameterReferenceValidatorTests.cs" "tests/SqlHarness.Tests/Postgres/PostgresSafetyTests.cs" "AGENTS.md"
```

Zmiany poprzedników są oczekiwane: przeczytaj je i dopasuj plan przed kodowaniem. Niewyjaśniony rozjazd wymaga aktualizacji założeń, nie mechanicznego wklejania starego kodu.

Offline: scalar DECLARE działa, SET zmiennej i table variable są unsupported. PG CREATE TEMP + TRUNCATE jest unsupported; ANALYZE daje parse_error. Zwykłe CTE SELECT działają. Table variable i ANALYZE to znane granice, nie przypadkowe regresje.

Punkt odniesienia z kodu/komendy:
```text
case DeclareVariableStatement declare:
```

.NET8/C#/xUnit. Dopasuj wzorce do testów wymienionych w zakresie. Core pozostaje wspólnym miejscem safety; adaptery nie tworzą własnego klasyfikatora. Stosuj istniejące fake reader/session/module i syntetyczny SQLHARNESS_HOME. Nie czytaj rzeczywistych sekretów ani artefaktów użytkownika.

## Zakres

- `src/SqlHarness.Core/SqlSafety.cs`
- `src/SqlHarness.Core/Postgres/PostgresSafetyClassifier.cs`
- `src/SqlHarness.Core/Postgres/PostgresDocument.cs`
- `src/SqlHarness.Core/SqlValidation.cs`
- `src/SqlHarness.Core/Capabilities.cs`
- `tests/SqlHarness.Tests/SqlSafetyTests.cs`
- `tests/SqlHarness.Tests/SqlParameterReferenceValidatorTests.cs`
- `tests/SqlHarness.Tests/Postgres/PostgresSafetyTests.cs`
- `AGENTS.md`

Dodatkowo: nowe pliki nazwane w krokach, dokumenty wynikowe plans/011-*.md oraz status w indeksie. Inne pliki wymagają jawnej korekty zakresu i uzasadnienia przed zmianą.

### Korekty zakresu (scope corrections)

Dopisane w końcowej fali poprawek (2026-10-01). Poniższe pliki leżą poza listą powyżej i zostały zmienione za zgodą kontrolera; żaden nowy plik źródłowy ani testowy nie powstał.

- `README.md` — dwa fragmenty wymieniające instrukcje „session-only" opisywały stan sprzed planu 011. Pozostawienie ich byłoby nieprawdziwą dokumentacją dla użytkownika (T6 runda 1; fala końcowa: pisownia `pg_temp.<name>`, ograniczenie `search_path`, zmienna tablicowa z `--setup`).
- `docs/superpowers/specs/2026-09-26-postgres-safety-policy.md`, `docs/superpowers/specs/2026-09-10-postgres-engine-design.md`, `docs/superpowers/plans/2026-09-10-postgres-engine.md`, `plans/009-strict-profile-assessment.md` — cztery starsze dokumenty nadal podawały prefiks `pg_temp_` jako dowód celu lokalnego dla sesji. Dwie specyfikacje poprawiono w miejscu z notą „zmienione planem 011", dwa historyczne plany dostały datowaną notę (T6).
- `tests/SqlHarness.Tests/Fixtures/AgentWorkflow/byte-budgets.json` — dokładny pin bajtów, który czyta `AgentWorkflowTests`. Rośnie razem z tekstem capabilities: 5710 → 6219 → 6887 → 7100 bajtów UTF-8. Sufit 8192 nie został podniesiony. Sam plik `AgentWorkflowTests.cs` nie był zmieniany.
- `tests/SqlHarness.Tests/Cli/CapabilitiesCommandTests.cs` — testy wiążące każde zdanie `sessionTempStatements` z werdyktem prawdziwego klasyfikatora (T6, fala końcowa).
- `tests/SqlHarness.Tests/QueryTests.cs`, `tests/SqlHarness.Tests/MeasureTests.cs`, `tests/SqlHarness.Tests/CompareTests.cs`, `tests/SqlHarness.Tests/Postgres/PostgresQueryTests.cs`, `tests/SqlHarness.Tests/Postgres/PostgresBenchmarkTests.cs` — testy end-to-end przez `SqlHarnessModule` (fala końcowa, M1). Dopisane do istniejących klas, bo ich fake session/reader są prywatne dla klasy; nowa infrastruktura testowa nie powstała.

Poza zakresem: live DB, deploy, profile i hasła użytkownika, instalacja, push, globalne wyłączenie walidacji i niepowiązane refaktoryzacje. Zachowaj kody 0/2/3/4/5/6/7/8, legacy JSON i oba silniki. Pola addytywne tylko zgodnie z krokami.

## Zadania

### 011/T1

- [x] W plans/011-syntax-contract.md zapisz macierz dozwolonych AST i negatywnych przypadków przed zmianą classifiera. Dziel wdrożenie na niezależne podzadania; każde ma osobny test regresji.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~SqlSafetyTests|FullyQualifiedName~SqlParameterReferenceValidatorTests|FullyQualifiedName~PostgresSafetyTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 011/T2

- [x] T-SQL SET: dopuść wyłącznie rozpoznane przypisanie do lokalnej zmiennej skalarnej, analizując RHS przez istniejące kontrole external/stateful/cross-db. Nie włączaj SET opcji sesji/transakcji.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~SqlSafetyTests|FullyQualifiedName~SqlParameterReferenceValidatorTests|FullyQualifiedName~PostgresSafetyTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 011/T3

- [x] T-SQL table variables: zaprojektuj identyfikację lokalnego celu dla DECLARE/SELECT/DML/OUTPUT, scope batcha i referencje parametrów. Dopiero pełna analiza celu może zezwalać na DML; nie traktuj dowolnego @name jako lokalnego.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~SqlSafetyTests|FullyQualifiedName~SqlParameterReferenceValidatorTests|FullyQualifiedName~PostgresSafetyTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 011/T4

- [x] PG TRUNCATE: dopuść tylko wszystkie cele udowodnione jako należące do bieżącej sesji; odrzuć persistent/mixed targets i CASCADE, rozstrzygnij RESTART IDENTITY w macierzy zamiast pozwalać domyślnie. Nie ufaj dowolnemu prefiksowi pg_temp_ jako dowodowi bieżącej sesji.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~SqlSafetyTests|FullyQualifiedName~SqlParameterReferenceValidatorTests|FullyQualifiedName~PostgresSafetyTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 011/T5

- [x] PG ANALYZE: najpierw sprawdź rzeczywisty AST parsera offline. Jeśli nadal brak obsługi, dostarcz plans/011-analyze-parser-spike.md z zakresem zmiany parsera i testami akceptacji; status tego podzadania DESIGN COMPLETE, nie IMPLEMENTED. Bez regex bypass i bez aktualizacji zależności bez oceny regresji.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~SqlSafetyTests|FullyQualifiedName~SqlParameterReferenceValidatorTests|FullyQualifiedName~PostgresSafetyTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 011/T6

- [x] Zaktualizuj capabilities i AGENTS wyłącznie dla faktycznie obsługiwanych konstrukcji. Zachowaj odmowy dynamic SQL, persistent DDL, cross-database i efekty funkcji.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~SqlSafetyTests|FullyQualifiedName~SqlParameterReferenceValidatorTests|FullyQualifiedName~PostgresSafetyTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

## Testy i zakończenie

Każde wdrożone rozszerzenie ma pozytywny i negatywny test; wszystkie dotychczasowe odmowy istotnych efektów pozostają. ANALYZE ma jawny wynik wdrożenie albo projekt zależności.

- [x] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~SqlSafetyTests|FullyQualifiedName~SqlParameterReferenceValidatorTests|FullyQualifiedName~PostgresSafetyTests' --verbosity minimal` → exit 0.
- [x] `dotnet build SqlHarness.sln --no-restore -warnaserror` → exit 0.
- [x] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName!~Integration' --verbosity minimal` → exit 0; zapisz passed/failed/skipped.
- [x] Testy obejmują zachowanie właściwego adaptera/runnera, a nie tylko listę nazw lub stałą.
- [x] `git diff --check` → exit 0.
- [x] `git status --short` pokazuje wyłącznie autorskie zmiany w zakresie.
- [x] Indeks zawiera status, commit i dowód; lokalne ścieżki istnieją lub są oznaczone jako nowe.
- [x] Brak dowodu live/platformowego jest jawny.

Baseline audytu: 1895 Core/CLI passed; MCP 122 passed, 2 timeouty, 4 skipped. Plan005 diagnozuje timeouty. Wcześniejsza naprawa bezpieczeństwa może być gotowa do review przy udokumentowanej niezależnej awarii gate; nie ogłaszaj wtedy pełnego PASS.

## Warunki zatrzymania i utrzymanie

Jeżeli parser nie reprezentuje efektów potrzebnych do rozstrzygnięcia, zachowaj odmowę i opisz zależność. Nie rozszerzaj allowlist tylko na podstawie słowa kluczowego.

Jeśli krok wymaga rzeczywistego celu DB, sekretów lub wyjścia poza autoryzację, zatrzymaj ten krok i zapisz brak. Dokończ niezależne zadania. Nie oznaczaj całości DONE bez wszystkich wymaganych wyników.

Reviewer sprawdza bezpieczeństwo negatywnych przypadków, kompatybilność i prawdziwość dowodów. Po zmianie parsera/SDK/operacji wróć do tych testów. Małe commity typu fix/test/docs, bez push. Wymagane testy charakterystyczne muszą poprzedzać refaktoryzację.
