# Plan 006: Ogranicz pamięć i czas porównywania wyników

Status: **TODO**  
Data: 2026-09-29. Baza: `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`.  
Priorytet: P1; nakład: L; ryzyko zmiany: HIGH.  
Pokrycie: **R2**. Zależności: **005**.

## Kontekst i instrukcja wykonania

Czytaj cały plan. Pracuj w izolowanym worktree, zachowaj cudze zmiany. Zapis planu nie jest wdrożeniem. Po ukończeniu zaktualizuj plans/README.md wraz z dowodem; nie pushuj ani nie publikuj bez zlecenia.

Drift check:
```powershell
git status --short
git diff --stat 8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b..HEAD -- "src/SqlHarness.Core/ResultEquivalence.cs" "src/SqlHarness.Core/CompareCellRunner.cs" "src/SqlHarness.Core/BenchmarkCollector.cs" "src/SqlHarness.Core/Postgres/PostgresBenchmark.cs" "src/SqlHarness.Core/BenchmarkRunner.cs" "src/SqlHarness.Core/Capabilities.cs" "tests/SqlHarness.Tests/ResultEquivalenceTests.cs" "tests/SqlHarness.Tests/CompareTests.cs" "tests/SqlHarness.Mcp.Tests/McpCancellationTests.cs"
```

Zmiany poprzedników są oczekiwane: przeczytaj je i dopasuj plan przed kodowaniem. Niewyjaśniony rozjazd wymaga aktualizacji założeń, nie mechanicznego wklejania starego kodu.

CompareCellRunner zachowuje wszystkie fingerprinty; ResultComparer robi iloczyn baseline×candidate bez tokenu. Limit 1M jest na przebieg, repeat do100. 200M fingerprintów i 10k par są dozwolonym górnym zakresem, nie zmierzonym obciążeniem.

Punkt odniesienia z kodu/komendy:
```text
foreach (var left in baseline)
```

.NET8/C#/xUnit. Dopasuj wzorce do testów wymienionych w zakresie. Core pozostaje wspólnym miejscem safety; adaptery nie tworzą własnego klasyfikatora. Stosuj istniejące fake reader/session/module i syntetyczny SQLHARNESS_HOME. Nie czytaj rzeczywistych sekretów ani artefaktów użytkownika.

## Zakres

- `src/SqlHarness.Core/ResultEquivalence.cs`
- `src/SqlHarness.Core/CompareCellRunner.cs`
- `src/SqlHarness.Core/BenchmarkCollector.cs`
- `src/SqlHarness.Core/Postgres/PostgresBenchmark.cs`
- `src/SqlHarness.Core/BenchmarkRunner.cs`
- `src/SqlHarness.Core/Capabilities.cs`
- `tests/SqlHarness.Tests/ResultEquivalenceTests.cs`
- `tests/SqlHarness.Tests/CompareTests.cs`
- `tests/SqlHarness.Mcp.Tests/McpCancellationTests.cs`

Dodatkowo: nowe pliki nazwane w krokach, dokumenty wynikowe plans/006-*.md oraz status w indeksie. Inne pliki wymagają jawnej korekty zakresu i uzasadnienia przed zmianą.

Poza zakresem: live DB, deploy, profile i hasła użytkownika, instalacja, push, globalne wyłączenie walidacji i niepowiązane refaktoryzacje. Zachowaj kody 0/2/3/4/5/6/7/8, legacy JSON i oba silniki. Pola addytywne tylko zgodnie z krokami.

## Zadania

### 006/T1

- [ ] Dodaj małe testy charakterystyczne wszystkich trybów: kolejność, duplikaty, różne schematy, puste wyniki, niestabilne powtórzenia, maksimum kierunkowych różnic dla wszystkich par. Zachowaj prostą implementację referencyjną wyłącznie w testach.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~ResultEquivalenceTests|FullyQualifiedName~CompareTests|FullyQualifiedName~McpCancellationTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 006/T2

- [ ] Zdefiniuj w plans/006-resource-contract.md jawny budżet całej operacji, sposób rozliczenia fingerprintów/histogramów i odmowę bez obcinania danych. Zachowaj limit per-run i nie mieszaj MaxRows prezentacji z equivalence. Uwzględnij matrix reset budżetu per cell oraz wspólny deadline.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~ResultEquivalenceTests|FullyQualifiedName~CompareTests|FullyQualifiedName~McpCancellationTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 006/T3

- [ ] Przekaż CancellationToken do faz CPU i sprawdzaj go w ograniczonych odstępach pracy. Wprowadź zwartą reprezentację i cache histogramu per unikalny wynik; identyczne przebiegi mogą współdzielić reprezentację. Nie zmieniaj SHA/semantyki hash bez wersjonowania.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~ResultEquivalenceTests|FullyQualifiedName~CompareTests|FullyQualifiedName~McpCancellationTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 006/T4

- [ ] Zweryfikuj nowy algorytm przeciw oracle na deterministycznych małych zbiorach; dodaj próby limitu przy małym wstrzykniętym budżecie i anulowania już rozpoczętego porównania. Zmierz alokacje na ograniczonym syntetycznym zestawie, bez prób 200M wierszy.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~ResultEquivalenceTests|FullyQualifiedName~CompareTests|FullyQualifiedName~McpCancellationTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 006/T5

- [ ] Zachowaj artefakty ukończonych komórek matrix i kontrolowane błędy. Uzupełnij capabilities o rzeczywiście egzekwowany budżet.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~ResultEquivalenceTests|FullyQualifiedName~CompareTests|FullyQualifiedName~McpCancellationTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

## Testy i zakończenie

Oracle i nowy kod zgodne dla wszystkich trybów; budżet całkowity nieprzekraczany; anulowanie fazy CPU obserwowane; nigdy equivalent=true na obciętych danych.

- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~ResultEquivalenceTests|FullyQualifiedName~CompareTests|FullyQualifiedName~McpCancellationTests' --verbosity minimal` → exit 0.
- [ ] `dotnet build SqlHarness.sln --no-restore -warnaserror` → exit 0.
- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName!~Integration' --verbosity minimal` → exit 0; zapisz passed/failed/skipped.
- [ ] Testy obejmują zachowanie właściwego adaptera/runnera, a nie tylko listę nazw lub stałą.
- [ ] `git diff --check` → exit 0.
- [ ] `git status --short` pokazuje wyłącznie autorskie zmiany w zakresie.
- [ ] Indeks zawiera status, commit i dowód; lokalne ścieżki istnieją lub są oznaczone jako nowe.
- [ ] Brak dowodu live/platformowego jest jawny.

Baseline audytu: 1895 Core/CLI passed; MCP 122 passed, 2 timeouty, 4 skipped. Plan005 diagnozuje timeouty. Wcześniejsza naprawa bezpieczeństwa może być gotowa do review przy udokumentowanej niezależnej awarii gate; nie ogłaszaj wtedy pełnego PASS.

## Warunki zatrzymania i utrzymanie

Jeśli dokładnych maksymalnych liczników nie da się zachować proponowaną optymalizacją, zachowaj semantykę i ogranicz nakład limitem; nie zastępuj ich porównaniem tylko pierwszej pary.

Jeśli krok wymaga rzeczywistego celu DB, sekretów lub wyjścia poza autoryzację, zatrzymaj ten krok i zapisz brak. Dokończ niezależne zadania. Nie oznaczaj całości DONE bez wszystkich wymaganych wyników.

Reviewer sprawdza bezpieczeństwo negatywnych przypadków, kompatybilność i prawdziwość dowodów. Po zmianie parsera/SDK/operacji wróć do tych testów. Małe commity typu fix/test/docs, bez push. Wymagane testy charakterystyczne muszą poprzedzać refaktoryzację.

