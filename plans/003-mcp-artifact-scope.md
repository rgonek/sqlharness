# Plan 003: Powiąż dostęp do artefaktów z zakresem MCP

Status: **TODO**  
Data: 2026-09-29. Baza: `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`.  
Priorytet: P1; nakład: L; ryzyko zmiany: HIGH.  
Pokrycie: **S2; przestrzeń nazw snapshotów**. Zależności: **001, 002**.

## Kontekst i instrukcja wykonania

Czytaj cały plan. Pracuj w izolowanym worktree, zachowaj cudze zmiany. Zapis planu nie jest wdrożeniem. Po ukończeniu zaktualizuj plans/README.md wraz z dowodem; nie pushuj ani nie publikuj bez zlecenia.

Drift check:
```powershell
git status --short
git diff --stat 8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b..HEAD -- "src/SqlHarness.Mcp/McpScope.cs" "src/SqlHarness.Mcp/McpOperationMapper.cs" "src/SqlHarness.Mcp/Tools/McpToolCatalog.cs" "src/SqlHarness.Core/ArtifactReader.cs" "src/SqlHarness.Core/Artifacts.cs" "src/SqlHarness.Core/Contracts.cs" "src/SqlHarness.Core/SqlHarnessModule.cs" "src/SqlHarness.Core/SnapshotStore.cs" "src/SqlHarness.Core/SnapshotRunner.cs" "tests/SqlHarness.Mcp.Tests/McpScopeTests.cs" "tests/SqlHarness.Mcp.Tests/McpMappingTests.cs" "tests/SqlHarness.Tests/ArtifactReaderTests.cs" "tests/SqlHarness.Tests/SnapshotStoreTests.cs"
```

Zmiany poprzedników są oczekiwane: przeczytaj je i dopasuj plan przed kodowaniem. Niewyjaśniony rozjazd wymaga aktualizacji założeń, nie mechanicznego wklejania starego kodu.

ReadArtifactSection przyjmuje scope, ale czyta globalny CompareDir bez sprawdzenia właściciela. Syntetyczny raport innej bazy został zwrócony z isError=false. Manifest nie ma tożsamości zakresu; SnapshotDocument także nie zapisuje targetu.

Punkt odniesienia z kodu/komendy:
```text
return ArtifactReader.ReadSection(SqlHarnessPaths.CompareDir, id ?? string.Empty, section);
```

.NET8/C#/xUnit. Dopasuj wzorce do testów wymienionych w zakresie. Core pozostaje wspólnym miejscem safety; adaptery nie tworzą własnego klasyfikatora. Stosuj istniejące fake reader/session/module i syntetyczny SQLHARNESS_HOME. Nie czytaj rzeczywistych sekretów ani artefaktów użytkownika.

## Zakres

- `src/SqlHarness.Mcp/McpScope.cs`
- `src/SqlHarness.Mcp/McpOperationMapper.cs`
- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs`
- `src/SqlHarness.Core/ArtifactReader.cs`
- `src/SqlHarness.Core/Artifacts.cs`
- `src/SqlHarness.Core/Contracts.cs`
- `src/SqlHarness.Core/SqlHarnessModule.cs`
- `src/SqlHarness.Core/SnapshotStore.cs`
- `src/SqlHarness.Core/SnapshotRunner.cs`
- `tests/SqlHarness.Mcp.Tests/McpScopeTests.cs`
- `tests/SqlHarness.Mcp.Tests/McpMappingTests.cs`
- `tests/SqlHarness.Tests/ArtifactReaderTests.cs`
- `tests/SqlHarness.Tests/SnapshotStoreTests.cs`

Dodatkowo: nowe pliki nazwane w krokach, dokumenty wynikowe plans/003-*.md oraz status w indeksie. Inne pliki wymagają jawnej korekty zakresu i uzasadnienia przed zmianą.

Poza zakresem: live DB, deploy, profile i hasła użytkownika, instalacja, push, globalne wyłączenie walidacji i niepowiązane refaktoryzacje. Zachowaj kody 0/2/3/4/5/6/7/8, legacy JSON i oba silniki. Pola addytywne tylko zgodnie z krokami.

## Zadania

### 003/T1

- [ ] Najpierw zapisz w plans/003-scope-contract.md model zakresu: profil + kanoniczny zestaw zmiennych + resolved engine/server/database, bez haseł. Ustal trwałość po restarcie i właściciela dla matrix/param-set. Wybrany wariant: wersjonowane metadane właściciela tworzone przez zaufaną ścieżkę zapisu; brak właściciela oznacza odmowę MCP. Lokalny pisarz z uprawnieniami procesu jest poza granicą integralności — nie obiecuj odporności na fałszowanie manifestu.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpScopeTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~ArtifactReaderTests|FullyQualifiedName~SnapshotStoreTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 003/T2

- [ ] Dodaj testy własnego i obcego zakresu, tej samej nazwy DB na innym serwerze, innych vars/profilu oraz braku owner w starym artefakcie. Zachowaj istniejący offline CLI reader; MCP nie może sam nadać sobie prawa do starego artefaktu na podstawie jego nazwy.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpScopeTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~ArtifactReaderTests|FullyQualifiedName~SnapshotStoreTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 003/T3

- [ ] Przenieś kontekst właściciela z frozen scope przez operację do publikacji i readera. Sprawdź właściciela przed projekcją raportu. Zachowaj summary/metrics/operators, brak raw SQL i wartości, atomic publish, budżety i bezpieczne błędy.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpScopeTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~ArtifactReaderTests|FullyQualifiedName~SnapshotStoreTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 003/T4

- [ ] Zdefiniuj osobno snapshot capture/diff: MCP wymaga właściciela i odmowy obcego zakresu, CLI zachowuje dotychczasową świadomą pracę po nazwie. Nie migruj ani nie nadpisuj automatycznie istniejących plików. Testuj brak overwrite i brak ujawnienia komórek.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpScopeTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~ArtifactReaderTests|FullyQualifiedName~SnapshotStoreTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 003/T5

- [ ] Uzupełnij docs/mcp.md i specyfikację MCP o faktyczny model zakresu i kompatybilność legacy; dopisz te dwa pliki do listy scope w trakcie wdrożenia.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpScopeTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~ArtifactReaderTests|FullyQualifiedName~SnapshotStoreTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

## Testy i zakończenie

Obcy zakres i legacy bez właściciela odrzucone przed zwróceniem danych; własny artefakt działa po restarcie według kontraktu; legacy CLI działa; snapshot nie ujawnia komórek.

- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpScopeTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~ArtifactReaderTests|FullyQualifiedName~SnapshotStoreTests' --verbosity minimal` → exit 0.
- [ ] `dotnet build SqlHarness.sln --no-restore -warnaserror` → exit 0.
- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName!~Integration' --verbosity minimal` → exit 0; zapisz passed/failed/skipped.
- [ ] Testy obejmują zachowanie właściwego adaptera/runnera, a nie tylko listę nazw lub stałą.
- [ ] `git diff --check` → exit 0.
- [ ] `git status --short` pokazuje wyłącznie autorskie zmiany w zakresie.
- [ ] Indeks zawiera status, commit i dowód; lokalne ścieżki istnieją lub są oznaczone jako nowe.
- [ ] Brak dowodu live/platformowego jest jawny.

Baseline audytu: 1895 Core/CLI passed; MCP 122 passed, 2 timeouty, 4 skipped. Plan005 diagnozuje timeouty. Wcześniejsza naprawa bezpieczeństwa może być gotowa do review przy udokumentowanej niezależnej awarii gate; nie ogłaszaj wtedy pełnego PASS.

## Warunki zatrzymania i utrzymanie

Nie używać samej nazwy DB ani nazwy folderu jako autoryzacji. Jeżeli threading kontekstu wymaga innego runnera, uzupełnij scope planu i uzasadnienie przed zmianą; nie pomijaj matrix/param-set.

Jeśli krok wymaga rzeczywistego celu DB, sekretów lub wyjścia poza autoryzację, zatrzymaj ten krok i zapisz brak. Dokończ niezależne zadania. Nie oznaczaj całości DONE bez wszystkich wymaganych wyników.

Reviewer sprawdza bezpieczeństwo negatywnych przypadków, kompatybilność i prawdziwość dowodów. Po zmianie parsera/SDK/operacji wróć do tych testów. Małe commity typu fix/test/docs, bez push. Wymagane testy charakterystyczne muszą poprzedzać refaktoryzację.

