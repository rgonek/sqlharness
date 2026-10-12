# Plan 037: Potwierdź kształty pgstop i przygotuj fixture możliwości runtime

> **REJECTED 2026-10-11** — `pgstop` will not be built. `query` can already read `pg_stat_statements`; a ranked example query in the agent docs (plan 029) covers the need. Kept for history; do not execute.

> Wykonawca: przeczytaj kontrakt i implementation plan 014. Ten plan tworzy dokumentację i fixture specyfikujące przyszłe testy; nie wdraża pgstop. Drift sprawdź przez `git diff --stat 729d88f..HEAD -- plans/014-pg-statements-contract.md plans/014-pg-statements-implementation.md docs/superpowers/specs/2026-09-26-postgres-statement-diagnostics.md docs/superpowers/specs/2026-09-10-postgres-engine-design.md`. Przeanalizuj zmiany i dostosuj wskazówki. Zatrzymaj tylko krok z rzeczywistą nierozwiązaną sprzecznością zachowania.

## Status

- **Priority**: P2
- **Effort**: S–M
- **Risk**: LOW
- **Status**: PLANNED; aktualizacja projektu 2026-10-11 nie oznacza wykonania badań PG17/18.
- **Depends on**: kontrakt 014; plan 036 daje środowisko live proof, nie listę wersji wymaganych do dopuszczenia runtime.
- **Category**: direction/tests
- **Planned at**: `729d88f`, 2026-10-11

## Problem i wynik

Wcześniejszy projekt dopuszczał jedynie dokładne pary PG14+1.9 i PG15/16+1.10. To utrwalało ręczną listę numerów jako blokadę poprawnej diagnostyki. Kontrakt 014 teraz wymaga runtime probe nazw/typów i jawnej projekcji, więc nieznana wersja z kompatybilnym kształtem jest obsługiwana. Ten plan potwierdza wymagane minimum, opcjonalne pola oraz fixture PG17/18 na źródłach pierwotnych. Numery wersji służą pokryciu testów; nie wyłączają poprawnego kształtu.

## Stan i zakres

`plans/014-pg-statements-contract.md` ma historyczną macierz PG14/15/16 oraz nowy normatywny krok 6 runtime probe. `plans/014-pg-statements-implementation.md` wymaga fixture F14/F15/F17/F18/F-new/F-missing/F-type/F-optional/F-schema. Nie ma jeszcze product code PgStatementTop. `scripts/setup-local-postgres.ps1` używa postgres:16. Nie zmieniaj setupu w tym zadaniu dokumentacyjnym.

W zakresie: obydwa dokumenty 014, `docs/superpowers/specs/2026-09-26-postgres-statement-diagnostics.md`, jedna adnotacja decyzji w `docs/superpowers/specs/2026-09-10-postgres-engine-design.md`, indeks. Poza zakresem: kod pgstop, skrypty DB, delta, reset, instalacja extension, zmiany GUC, profile i hasła użytkownika. Pracuj w izolowanym worktree, commity docs(037), bez push.

## Kroki

### 1. Źródła PG17/18

Otwórz oficjalne strony `https://www.postgresql.org/docs/17/pgstatstatements.html` i `https://www.postgresql.org/docs/18/pgstatstatements.html` oraz katalogi systemowe opisujące namespace, kolumny/typy i własność obiektów rozszerzenia. Uzupełnij macierz 014 o PG17/18; każda faktograficzna komórka ma źródło/wersję strony albo UNPROVEN z warunkiem. Rozdziel extversion od server_version. Nie zgaduj nazw ani semantyki czasów I/O.

**Weryfikacja:** `git diff --check` → exit 0; każda nowa komórka ma URL albo jawne UNPROVEN.

### 2. Descriptor możliwości i jawne query

Potwierdź wymagane typy kroku 6 kontraktu i katalogowy sposób identyfikacji obiektu należącego do rozszerzenia. Opisz jawny SQL probe i jawne projekcje w implementation plan; namespace cytowany helperem, nazwy metryk z wewnętrznej listy, brak search_path/SELECT *. Kolumny opcjonalne nie są warunkiem sukcesu. Zmianę nazwy I/O odwzoruj wyłącznie po źródłowym potwierdzeniu semantyki; gdy znaczenie się różni, pozostaw unavailable albo zaprojektuj osobne pole z uzasadnieniem. Nie sumuj odmiennych zakresów I/O jako tej samej metryki.

**Weryfikacja:** `git diff --check` → exit 0; wszystkie projekcje mają jawne kolumny, żaden zapis nie odrzuca wyłącznie nieznanej pary wersji.

### 3. Fixture i live proof

W tabeli implementation plan opisz fixture kształtów PG14/15/16/17/18, nieznanych wersji z kompatybilnym minimum, braku/zmiany typu wymaganej kolumny, opcjonalnego pola unavailable, extra kolumn, namespace z cudzysłowem i fałszywego obiektu na search_path. Oczekiwane: zgodne minimum → ranking; niezgodne minimum → exit 5 przed query rankingowym; opcjonalne brak → null/unavailable z przyczyną. Wersja sama nie tworzy innej ścieżki. Fixture nie jest live proof.

Zapisz sposób użycia izolowanej ścieżki 036 dla PG17 i PG18 z pg_stat_statements. Przy wykonaniu live wymagany osobny ograniczony cel/autoryzacja zgodnie z kontraktem; nie wybieraj profilu użytkownika. Brak środowiska jednej wersji zostaw jako nieprzeprowadzony dowód; nie przywracaj przez to runtime allowlisty. Live sprawdza faktycznie obecne typy, discovery/quoting, prosty ranking i ograniczoną rolę. Reset/delta nie są zadaniem v1.

**Weryfikacja:** `git diff --check` → exit 0; wszystkie wyżej wymienione klasy mają wejście/oczekiwany wynik, status live jest jawny.

### 4. Uzgodnij specyfikacje

Zastąp w szkicu postgres-statement-diagnostics normatywne delta/allowlist wymagania skumulowanym v1 i runtime probe według 014. Future delta pozostaje odłożona; bez funkcji która zawsze daje unknown/incomparable. W engine-design przy non-goal pg_stat_statements dodaj adnotację: adopted as plan 014, feature still PLANNED. Zaktualizuj indeks z dowodem projektu, bez twierdzenia o wdrożeniu.

**Weryfikacja:** `git diff --check` → exit 0; `Test-Path` każdej cytowanej lokalnej ścieżki → True; `git diff --stat` mieści się w zakresie. Testy aplikacji nie są potrzebne dla dokumentów.

## Done i STOP

- [ ] PG17/18 mają źródłowe komórki lub oznaczone brakujące dowody.
- [ ] Descriptor/probe, wymagane/optional typy i bezpieczne kwalifikowanie obiektu są wykonalne i zgodne z kontraktem 014.
- [ ] Fixture obejmują nieznaną kompatybilną wersję oraz błędny wymagany shape; numer wersji nie stanowi bramy.
- [ ] Specyfikacje i indeks uzgodnione, diff check exit 0; brak kodu i uruchomień na DB.

Gdy źródło jest niedostępne, zapisz konkretny brak i wykonaj niezależne zadania; bez zmyślonych faktów. Zatrzymaj zależny krok, jeżeli źródła obalają semantykę wymaganej metryki albo bezpieczną identyfikację obiektu i nie da się dopasować tego w zakresie kontraktu. Każdy nowy major może dodać coverage; nie potrzebuje osobnego dokumentacyjnego odblokowania runtime. Reviewer sprawdza znaczenie aliasów I/O i rozdzielenie nieznanego numeru od niezgodnego kształtu.
