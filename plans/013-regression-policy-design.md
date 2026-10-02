# Plan 013: Doprecyzuj i przygotuj wdrożenie decyzji regresji CI

Status: **DONE — etap projektowy/diagnostyczny** (funkcja nadal **PLANNED**; dokumenty: `plans/013-regression-contract.md`, `plans/013-regression-implementation.md`; commity `983f7b6..f6c8d7a`)
Data: 2026-09-29. Baza: `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`.
Priorytet: P2; nakład: L; ryzyko zmiany: MED.
Pokrycie: **propozycja funkcji regresji**. Zależności: **003, 006, 009**.

## Kontekst i instrukcja wykonania

Czytaj cały plan. Pracuj w izolowanym worktree, zachowaj cudze zmiany. Zapis planu nie jest wdrożeniem. Po ukończeniu zaktualizuj plans/README.md wraz z dowodem; nie pushuj ani nie publikuj bez zlecenia.

Drift check:
```powershell
git status --short
git diff --stat 8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b..HEAD -- "plans/013-regression-contract.md" "plans/013-regression-implementation.md"
```

Zmiany poprzedników są oczekiwane: przeczytaj je i dopasuj plan przed kodowaniem. Niewyjaśniony rozjazd wymaga aktualizacji założeń, nie mechanicznego wklejania starego kodu.

Spec docs/superpowers/specs/2026-09-26-benchmark-regression-policy.md jest projektem. R1/R2 zwracają pass dla zer zanim R4–R7 sprawdzą brak danych/niestabilność/equivalence, co koliduje z deklarowaną ostrożnością. ArtifactMetricsSection nie zawiera wszystkich wejść postulowanych tabelą spec.

Punkt odniesienia z kodu/komendy:
```text
public sealed record ArtifactMetricsSection(
```

.NET8/C#/xUnit. Dopasuj wzorce do testów wymienionych w zakresie. Core pozostaje wspólnym miejscem safety; adaptery nie tworzą własnego klasyfikatora. Stosuj istniejące fake reader/session/module i syntetyczny SQLHARNESS_HOME. Nie czytaj rzeczywistych sekretów ani artefaktów użytkownika.

## Zakres

- `plans/013-regression-contract.md`
- `plans/013-regression-implementation.md`

Dodatkowo: nowe pliki nazwane w krokach, dokumenty wynikowe plans/013-*.md oraz status w indeksie. Inne pliki wymagają jawnej korekty zakresu i uzasadnienia przed zmianą.

Poza zakresem: live DB, deploy, profile i hasła użytkownika, instalacja, push, globalne wyłączenie walidacji i niepowiązane refaktoryzacje. Ten etap tworzy dokumenty; nie dodaje komend, stubów ani capabilities.

## Zadania

### 013/T1

- [x] Zmapuj potrzebne dane do ArtifactReader, CompareReport i MetricReport: liczba przebiegów, spread, dostępność czasu/CPU/reads, equivalence, stabilność. W kontrakcie oznacz brakujące pola jako unknown; nie domyślaj ich.

**Weryfikacja:** sprawdź wskazane źródła i odwołania lokalne; `git diff --check` → exit 0; rezultat kroku zapisany w dokumencie wynikowym.

### 013/T2

- [x] Zapisz jednoznaczną kolejność: kompletność/dostępność/equivalence/stabilność przed progami i przypadkami zero. Udokumentuj zmianę względem starej spec. Dodaj tabelę oczekiwań dla kombinacji zero + mismatch/missing/unstable, a nie wyłącznie izolowanych przypadków.

**Weryfikacja:** sprawdź wskazane źródła i odwołania lokalne; `git diff --check` → exit 0; rezultat kroku zapisany w dokumencie wynikowym.

### 013/T3

- [x] Zachowaj offline odczyt bez rebenchmark, pass/fail/inconclusive jako verdict z exit0, brak przejęcia exit8, CPU PG unavailable i AND progów względny+absolutny. Zachowaj elapsed plus corroboration, minimum5 i spread25% o ile zmiana zostanie jawnie uzasadniona.

**Weryfikacja:** sprawdź wskazane źródła i odwołania lokalne; `git diff --check` → exit 0; rezultat kroku zapisany w dokumencie wynikowym.

### 013/T4

- [x] Napisz samodzielny plan implementacji RegressionPolicy.cs, RegressionDecider.cs, RegressCommand.cs i testów; wskaż konieczne addytywne metadane artefaktu, kompatybilność legacy, offline błędy i przykładowy CI gate po verdict. MCP dodawaj dopiero w osobnym uzgodnionym rozszerzeniu katalogu.

**Weryfikacja:** sprawdź wskazane źródła i odwołania lokalne; `git diff --check` → exit 0; rezultat kroku zapisany w dokumencie wynikowym.

## Testy i zakończenie

Powstają dwa dokumenty: kontrakt bez sprzecznej kolejności i wykonawczy plan z pełną tabelą testów. Stan funkcji nadal PLANNED, bez capabilities ani stubów.

- [x] Dokumenty wynikowe zawierają decyzje, granice, zakres przyszłych plików oraz tabelę testów.
- [x] Niepewności oznaczone UNPROVEN z warunkiem uzyskania dowodu.
- [x] Nie uruchamiaj testów aplikacji przy samym zapisie dokumentów.
- [x] `git diff --check` → exit 0.
- [x] `git status --short` pokazuje wyłącznie autorskie zmiany w zakresie.
- [x] Indeks zawiera status, commit i dowód; lokalne ścieżki istnieją lub są oznaczone jako nowe.
- [x] Brak dowodu live/platformowego jest jawny.

Baseline audytu: 1895 Core/CLI passed; MCP 122 passed, 2 timeouty, 4 skipped. Plan005 diagnozuje timeouty. Wcześniejsza naprawa bezpieczeństwa może być gotowa do review przy udokumentowanej niezależnej awarii gate; nie ogłaszaj wtedy pełnego PASS.

## Warunki zatrzymania i utrzymanie

Ten etap jest projektowy. Nie implementuj starej macierzy wprost i nie udawaj metryki dostępnej na podstawie wartości 0.

Jeśli krok wymaga rzeczywistego celu DB, sekretów lub wyjścia poza autoryzację, zatrzymaj ten krok i zapisz brak. Dokończ niezależne zadania. Nie oznaczaj całości DONE bez wszystkich wymaganych wyników.

Reviewer sprawdza bezpieczeństwo negatywnych przypadków, kompatybilność i prawdziwość dowodów. Po zmianie parsera/SDK/operacji wróć do tych testów. Małe commity typu fix/test/docs, bez push. Wymagane testy charakterystyczne muszą poprzedzać refaktoryzację.
