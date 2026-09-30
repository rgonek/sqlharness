# 009/T4 — Projekt zaostrzonego profilu (assessment, nie wdrożenie)

**Status:** PROJEKT (nie wdrożone, nie decyzja — materiał do rozstrzygnięcia poza tym planem)
**Data:** 2026-09-30
**Baza:** `361280b`
**Poprzednik:** T1–T3 planu 009 (granica statycznego safety); ten dokument ich nie zmienia.
**Dowód live:** brak — dokument projektowy, bez live DB, bez nowych wywołań serwera.

## Cel i zakres

Opcja zaostrzonego profilu dla silnika Postgres, obejmująca cztery obszary: (1) ograniczenia
TEMP, (2) funkcje `SECURITY DEFINER`, (3) operatory, (4) efekty zewnętrzne. Dokument opisuje
wyłącznie rozważane opcje wraz z powodami odrzucenia lub odłożenia. Nie zawiera kodu
wdrożeniowego ani instrukcji włączania mechanizmów wykonawczych (transakcja `READ ONLY`,
`SET ROLE` / przełączanie ról, `REVOKE` / zmiana uprawnień) — te mechanizmy występują tu tylko
jako rozważane i odrzucone/odłożone opcje z uzasadnieniem.

Tło wiążące: [polityka bezpieczeństwa PostgreSQL](../docs/superpowers/specs/2026-09-26-postgres-safety-policy.md)
— model zagrożeń, porównanie kontroli, macierz decyzji, sekcje „Wybrana polityka i granica
zaufania", „Classify", „Sesja: Query i CompareSetup", „Setup i tabele tymczasowe".

## Założenia wspólne

- Klasyfikator `PostgresSafetyClassifier.Classify` widzi tylko tekst batcha (AST SqlParserCS);
  ciał funkcji, widoków, reguł, triggerów i ciał operatorów nie widzi.
- Sesja Npgsql wykonuje sklasyfikowany tekst bez dokładanych komend (bez `BEGIN`,
  `SET TRANSACTION`, `SET`, `SET ROLE`).
- Uprawnienia konta połączenia są granicą zewnętrzną poza harnessem; harness nie czyta
  `pg_roles` ani `pg_proc` i nie wykonuje `SET ROLE`, `SET SESSION AUTHORIZATION`, `GRANT`,
  `REVOKE` (polityka, „Model zagrożeń").
- Etykieta `read-only` znaczy „brak widocznej mutacji i brak pracy lokalnej sesji" — nie jest
  gwarancją braku efektów ukrytych (polityka, „Wybrana polityka i granica zaufania").

## 1. Ograniczenia TEMP

Stan dzisiejszy (polityka, „Setup i tabele tymczasowe"): `CREATE TEMP` / `CREATE TEMPORARY`
(w tym `CREATE TEMP TABLE AS SELECT` bez celu zapisu w źródle), DML wyłącznie do celów
sesyjnie lokalnych, `CREATE INDEX` / `DROP` tych tabel oraz `DROP` indeksu tymczasowego —
bez flag mutacji, w `Query` i w `CompareSetup`.

Rozważane zaostrzenia (projekt, nie wdrożenie):

1. **Zakaz `SELECT INTO TEMP TABLE` w setupie** — dziś dozwolony jako niejednoznaczny zapis
   tymczasowy. Zaostrzenie usunęłoby jedną ścieżkę tworzenia tabel poza `CREATE TEMP`.
   Koszt: zmiana kontraktu setupu udokumentowanego dla agentów; odłożone — brak dowodu, że
   ta jedna ścieżka wnosi ryzyko ponad pozostałe dozwolone zapisy TEMP.
2. **Limit liczby / rozmiaru tabel TEMP na batch** — brak sygnału z audytu, że rozmiar pracy
   lokalnej sesji jest wektorem; odłożone jako nieuzasadnione.
3. **Wymuszenie kwalifikatora `pg_temp` przy każdym odwołaniu** — reguła nazwy już istnieje
   (pierwszy identyfikator `pg_temp` / prefiks `pg_temp_` albo zbiór tabel batcha; polityka,
   „Classify" pkt 4). Doprecyzowanie kwalifikacji nie domyka żadnego z trzech skutków
   (trwały DML, efekty administracyjne, dostęp zewnętrzny); odłożone.

## 2. Funkcje SECURITY DEFINER

Stan dzisiejszy (polityka, macierz decyzji): atrybut `SECURITY DEFINER` nie występuje w tekście
wywołania, więc nie da się go wymusić z samego wywołania; `SELECT app.write_something()`
pozostaje `Allowed`; jedyną kontrolą poza tekstem są uprawnienia konta, których harness nie
zmienia i nie sprawdza. Tak samo funkcje `VOLATILE` wykonujące DML oraz funkcje schowane
w widokach.

Rozważane opcje (projekt, nie wdrożenie):

1. **Rozwiązanie przeciążeń po katalogu (zaufany wpis + odczyt `pg_proc`)** — dałoby widoczność
   atrybutu `SECURITY DEFINER` i volatility wyłącznie dla wywołań obecnych w tekście; nadal nie
   widziałoby funkcji schowanej w widoku ani regule. Wymaga zaufanego wpisu katalogu zależnego
   od `search_path` oraz dodatkowych rund zapytań (polityka, „Porównanie kontroli": liczba
   wywołań serwera dziś nie rośnie). Odrzucone w tej zmianie polityki z tego powodu; jako opcja
   profilu zaostrzonego pozostaje odłożone — bez rozwinięcia widoków i operatorów byłoby kolejną
   listą, nie granicą.
2. **Przełączanie ról / odbieranie uprawnień właścicielowi (`SET ROLE`, `REVOKE`)** — opisane tu
   wyłącznie jako rozważana i odrzucona opcja: harness celowo nie wykonuje `SET ROLE`,
   `SET SESSION AUTHORIZATION`, `GRANT` ani `REVOKE` (polityka, „Model zagrożeń"); zmiana
   uprawnień jest granicą zewnętrzną poza harnessem. Ten projekt nie zawiera instrukcji
   włączania takich mechanizmów.
3. **Lista nazw funkcji `SECURITY DEFINER` utrzymywana ręcznie** — bez rozwiązania przeciążeń
   po typach argumentów kwalifikacja schematem w tekście nie jest tożsamością (polityka, „Model
   zagrożeń"); lista udawałaby dowód. Odrzucone.

## 3. Operatory

Stan dzisiejszy (polityka, „Model zagrożeń"): przeciążenia operatorów (`+` i inne są w AST
węzłem operatora, nie wywołaniem funkcji); ciało operatora jest poza tekstem tak samo jak ciało
funkcji.

Rozważane opcje (projekt, nie wdrożenie):

1. **Rozwinięcie operatorów do funkcji bazowych po katalogu** — wymaga tego samego zaufanego
   wpisu co pkt 2.1 (odczyt katalogu zależny od `search_path`, pełne rozwiązanie przeciążeń po
   typach). Bez tego byłoby kolejną listą nazw, nie granicą. Odłożone z tego samego powodu co
   rozwiązanie przeciążeń funkcji.
2. **Zakaz wybranych operatorów po nazwie/symbolu w tekście** — symbol operatora nie świadczy
   o efekcie (przeciążenie może dodać DML do dowolnego symbolu, a zakaz symbolu blokowałby też
   czyste użycia); lista symboli nie domyka skutku trwałego DML. Odrzucone.

## 4. Efekty zewnętrzne

Stan dzisiejszy (polityka, macierz decyzji): filtry tekstu — dokładne `pg_sleep`,
`pg_read_file`, `pg_ls_dir`, `lo_import`, `set_config`, `pg_cancel_backend`,
`pg_terminate_backend`, `nextval` / `setval`; prefiksy `dblink`, `pg_read_`, `pg_ls_`, `lo_`,
`pg_advisory_` / `pg_try_advisory_`. Filtr jest filtrem tekstu, nie dowodem efektu; mutacja
zatwierdzona flagami nie zdejmuje filtra.

Rozważane zaostrzenia (projekt, nie wdrożenie):

1. **Rozszerzenie prefiksów / dokładnych nazw** — każdy nowy wpis to kolejna nadmiarowa odmowa
   filtra bez zmiany granicy zaufania (efekty schowane w funkcjach, widokach, operatorach
   pozostają niewidoczne). Dopuszczalne tylko jako świadome nadmiarowe odmowy w stylu wpisu
   `lo_` (audyt B4), nigdy jako dowód braku efektu. Odłożone — brak wskazanego wektora.
2. **Blokowanie dostępu zewnętrznego na poziomie roli** (brak `pg_write_server_files`,
   brak praw FDW/`dblink`) — granica zewnętrzna poza harnessem; ten projekt nie zawiera
   instrukcji jej konfigurowania. Odnotowane jako warunek, nie część harnessu.
3. **Transakcja `READ ONLY` jako zaostrzenie** — opisana tu wyłącznie jako rozważana
   i odrzucona opcja: nie blokuje wierszy administracyjnych ani dostępu zewnętrznego
   (`set_config`, `pg_cancel_backend`, `pg_terminate_backend`, blokady doradcze i zdalny SQL
   nie są na liście komend zabronionych w transakcji read-only), a jednocześnie psuje kontrakt
   tabel tymczasowych (`CREATE TEMP` jest `CREATE`, więc setup przestałby działać albo wymagałby
   osobnych rund i innej transakcji dla DDL). Ten projekt nie zawiera kodu ani instrukcji
   włączania transakcji read-only.

## Co ten projekt świadomie nie robi

- Nie wdraża transakcji `READ ONLY` ani przełączania ról — powody odrzucenia: sekcja 4.3
  (psuje `CREATE TEMP`, nie blokuje efektów administracyjnych/zewnętrznych) oraz sekcja 2.2
  (role i uprawnienia są granicą zewnętrzną poza harnessem).
- Nie dodaje odczytu katalogu (`pg_proc`, `pg_roles`), rozwinięcia widoków ani operatorów —
  wymagałoby to zaufanego wpisu i dodatkowych rund; bez nich byłaby to kolejna lista, nie
  granica (sekcje 2.1, 3.1).
- Nie zmienia `ClassificationLabel`, kontraktu mutacji (`--allow-mutation` +
  `--confirm-database`) ani zakresu sesji Npgsql.
- Nie zmienia plików T1–T3 ani żadnego kodu i testu.

## Warunek wdrożenia (poza tym planem)

Każde przyszłe wdrożenie zaostrzeń z tego projektu wymaga osobnej decyzji oraz — dla pozycji
katalogowych (2.1, 3.1) — zaufanego wpisu katalogu, rozwinięcia widoków/operatorów i akceptacji
dodatkowych rund serwera; dla pozycji uprawnieniowych (2.2, 4.2) — przygotowania roli konta
poza SQLHarness. Minimalne uprawnienia konta pozostają granicą zewnętrzną poza harnessem.
