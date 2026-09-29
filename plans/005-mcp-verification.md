# Plan 005: Ustal przyczyny timeoutów testów MCP

Status: **TODO**
Data: 2026-09-29. Baza: `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`.
Priorytet: P2; nakład: M; ryzyko zmiany: LOW.
Pokrycie: **T1**. Zależności: **001, 004**.

## Kontekst i instrukcja wykonania

Czytaj cały plan. Pracuj w izolowanym worktree, zachowaj cudze zmiany. Zapis planu nie jest wdrożeniem. Po ukończeniu zaktualizuj plans/README.md wraz z dowodem; nie pushuj ani nie publikuj bez zlecenia.

Drift check:
```powershell
git status --short
git diff --stat 8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b..HEAD -- "tests/SqlHarness.Mcp.Tests/McpLifecycleTests.cs" "tests/SqlHarness.Mcp.Tests/McpStdioProcessTests.cs" "tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj" "src/SqlHarness.Mcp/McpExecutionGate.cs" "src/SqlHarness.Mcp/McpEofShutdown.cs" ".github/workflows/ci.yml"
```

Zmiany poprzedników są oczekiwane: przeczytaj je i dopasuj plan przed kodowaniem. Niewyjaśniony rozjazd wymaga aktualizacji założeń, nie mechanicznego wklejania starego kodu.

Pełny audyt: Core/CLI 1895 pass; MCP 122 pass, 2 timeouty, 4 skip. Progress timeout 15 s w WaitForProgressCountAsync; osobno test przeszedł. Publish smoke utknął w PublishAsync po około 6 minutach, przed uruchomieniem binarki.

Punkt odniesienia z kodu/komendy:
```text
await WaitForProgressCountAsync(collector, 1, cts.Token)
```

.NET8/C#/xUnit. Dopasuj wzorce do testów wymienionych w zakresie. Core pozostaje wspólnym miejscem safety; adaptery nie tworzą własnego klasyfikatora. Stosuj istniejące fake reader/session/module i syntetyczny SQLHARNESS_HOME. Nie czytaj rzeczywistych sekretów ani artefaktów użytkownika.

## Zakres

- `tests/SqlHarness.Mcp.Tests/McpLifecycleTests.cs`
- `tests/SqlHarness.Mcp.Tests/McpStdioProcessTests.cs`
- `tests/SqlHarness.Mcp.Tests/SqlHarness.Mcp.Tests.csproj`
- `src/SqlHarness.Mcp/McpExecutionGate.cs`
- `src/SqlHarness.Mcp/McpEofShutdown.cs`
- `.github/workflows/ci.yml`

Dodatkowo: nowe pliki nazwane w krokach, dokumenty wynikowe plans/005-*.md oraz status w indeksie. Inne pliki wymagają jawnej korekty zakresu i uzasadnienia przed zmianą.

Poza zakresem: live DB, deploy, profile i hasła użytkownika, instalacja, push, globalne wyłączenie walidacji i niepowiązane refaktoryzacje. Zachowaj kody 0/2/3/4/5/6/7/8, legacy JSON i oba silniki. Pola addytywne tylko zgodnie z krokami.

## Zadania

### 005/T1

- [ ] Odtwórz osobno progress i publish na bieżącym HEAD, zapisując etap, czas, exit i przyczynę anulowania bez SQL/sekretów. Jeśli problem nie występuje, wykonaj jeden pełny zestaw; nie uznawaj pojedynczego pass za wyjaśnienie.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpLifecycleTests|FullyQualifiedName~McpStdioProcessTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 005/T2

- [ ] Rozdziel token operacji, życia klienta i oczekiwania na progress; sprawdź rejestrację odbiornika zanim nadejdzie odpowiedź. Napraw wyłącznie ustaloną przyczynę; nie dodawaj ślepych retries.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpLifecycleTests|FullyQualifiedName~McpStdioProcessTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 005/T3

- [ ] W PublishAsync zapewnij równoległe opróżnianie stdout/stderr, kontrolę exit, rozróżnienie restore/publish/launch i zakończenie własnego drzewa procesów po timeout. Nie zatrzymuj procesów innych sesji.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpLifecycleTests|FullyQualifiedName~McpStdioProcessTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 005/T4

- [ ] Uruchom pełny zestaw i zapisz rzeczywiste wyniki w plans/005-verification-evidence.md. Timeout środowiska raportuj osobno od błędu aplikacji. Nie zwiększaj limitów bez pomiaru uzasadniającego zmianę.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpLifecycleTests|FullyQualifiedName~McpStdioProcessTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

## Testy i zakończenie

Pełny zestaw przechodzi w docelowym środowisku; testy mają dowód poprawnego cleanup i diagnostykę etapu. Jeśli blokuje infrastruktura, status BLOCKED z dowodem, nigdy DONE.

- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpLifecycleTests|FullyQualifiedName~McpStdioProcessTests' --verbosity minimal` → exit 0.
- [ ] `dotnet build SqlHarness.sln --no-restore -warnaserror` → exit 0.
- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName!~Integration' --verbosity minimal` → exit 0; zapisz passed/failed/skipped.
- [ ] Testy obejmują zachowanie właściwego adaptera/runnera, a nie tylko listę nazw lub stałą.
- [ ] `git diff --check` → exit 0.
- [ ] `git status --short` pokazuje wyłącznie autorskie zmiany w zakresie.
- [ ] Indeks zawiera status, commit i dowód; lokalne ścieżki istnieją lub są oznaczone jako nowe.
- [ ] Brak dowodu live/platformowego jest jawny.

Baseline audytu: 1895 Core/CLI passed; MCP 122 passed, 2 timeouty, 4 skipped. Plan005 diagnozuje timeouty. Wcześniejsza naprawa bezpieczeństwa może być gotowa do review przy udokumentowanej niezależnej awarii gate; nie ogłaszaj wtedy pełnego PASS.

## Warunki zatrzymania i utrzymanie

Nie oznaczaj niewykonanych RID jako sprawdzonych. Zmiana produkcyjnego lifecycle wymaga odtworzenia błędu, a nie samego timeoutu testu.

Jeśli krok wymaga rzeczywistego celu DB, sekretów lub wyjścia poza autoryzację, zatrzymaj ten krok i zapisz brak. Dokończ niezależne zadania. Nie oznaczaj całości DONE bez wszystkich wymaganych wyników.

Reviewer sprawdza bezpieczeństwo negatywnych przypadków, kompatybilność i prawdziwość dowodów. Po zmianie parsera/SDK/operacji wróć do tych testów. Małe commity typu fix/test/docs, bez push. Wymagane testy charakterystyczne muszą poprzedzać refaktoryzację.
