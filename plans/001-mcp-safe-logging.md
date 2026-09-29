# Plan 001: Zamknij wyciek SQL i parametrów przez stderr

Status: **TODO**
Data: 2026-09-29. Baza: `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`.
Priorytet: P1; nakład: S; ryzyko zmiany: LOW.
Pokrycie: **S1**. Zależności: **—**.

## Kontekst i instrukcja wykonania

Czytaj cały plan. Pracuj w izolowanym worktree, zachowaj cudze zmiany. Zapis planu nie jest wdrożeniem. Po ukończeniu zaktualizuj plans/README.md wraz z dowodem; nie pushuj ani nie publikuj bez zlecenia.

Drift check:
```powershell
git status --short
git diff --stat 8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b..HEAD -- "src/SqlHarness.Mcp/McpHost.cs" "tests/SqlHarness.Mcp.Tests/McpSecretRedactionTests.cs" "tests/SqlHarness.Mcp.Tests/McpStdioProcessTests.cs"
```

Zmiany poprzedników są oczekiwane: przeczytaj je i dopasuj plan przed kodowaniem. Niewyjaśniony rozjazd wymaga aktualizacji założeń, nie mechanicznego wklejania starego kodu.

McpHost.cs:131,143 włącza wszystkie poziomy logowania i przekazuje formatter SDK do stderr. Próba procesu z syntetycznym SQL i parametrem wykazała obecność obu wartości w stderr, także przy odrzuceniu argumentów.

Punkt odniesienia z kodu/komendy:
```text
public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
```

.NET8/C#/xUnit. Dopasuj wzorce do testów wymienionych w zakresie. Core pozostaje wspólnym miejscem safety; adaptery nie tworzą własnego klasyfikatora. Stosuj istniejące fake reader/session/module i syntetyczny SQLHARNESS_HOME. Nie czytaj rzeczywistych sekretów ani artefaktów użytkownika.

## Zakres

- `src/SqlHarness.Mcp/McpHost.cs`
- `tests/SqlHarness.Mcp.Tests/McpSecretRedactionTests.cs`
- `tests/SqlHarness.Mcp.Tests/McpStdioProcessTests.cs`

Dodatkowo: nowe pliki nazwane w krokach, dokumenty wynikowe plans/001-*.md oraz status w indeksie. Inne pliki wymagają jawnej korekty zakresu i uzasadnienia przed zmianą.

Poza zakresem: live DB, deploy, profile i hasła użytkownika, instalacja, push, globalne wyłączenie walidacji i niepowiązane refaktoryzacje. Zachowaj kody 0/2/3/4/5/6/7/8, legacy JSON i oba silniki. Pola addytywne tylko zgodnie z krokami.

## Zadania

### 001/T1

- [ ] Dodaj procesowe testy regresji na wzór McpStdioProcessTests: handshake, poprawny validate, odrzucone argumenty i błędny frame. Użyj różnych sztucznych znaczników w SQL, parametrze, ścieżce i treści wyjątku. Sprawdzaj stdout i stderr; nie uruchamiaj DB.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpSecretRedactionTests|FullyQualifiedName~McpStdioProcessTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 001/T2

- [ ] Zastąp dowolne formatowanie logów SDK stałymi komunikatami lub jawną listą bezpiecznych zdarzeń. Nie wywołuj formattera ani ToString na nieznanym stanie/wyjątku. Ograniczenie poziomu logowania samo w sobie nie jest zabezpieczeniem.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpSecretRedactionTests|FullyQualifiedName~McpStdioProcessTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

### 001/T3

- [ ] Zachowaj stdout wyłącznie dla ramek MCP, bezpieczne komunikaty startu i zakończenia oraz diagnostykę kategorii błędu. Sprawdź normalne EOF i anulowanie.

**Weryfikacja:** `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpSecretRedactionTests|FullyQualifiedName~McpStdioProcessTests' --verbosity minimal` → exit 0 po zmianie. Dla testu regresji najpierw potwierdź oczekiwaną porażkę starego kodu. Krok wyłącznie dokumentacyjny: `git diff --check` i sprawdzenie ścieżek.

## Testy i zakończenie

Test procesu nie znajduje żadnego syntetycznego znacznika na stderr; odpowiedź validate pozostaje poprawną ramką. Błędy startu pozostają zwięzłe.

- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpSecretRedactionTests|FullyQualifiedName~McpStdioProcessTests' --verbosity minimal` → exit 0.
- [ ] `dotnet build SqlHarness.sln --no-restore -warnaserror` → exit 0.
- [ ] `dotnet test SqlHarness.sln --filter 'FullyQualifiedName!~Integration' --verbosity minimal` → exit 0; zapisz passed/failed/skipped.
- [ ] Testy obejmują zachowanie właściwego adaptera/runnera, a nie tylko listę nazw lub stałą.
- [ ] `git diff --check` → exit 0.
- [ ] `git status --short` pokazuje wyłącznie autorskie zmiany w zakresie.
- [ ] Indeks zawiera status, commit i dowód; lokalne ścieżki istnieją lub są oznaczone jako nowe.
- [ ] Brak dowodu live/platformowego jest jawny.

Baseline audytu: 1895 Core/CLI passed; MCP 122 passed, 2 timeouty, 4 skipped. Plan005 diagnozuje timeouty. Wcześniejsza naprawa bezpieczeństwa może być gotowa do review przy udokumentowanej niezależnej awarii gate; nie ogłaszaj wtedy pełnego PASS.

## Warunki zatrzymania i utrzymanie

Nie dodawać regexowej redakcji całych payloadów jako podstawowej ochrony. Nie zmieniać klasyfikatora SQL ani poziomu poufności wyniku query.

Jeśli krok wymaga rzeczywistego celu DB, sekretów lub wyjścia poza autoryzację, zatrzymaj ten krok i zapisz brak. Dokończ niezależne zadania. Nie oznaczaj całości DONE bez wszystkich wymaganych wyników.

Reviewer sprawdza bezpieczeństwo negatywnych przypadków, kompatybilność i prawdziwość dowodów. Po zmianie parsera/SDK/operacji wróć do tych testów. Małe commity typu fix/test/docs, bez push. Wymagane testy charakterystyczne muszą poprzedzać refaktoryzację.
