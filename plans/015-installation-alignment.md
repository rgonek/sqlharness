# Plan 015: Zdiagnozuj rozjazd zainstalowanego CLI i repozytorium

Status: **DONE — tylko etap diagnostyczny** (rozjazd zostaje; nie RESOLVED EXTERNALLY; binarka PATH nie została zastąpiona; instalacja nie wykonana; dowód: `plans/015-installation-evidence.md`; commity `d2a0729..22c0644`; nie scalono; push czeka)
Data: 2026-09-29. Baza: `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`.
Priorytet: P3; nakład: S; ryzyko zmiany: LOW.
Pokrycie: **obserwacja PATH i nieaktualnego help**. Zależności: **005**.

## Kontekst i instrukcja wykonania

Czytaj cały plan. Pracuj w izolowanym worktree, zachowaj cudze zmiany. Zapis planu nie jest wdrożeniem. Po ukończeniu zaktualizuj plans/README.md wraz z dowodem; nie pushuj ani nie publikuj bez zlecenia.

Drift check:
```powershell
git status --short
git diff --stat 8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b..HEAD -- "plans/015-installation-evidence.md"
```

Zmiany poprzedników są oczekiwane: przeczytaj je i dopasuj plan przed kodowaniem. Niewyjaśniony rozjazd wymaga aktualizacji założeń, nie mechanicznego wklejania starego kodu.

Audyt PATH wskazał C:\Users\rgone\.local\bin\sqlharness.exe z help bez capabilities/validate/mcp, podczas gdy build repo obsługuje MCP. To obserwacja lokalnej instalacji, nie wada aktualnego źródła.

Punkt odniesienia z kodu/komendy:
```text
Get-Command sqlharness
```

.NET8/C#/xUnit. Dopasuj wzorce do testów wymienionych w zakresie. Core pozostaje wspólnym miejscem safety; adaptery nie tworzą własnego klasyfikatora. Stosuj istniejące fake reader/session/module i syntetyczny SQLHARNESS_HOME. Nie czytaj rzeczywistych sekretów ani artefaktów użytkownika.

## Zakres

- `plans/015-installation-evidence.md`

Dodatkowo: nowe pliki nazwane w krokach, dokumenty wynikowe plans/015-*.md oraz status w indeksie. Inne pliki wymagają jawnej korekty zakresu i uzasadnienia przed zmianą.

Poza zakresem: live DB, deploy, profile i hasła użytkownika, instalacja, push, globalne wyłączenie walidacji i niepowiązane refaktoryzacje. Ten etap tworzy dokumenty; nie dodaje komend, stubów ani capabilities.

## Zadania

### 015/T1

- [x] Uruchom Get-Command sqlharness -All, sqlharness --help i --version; porównaj z dotnet run --project src/SqlHarness.Cli -- --help oraz capabilities --json z buildu repo. Nie wyświetlaj targets.json, env ani profili.

**Weryfikacja:** sprawdź wskazane źródła i odwołania lokalne; `git diff --check` → exit 0; rezultat kroku zapisany w dokumencie wynikowym.

### 015/T2

- [x] Zapisz pochodzenie, datę/hash binarki i listę dostępnych komend w evidence. Jeżeli rozjazd już zniknął, oznacz RESOLVED EXTERNALLY, nie wykonuj reinstalacji.

**Weryfikacja:** sprawdź wskazane źródła i odwołania lokalne; `git diff --check` → exit 0; rezultat kroku zapisany w dokumencie wynikowym.

### 015/T3

- [x] Przy istniejącym rozjeździe przygotuj dokładną instrukcję aktualizacji z weryfikacją sumy i kopią poprzedniej binarki. Wykonanie instalacji jest osobnym zadaniem; zapis planu nie oznacza zastąpienia PATH executable.

**Weryfikacja:** sprawdź wskazane źródła i odwołania lokalne; `git diff --check` → exit 0; rezultat kroku zapisany w dokumencie wynikowym.

## Testy i zakończenie

Jest bieżący dowód źródła komendy i rozjazdu lub jego ustąpienia; brak zmian profili/instalacji.

- [x] Dokumenty wynikowe zawierają decyzje, granice, zakres przyszłych plików oraz tabelę testów.
- [x] Niepewności oznaczone UNPROVEN z warunkiem uzyskania dowodu.
- [x] Nie uruchamiaj testów aplikacji przy samym zapisie dokumentów.
- [x] `git diff --check` → exit 0.
- [x] `git status --short` pokazuje wyłącznie autorskie zmiany w zakresie.
- [x] Indeks zawiera status, commit i dowód; lokalne ścieżki istnieją lub są oznaczone jako nowe.
- [x] Brak dowodu live/platformowego jest jawny.

Baseline audytu: 1895 Core/CLI passed; MCP 122 passed, 2 timeouty, 4 skipped. Plan005 diagnozuje timeouty. Wcześniejsza naprawa bezpieczeństwa może być gotowa do review przy udokumentowanej niezależnej awarii gate; nie ogłaszaj wtedy pełnego PASS.

## Warunki zatrzymania i utrzymanie

Nie nadpisuj działającej binarki ani konfiguracji użytkownika w zadaniu diagnostycznym.

Jeśli krok wymaga rzeczywistego celu DB, sekretów lub wyjścia poza autoryzację, zatrzymaj ten krok i zapisz brak. Dokończ niezależne zadania. Nie oznaczaj całości DONE bez wszystkich wymaganych wyników.

Reviewer sprawdza bezpieczeństwo negatywnych przypadków, kompatybilność i prawdziwość dowodów. Po zmianie parsera/SDK/operacji wróć do tych testów. Małe commity typu fix/test/docs, bez push. Wymagane testy charakterystyczne muszą poprzedzać refaktoryzację.
