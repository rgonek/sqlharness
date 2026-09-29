# Plan 004: Obejmij inspect wspólną blokadą operacji DB

Status: **TODO**
Data: 2026-09-29. Baza: `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`.
Priorytet: P1; nakład: S; ryzyko zmiany: MED.
Pokrycie: **R1**. Zależności: **001**.

## Kontekst i instrukcja wykonania

Czytaj cały plan. Pracuj w izolowanym worktree, zachowaj cudze zmiany. Zapis planu nie jest wdrożeniem. Po ukończeniu zaktualizuj plans/README.md wraz z dowodem; nie pushuj ani nie publikuj bez zlecenia.

Drift check:
```powershell
git status --short
git diff --stat 8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b..HEAD -- "src/SqlHarness.Mcp/Tools/McpToolCatalog.cs" "src/SqlHarness.Mcp/McpExecutionGate.cs" "tests/SqlHarness.Mcp.Tests/McpLifecycleTests.cs" "tests/SqlHarness.Mcp.Tests/McpCancellationTests.cs" "docs/mcp.md"
```

Zmiany poprzedników są oczekiwane: przeczytaj je i dopasuj plan przed kodowaniem. Niewyjaśniony rozjazd wymaga aktualizacji założeń, nie mechanicznego wklejania starego kodu.

InspectAsync korzysta z RunAsync bez gate, chociaż mapper tworzy rzeczywiste operacje DB. RunDbAsync zajmuje blokadę. Test utrwala błędny podział pięciu narzędzi i traktuje inspect jako discovery.

Punkt odniesienia z kodu/komendy:
```text
_gate.TryEnterDb()
```

.NET8/C#/xUnit. Dopasuj wzorce do testów wymienionych w zakresie. Core pozostaje wspólnym miejscem safety; adaptery nie tworzą własnego klasyfikatora. Stosuj istniejące fake reader/session/module i syntetyczny SQLHARNESS_HOME. Nie czytaj rzeczywistych sekretów ani artefaktów użytkownika.

## Zakres

- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs`
- `src/SqlHarness.Mcp/McpExecutionGate.cs`
- `tests/SqlHarness.Mcp.Tests/McpLifecycleTests.cs`
- `tests/SqlHarness.Mcp.Tests/McpCancellationTests.cs`
- `docs/mcp.md`

Dodatkowo: nowe pliki nazwane w krokach, dokumenty wynikowe plans/004-*.md oraz status w indeksie. Inne pliki wymagają jawnej korekty zakresu i uzasadnienia przed zmianą.

Poza zakresem: live DB, deploy, profile i hasła użytkownika, instalacja, push, globalne wyłączenie walidacji i niepowiązane refaktoryzacje. Zachowaj kody 0/2/3/4/5/6/7/8, legacy JSON i oba silniki. Pola addytywne tylko zgodnie z krokami.

## Zadania

### 004/T1

- [ ] Dodaj deterministyczne testy z RecordingModule i TaskCompletionSource: aktywne query blokuje inspect, inspect blokuje query i drugi inspect; capabilities/validate pozostają dostępne. Bez sleeps i bez DB.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpLifecycleTests|FullyQualifiedName~McpCancellationTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 004/T2

- [ ] Skieruj wszystkie kind inspect przez RunDbAsync i uwzględnij inspect w IsDbTool. Zachowaj timeouts per kind i istniejący procesowy limit; nowe argumenty per-call tylko jeśli potrzebne, wtedy z testem schematu.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpLifecycleTests|FullyQualifiedName~McpCancellationTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 004/T3

- [ ] Sprawdź zwolnienie slotu po odmowie mapowania, błędzie Core, deadline, anulowaniu i EOF. Zaktualizuj dokumentację i test klasyfikacji; nie zwiększaj liczby tools.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpLifecycleTests|FullyQualifiedName~McpCancellationTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

## Testy i zakończenie

Równocześnie najwyżej jedno wywołanie DB dociera do module; drugie otrzymuje busy/exit2/isError=true; po zakończeniu slot jest dostępny.

- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpLifecycleTests|FullyQualifiedName~McpCancellationTests' --verbosity minimal` → exit 0.
- [ ] `dotnet build SqlHarness.sln --no-restore -warnaserror` → exit 0.
- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName!~Integration' --verbosity minimal` → exit 0; zapisz passed/failed/skipped.
- [ ] Testy obejmują zachowanie właściwego adaptera/runnera, a nie tylko listę nazw lub stałą.
- [ ] `git diff --check` → exit 0.
- [ ] `git status --short` pokazuje wyłącznie autorskie zmiany w zakresie.
- [ ] Indeks zawiera status, commit i dowód; lokalne ścieżki istnieją lub są oznaczone jako nowe.
- [ ] Brak dowodu live/platformowego jest jawny.

Baseline audytu: 1895 Core/CLI passed; MCP 122 passed, 2 timeouty, 4 skipped. Plan005 diagnozuje timeouty. Wcześniejsza naprawa bezpieczeństwa może być gotowa do review przy udokumentowanej niezależnej awarii gate; nie ogłaszaj wtedy pełnego PASS.

## Warunki zatrzymania i utrzymanie

Nie zastępuj busy kolejką i nie serializuj bez potrzeby operacji offline.

Jeśli krok wymaga rzeczywistego celu DB, sekretów lub wyjścia poza autoryzację, zatrzymaj ten krok i zapisz brak. Dokończ niezależne zadania. Nie oznaczaj całości DONE bez wszystkich wymaganych wyników.

Reviewer sprawdza bezpieczeństwo negatywnych przypadków, kompatybilność i prawdziwość dowodów. Po zmianie parsera/SDK/operacji wróć do tych testów. Małe commity typu fix/test/docs, bez push. Wymagane testy charakterystyczne muszą poprzedzać refaktoryzację.
