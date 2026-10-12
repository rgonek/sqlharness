# Plan 014: Przygotuj wykonalny kontrakt diagnostyki pg_stat_statements

> **REJECTED 2026-10-11** — `pgstop` will not be built. `query` can already read `pg_stat_statements`; a ranked example query in the agent docs (plan 029) covers the need. Kept for history; do not execute.

Status: **DONE — tylko etap projektowy/diagnostyczny** (funkcja `pgstop` nadal **PLANNED**; commity `f0934b3..7d82bf1`; niezaznaczone pola zadań nie są statusem)
Aktualizacja kontraktu: 2026-10-11 przy `729d88f`: skumulowany ranking v1, runtime probe możliwości; delta odłożona. Data etapu pierwotnego: 2026-09-29. Baza: `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`.
Priorytet: P2; nakład: L; ryzyko zmiany: MED.
Pokrycie: **propozycja diagnostyki PG**. Zależności: **003, 004, 009**.

## Kontekst i instrukcja wykonania

Czytaj cały plan. Pracuj w izolowanym worktree, zachowaj cudze zmiany. Zapis planu nie jest wdrożeniem. Po ukończeniu zaktualizuj plans/README.md wraz z dowodem; nie pushuj ani nie publikuj bez zlecenia.

Drift check:
```powershell
git status --short
git diff --stat 8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b..HEAD -- "plans/014-pg-statements-contract.md" "plans/014-pg-statements-implementation.md"
```

Zmiany poprzedników są oczekiwane: przeczytaj je i dopasuj plan przed kodowaniem. Niewyjaśniony rozjazd wymaga aktualizacji założeń, nie mechanicznego wklejania starego kodu.

Spec docs/superpowers/specs/2026-09-26-postgres-statement-diagnostics.md opisuje PG14+, cumulative counters i delta. Wymaga jednak rozpoznania nieznanej wersji przed connect oraz zakłada szerszą wykrywalność resetów niż sam globalny stats_reset może udowodnić. Wynik jest teraz skumulowanym rankingiem v1 z runtime probe wymaganych nazw/typów; dokładne pary numerów wersji nie są bramą. Delta pozostaje odłożona poza implementację v1.

Punkt odniesienia z kodu/komendy:
```text
QueryStoreAvailable: !pg
```

.NET8/C#/xUnit. Dopasuj wzorce do testów wymienionych w zakresie. Core pozostaje wspólnym miejscem safety; adaptery nie tworzą własnego klasyfikatora. Stosuj istniejące fake reader/session/module i syntetyczny SQLHARNESS_HOME. Nie czytaj rzeczywistych sekretów ani artefaktów użytkownika.

## Zakres

- `plans/014-pg-statements-contract.md`
- `plans/014-pg-statements-implementation.md`

Dodatkowo: nowe pliki nazwane w krokach, dokumenty wynikowe plans/014-*.md oraz status w indeksie. Inne pliki wymagają jawnej korekty zakresu i uzasadnienia przed zmianą.

Poza zakresem: live DB, deploy, profile i hasła użytkownika, instalacja, push, globalne wyłączenie walidacji i niepowiązane refaktoryzacje. Ten etap tworzy dokumenty; nie dodaje komend, stubów ani capabilities.

## Zadania

### 014/T1

- [ ] Sprawdź oficjalne dokumenty PG14/15/16 i zapisz macierz dostępności kolumn, wersji rozszerzenia i możliwości wykrywania resetu selektywnego. Nie utożsamiaj wersji serwera z wersją rozszerzenia. Podaj źródła w dokumencie.

**Weryfikacja:** sprawdź wskazane źródła i odwołania lokalne; `git diff --check` → exit 0; rezultat kroku zapisany w dokumencie wynikowym.

### 014/T2

- [ ] Zaprojektuj sekwencję resolve/auth/identity/version/extension/runtime column probe. Wersję odczytaj po potwierdzeniu targetu; sprawdź wymagane nazwy/typy niezależnie od dokładnej pary numerów. Bezpiecznie kwalifikuj obiekt rozszerzenia. Niekompatybilny wymagany shape, brak extension lub uprawnień rankingu jest exit 5; opcjonalne metryki/kontekst unavailable z przyczyną.

**Weryfikacja:** sprawdź wskazane źródła i odwołania lokalne; `git diff --check` → exit 0; rezultat kroku zapisany w dokumencie wynikowym.

### 014/T3

- [ ] Zachowaj osobny kontrakt od qstop: brak window i recency, filtry current DB, ranking deterministyczny, SQL tylko w lokalnym queries.jsonl. Ustal znaczenie ukrytego queryid i tożsamość agregacji; nie scalaj nieznanych ID w fałszywe jedno zapytanie.

**Weryfikacja:** sprawdź wskazane źródła i odwołania lokalne; `git diff --check` → exit 0; rezultat kroku zapisany w dokumencie wynikowym.

### 014/T4

- [ ] Zapisz odłożenie delty poza v1. Nie implementuj Compare, magazynu migawek ani samych tokenów unknown/incomparable bez użytecznej różnicy. Ewentualny późniejszy plan obserwowanej różnicy musi opisać reset/eviction/restart i niewykrywalną utratę ciągłości; macierz resetów nie blokuje skumulowanego rankingu.

**Weryfikacja:** sprawdź wskazane źródła i odwołania lokalne; `git diff --check` → exit 0; rezultat kroku zapisany w dokumencie wynikowym.

### 014/T5

- [ ] Napisz wykonawczy plan PgStatementTopQuery/Reader/ArtifactWriter, CLI pgstop i ewentualnego nowego inspect kind, ze wspólnym Core, scope i gate. Fixture-first wymagane/optional shape, PG14/15/16/17/18 i nieznany numer z kompatybilnym minimum, bez allowlisty par; brak instalacji extension, brak zmian GUC. Live proof pozostaje osobnym krokiem wymagającym jawnego celu.

**Weryfikacja:** sprawdź wskazane źródła i odwołania lokalne; `git diff --check` → exit 0; rezultat kroku zapisany w dokumencie wynikowym.

## Testy i zakończenie

Kontrakt rozróżnia obserwowalne fakty od unknown, ma tabelę fixture/error/empty/runtime-shape/version; delta nie jest zadaniem v1 i samodzielny plan kodu. Brak nowych reklamowanych capabilities przed implementacją.

- [ ] Dokumenty wynikowe zawierają decyzje, granice, zakres przyszłych plików oraz tabelę testów.
- [ ] Niepewności oznaczone UNPROVEN z warunkiem uzyskania dowodu.
- [ ] Nie uruchamiaj testów aplikacji przy samym zapisie dokumentów.
- [ ] `git diff --check` → exit 0.
- [ ] `git status --short` pokazuje wyłącznie autorskie zmiany w zakresie.
- [ ] Indeks zawiera status, commit i dowód; lokalne ścieżki istnieją lub są oznaczone jako nowe.
- [ ] Brak dowodu live/platformowego jest jawny.

Baseline audytu: 1895 Core/CLI passed; MCP 122 passed, 2 timeouty, 4 skipped. Plan005 diagnozuje timeouty. Wcześniejsza naprawa bezpieczeństwa może być gotowa do review przy udokumentowanej niezależnej awarii gate; nie ogłaszaj wtedy pełnego PASS.

## Warunki zatrzymania i utrzymanie

Etap projektowy; pytania resetów pozostają źródłowym UNPROVEN i nie blokują skumulowanego v1. Nie wymyślaj znaczników ciągłości ani nie dodawaj Compare.

Jeśli krok wymaga rzeczywistego celu DB, sekretów lub wyjścia poza autoryzację, zatrzymaj ten krok i zapisz brak. Dokończ niezależne zadania. Nie oznaczaj całości DONE bez wszystkich wymaganych wyników.

Reviewer sprawdza bezpieczeństwo negatywnych przypadków, kompatybilność i prawdziwość dowodów. Po zmianie parsera/SDK/operacji wróć do tych testów. Małe commity typu fix/test/docs, bez push. Wymagane testy charakterystyczne muszą poprzedzać refaktoryzację.
