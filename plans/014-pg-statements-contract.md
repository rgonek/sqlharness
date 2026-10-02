# Kontrakt pg_stat_statements — macierz źródeł

Status: macierz źródeł (T1); sekwencja połączenia (T2); osobny kontrakt od qstop (T3); delta (T4).

## Macierz źródeł

Ta macierz sprawdza oficjalne strony PostgreSQL 14, 15 i 16 oraz dwa odwołania lokalne. Nie opisuje sekwencji odczytu, rankingu, delty ani planu kodu. Nie dodaje komend, stubów ani capabilities. Nie uruchamiano live DB ani `dotnet test`.

Wersja serwera nie jest wersją rozszerzenia `pg_stat_statements`. Strona modułu opisuje kształt dostarczany z daną wersją główną serwera. Zainstalowana wersja jest osobnym polem katalogu. Zmienia ją `ALTER EXTENSION … UPDATE`, nie sam upgrade serwera.

Stopki otwartych stron modułu: PostgreSQL 14.24, sekcja F.30; PostgreSQL 15.19, sekcja F.32; PostgreSQL 16.15, sekcja F.32. Liczby `1.9` i `1.10` nie stoją na tych stronach HTML. Pochodzą z pliku kontrolnego tagu.

Specyfikacja `docs/superpowers/specs/2026-09-26-postgres-statement-diagnostics.md` jest hipotezą. Porównane są jej sekcje 3, 6 i 7. Status wiersza: fakt ze źródła, rozjazd ze specyfikacją albo `UNPROVEN` z warunkiem dowodu.

### Wersja rozszerzenia, nie wersja serwera

| Wiersz | PG14 | PG15 | PG16 | Status |
|---|---|---|---|---|
| Liczba na stronie modułu | Strona F.30 nie podaje `default_version` ani numeru rozszerzenia. | Strona F.32 nie podaje tej liczby. | Strona F.32 nie podaje tej liczby. | fakt |
| `default_version` w pliku kontrolnym | Tagi `REL_14_0` i `REL_14_24`: `default_version = '1.9'`. | Tagi `REL_15_0` i `REL_15_19`: `default_version = '1.10'`. | Tagi `REL_16_0` i `REL_16_15`: `default_version = '1.10'`. | fakt; plik `contrib/pg_stat_statements/pg_stat_statements.control` na mirrorze `postgres/postgres`. To nie jest numer serwera. |
| Wydania pośrednie między tagiem `.0` a stopką | Nie otwarto `REL_14_1` … `REL_14_23`. | Nie otwarto `REL_15_1` … `REL_15_18`. | Nie otwarto `REL_16_1` … `REL_16_14`. | `UNPROVEN`. Warunek: odczyt `default_version` w każdym tagu pośrednim. Oba otwarte końce tej wersji głównej są zgodne, ale środek serii nie był otwarty. |
| Skąd bierze się wersja przy `CREATE EXTENSION` | Parametr `version`: „The default version is whatever is specified in the extension's control file.” | To samo zdanie. | To samo zdanie. | fakt. Pominięty `VERSION` nie oznacza numeru serwera. |
| `ALTER EXTENSION … UPDATE` | Synopsis: `ALTER EXTENSION name UPDATE [ TO new_version ]`. Forma `UPDATE` aktualizuje zainstalowaną wersję skryptem aktualizacji. Bez `new_version` komenda „attempts to update to whatever is shown as the default version in the extension's control file.” | To samo. | To samo. Sekcja 38.17.4 dodatkowo: `ALTER EXTENSION UPDATE` „track which version of the extension is actually installed in a given database.” | fakt. To osobna komenda SQL, nie upgrade serwera. Zdanie o śledzeniu zainstalowanej wersji otwarto wprost dla PG16; PG14 i PG15 mają tę samą formę `UPDATE` na stronie `ALTER EXTENSION`. |
| Upgrade serwera a rozszerzenie | `pg_upgrade`, krok „Install extension shared object files”: „Do not load the schema definitions, e.g., `CREATE EXTENSION` pgcrypto, because these will be duplicated from the old cluster. If extension updates are available, `pg_upgrade` will report this and create a script that can be run later to update them.” | To samo zdanie. | To samo zdanie. | fakt. Strona nie nazywa treści tego skryptu jako `ALTER EXTENSION pg_stat_statements UPDATE`. Mówi, że aktualizacja rozszerzenia jest późniejszym skryptem, a definicje pochodzą ze starego klastra. |
| Katalog wersji | `pg_extension.extversion` `text`: „Version name for the extension” (52.22). `pg_available_extensions.default_version`: „Name of default version, or NULL if none is specified.” `installed_version`: „Currently installed version of the extension, or NULL if not installed” (52.65). | Te same kolumny. `pg_extension` to 53.22, widok to 54.2. | Te same kolumny. `pg_extension` to 53.22, widok to 54.2. | fakt. `installed_version` i `extversion` nie są `server_version`. |
| Zasięg instalacji | Widoki i funkcje „are not available globally but can be enabled for a specific database with `CREATE EXTENSION pg_stat_statements`.” | To samo zdanie. | To samo zdanie. | fakt |

### Kolumny `pg_stat_statements`

Widok ma jeden wiersz na każdą różną kombinację database ID, user ID, query ID i tego, czy polecenie jest top-level, aż do limitu modułu. To zdanie jest w F.30.1, F.32.1 (PG15) i F.32.1 (PG16).

`toplevel` i czasy planów są już w tabeli PG14. Otwarte strony nie mówią, że doszły między PG14 a PG16. Między tymi tabelami dochodzą kolumny I/O `temp_blk_read_time` i `temp_blk_write_time` oraz osiem kolumn `jit_*`.

| Kolumna | Typ | PG14 F.21 | PG15 F.20 | PG16 F.22 | Status |
|---|---|---|---|---|---|
| `userid` | `oid` | tak | tak | tak | fakt |
| `dbid` | `oid` | tak | tak | tak | fakt |
| `toplevel` | `bool` | tak | tak | tak | fakt. Opis: true, gdy zapytanie wykonano jako top-level; zawsze true, gdy `pg_stat_statements.track` jest `top`. Spec §3 zalicza kolumnę do podzbioru PG14. Zgodne co do obecności. |
| `queryid` | `bigint` | tak | tak | tak | fakt. „Hash code to identify identical normalized queries.” |
| `query` | `text` | tak | tak | tak | fakt. Linia tabeli nie mówi „nullable”. Proza mówi o null osobno, w wierszach widoczności. Spec §3 zapisuje `(nullable)`. To jest zgodne z prozą o odrzuceniu tekstów, nie z linią typu. |
| `plans` | `bigint` | tak | tak | tak | fakt. Liczba planowań, gdy `pg_stat_statements.track_planning` jest włączone, inaczej zero. |
| `total_plan_time` | `double precision` | tak | tak | tak | fakt. Milisekundy; zero, gdy `track_planning` wyłączone. |
| `min_plan_time` | `double precision` | tak | tak | tak | fakt. Ten sam warunek zera. |
| `max_plan_time` | `double precision` | tak | tak | tak | fakt. Ten sam warunek zera. |
| `mean_plan_time` | `double precision` | tak | tak | tak | fakt. Ten sam warunek zera. |
| `stddev_plan_time` | `double precision` | tak | tak | tak | fakt. Ten sam warunek zera. |
| `calls` | `bigint` | tak | tak | tak | fakt |
| `total_exec_time` | `double precision` | tak | tak | tak | fakt |
| `min_exec_time` | `double precision` | tak | tak | tak | fakt |
| `max_exec_time` | `double precision` | tak | tak | tak | fakt |
| `mean_exec_time` | `double precision` | tak | tak | tak | fakt |
| `stddev_exec_time` | `double precision` | tak | tak | tak | fakt |
| `rows` | `bigint` | tak | tak | tak | fakt |
| `shared_blks_hit` | `bigint` | tak | tak | tak | fakt |
| `shared_blks_read` | `bigint` | tak | tak | tak | fakt |
| `shared_blks_dirtied` | `bigint` | tak | tak | tak | fakt |
| `shared_blks_written` | `bigint` | tak | tak | tak | fakt |
| `local_blks_hit` | `bigint` | tak | tak | tak | fakt |
| `local_blks_read` | `bigint` | tak | tak | tak | fakt |
| `local_blks_dirtied` | `bigint` | tak | tak | tak | fakt |
| `local_blks_written` | `bigint` | tak | tak | tak | fakt |
| `temp_blks_read` | `bigint` | tak | tak | tak | fakt |
| `temp_blks_written` | `bigint` | tak | tak | tak | fakt |
| `blk_read_time` | `double precision` | tak | tak | tak | fakt, ze zmianą opisu. PG14: „reading blocks”. PG15 i PG16: „reading data file blocks”. Zero, gdy `track_io_timing` wyłączone. |
| `blk_write_time` | `double precision` | tak | tak | tak | fakt, ze zmianą opisu. PG14: „writing blocks”. PG15 i PG16: „writing data file blocks”. Zero, gdy `track_io_timing` wyłączone. |
| `temp_blk_read_time` | `double precision` | brak | tak | tak | fakt. Czas czytania bloków plików tymczasowych, w milisekundach; zero, gdy `track_io_timing` wyłączone. Spec §3 nie wymienia tej kolumny. Rozjazd: między PG14 a PG15 doszła kolumna I/O, której sekcja 3 nie ma. |
| `temp_blk_write_time` | `double precision` | brak | tak | tak | fakt. Ten sam rozjazd co wyżej, dla zapisu bloków tymczasowych. |
| `wal_records` | `bigint` | tak | tak | tak | fakt |
| `wal_fpi` | `bigint` | tak | tak | tak | fakt |
| `wal_bytes` | `numeric` | tak | tak | tak | fakt |
| `jit_functions` | `bigint` | brak | tak | tak | fakt. Spec §3: PG15 dodaje osiem `jit_*`, w tym tę. Zgodne. |
| `jit_generation_time` | `double precision` | brak | tak | tak | fakt. Zgodne ze spec §3. |
| `jit_inlining_count` | `bigint` | brak | tak | tak | fakt. Zgodne ze spec §3. |
| `jit_inlining_time` | `double precision` | brak | tak | tak | fakt. Zgodne ze spec §3. |
| `jit_optimization_count` | `bigint` | brak | tak | tak | fakt. Zgodne ze spec §3. |
| `jit_optimization_time` | `double precision` | brak | tak | tak | fakt. Zgodne ze spec §3. |
| `jit_emission_count` | `bigint` | brak | tak | tak | fakt. Zgodne ze spec §3. |
| `jit_emission_time` | `double precision` | brak | tak | tak | fakt. Zgodne ze spec §3. |

Na otwartych stronach PG15 i PG16 nie ma kolumny `jit_*` poza tą ósemką. Strona PG14 nie ma żadnej kolumny `jit_*`.

### Kolumny `pg_stat_statements_info`

Widok ma jeden wiersz. To zdanie jest w F.30.2, F.32.2 (PG15) i F.32.2 (PG16).

| Kolumna | Typ | PG14 F.22 | PG15 F.21 | PG16 F.23 | Status |
|---|---|---|---|---|---|
| `dealloc` | `bigint` | tak | tak | tak | fakt. „Total number of times `pg_stat_statements` entries about the least-executed statements were deallocated because more distinct statements than `pg_stat_statements.max` were observed.” Spec §3 wymienia tę kolumnę na 14+. Zgodne co do obecności. |
| `stats_reset` | `timestamp with time zone` | tak | tak | tak | fakt. „Time at which all statistics in the `pg_stat_statements` view were last reset.” Spec §3 wymienia `stats_reset timestamptz` na 14+. Zgodne co do obecności i typu. |

### GUC przy tych kolumnach

| Wiersz | PG14 | PG15 | PG16 | Status |
|---|---|---|---|---|
| `pg_stat_statements.track` | `top`, `all` albo `none`. Domyślnie `top`. | To samo. | To samo. | fakt. Spec §3: domyślnie `top`. Zgodne. |
| `pg_stat_statements.track_planning` | Domyślnie `off`. Czasy planów i `plans` są wtedy zero. | To samo. | To samo. | fakt. Spec §3: czasy planów są 0, chyba że GUC jest włączony. Zgodne. |
| `pg_stat_statements.track_utility` | Domyślnie `on`. Utility to wszystko poza `SELECT`, `INSERT`, `UPDATE` i `DELETE`. | Domyślnie `on`. Lista wyłączeń dodaje `MERGE`. | Domyślnie `on`. Lista wyłączeń dodaje `MERGE`. | fakt. Spec §3 w nawiasie podaje utility jako nie-`SELECT`/`INSERT`/`UPDATE`/`DELETE`, bez `MERGE`. Zgodne z PG14. Rozjazd dla PG15 i PG16. |
| Sklejanie utility | „Utility commands … are compared strictly on the basis of their textual query strings.” Plannable: `SELECT`, `INSERT`, `UPDATE` i `DELETE`. | To samo zdanie o tekście utility. Plannable dodaje `MERGE`. | „Plannable queries … and utility commands are combined” według wewnętrznego hasha. Zdania o ścisłym porównaniu tekstu utility na tej stronie nie ma. | fakt różnicy stron. Spec §3, §6 i §7 tego nie rozstrzygają. Ta macierz nie projektuje tożsamości wpisu. |
| `track_io_timing` | `blk_read_time` i `blk_write_time` są zero, gdy wyłączone. | To samo, oraz `temp_blk_read_time` i `temp_blk_write_time`. | To samo co PG15. | fakt. Spec §3 mówi o zerze dla `blk_read_time` / `blk_write_time`. Zgodne. O kolumnach `temp_blk_*_time` spec milczy; rozjazd jak w tabeli kolumn. |

### Widoczność `queryid` i `query`

| Wiersz | PG14 | PG15 | PG16 | Status |
|---|---|---|---|---|
| Kto widzi tekst i `queryid` cudzych zapytań | „only superusers and members of the `pg_read_all_stats` role are allowed to see the SQL text and `queryid` of queries executed by other users.” | „only superusers and roles with privileges of the `pg_read_all_stats` role are allowed to see the SQL text and `queryid` of queries executed by other users.” | To samo zdanie co PG15. | fakt. Spec §6 mówi „membership”. PG14 mówi „members of the `pg_read_all_stats` role”. PG15 i PG16 mówią „roles with privileges of”. Rozjazd sformułowania jest na PG15 i PG16. Te strony nie definiują różnicy zwrotów. |
| Czy inni widzą statystyki | „Other users can see the statistics, however, if the view has been installed in their database.” | To samo zdanie. | To samo zdanie. | fakt. Spec §6: niesuperuser widzi statystyki wierszy, a tekst i `queryid` tylko swoich zapytań. Zdanie źródła potwierdza, że statystyki są widoczne. Nie mówi „all rows” tymi słowami. |
| Wartość ukryta: null albo brak wiersza | Strona nie mówi, czy ukryte `query` i `queryid` są NULL, czy wiersz znika. | Strona tego nie mówi. | Strona tego nie mówi. | `UNPROVEN`. Warunek: odczyt widoku jako rola bez uprawnień `pg_read_all_stats`, dla wiersza innego użytkownika, na jawnym celu; wynik ma pokazać, czy `queryid` i `query` są NULL, czy wiersza nie ma. Tego odczytu nie wykonano. |
| Null `query` z `showtext` | Funkcja `pg_stat_statements(showtext boolean)`. `showtext := false` pomija tekst: argument `OUT` odpowiadający kolumnie `query` zwraca null. | To samo. | To samo. | fakt. To nie jest reguła uprawnień. To zdanie nie wymienia `queryid`. |
| Null `query` po odrzuceniu tekstów | Teksty reprezentatywne leżą w pliku dyskowym i nie zużywają shared memory. Gdy plik urośnie nadmiernie, moduł może odrzucić teksty: istniejące wpisy pokazują null w `query`, a statystyki przy `queryid` zostają. | To samo. | To samo, plus osobne zdanie niżej. | fakt. Spec §3 nazywa `query` nullable. Zgodne z tym zdaniem. |
| Stałe w tekście przy dealloc | Tego akapitu nie ma. | Tego akapitu nie ma. | „Queries on which normalization can be applied may be observed with constant values in `pg_stat_statements`, especially when there is a high rate of entry deallocations.” Rada: zwiększyć `pg_stat_statements.max`. | fakt tylko PG16. |

### `pg_stat_statements_reset`

Sygnatura na F.30.3, F.32.3 (PG15) i F.32.3 (PG16) jest jedna: `pg_stat_statements_reset(userid Oid, dbid Oid, queryid bigint) returns void`.

Zdanie funkcji, to samo na trzech stronach: odrzuca statystyki `pg_stat_statements` odpowiadające podanym `userid`, `dbid` i `queryid`. Parametr pominięty dostaje domyślne `0`(invalid); resetowane są statystyki pasujące do pozostałych parametrów. Brak parametrów albo wszystkie parametry `0`(invalid) odrzuca wszystkie statystyki. „If all statistics in the `pg_stat_statements` view are discarded, it will also reset the statistics in the `pg_stat_statements_info` view.” Domyślnie tylko superuser; innym można nadać `GRANT`.

Przykład w F.30.5, F.32.5 (PG15) i F.32.5 (PG16) woła `pg_stat_statements_reset()`, `pg_stat_statements_reset(0,0,s.queryid)` oraz `pg_stat_statements_reset(0,0,0)`. Przykład nie czyta `pg_stat_statements_info`.

| Wiersz | Co mówi źródło | Status |
|---|---|---|
| Reset całości | Brak parametrów albo wszystkie `0` odrzuca wszystkie statystyki widoku i resetuje statystyki widoku `pg_stat_statements_info`. | fakt. Strona nie rozdziela w tym zdaniu `stats_reset` i `dealloc`. |
| Skutek resetu całości dla `stats_reset` osobno i dla `dealloc` osobno | `stats_reset` jest opisane jako czas ostatniego resetu wszystkich statystyk widoku. `dealloc` jest opisane jako licznik wyrzuceń z powodu `pg_stat_statements.max`, nie jako skutek resetu. Zdanie funkcji mówi tylko, że statystyki widoku info są resetowane, gdy odrzucono wszystkie statystyki. | `UNPROVEN`, czy pełny reset ustawia `stats_reset`, zeruje `dealloc`, czy robi oba. Warunek: odczyt obu kolumn `pg_stat_statements_info` przed i po `pg_stat_statements_reset()` bez argumentów na jawnym celu. Tego odczytu nie wykonano. |
| Reset bazy | Reguła parametrów: podane `dbid` przy pozostałych `0` resetuje statystyki pasujące do tego `dbid`. Strona nie ma osobnej sygnatury „reset bazy”. | fakt co do reguły parametrów. |
| Skutek resetu bazy dla `stats_reset` i `dealloc` | Zdanie o resecie widoku info jest warunkowe: tylko gdy odrzucone są wszystkie statystyki widoku. Reset jednej bazy tego warunku sam nie wypowiada. | `UNPROVEN`. Warunek: odczyt `pg_stat_statements_info` przed i po `pg_stat_statements_reset` z `dbid` jednej bazy i zerami dla `userid` oraz `queryid`, na jawnym celu, przy statystykach także w innej bazie. Tego odczytu nie wykonano. |
| Reset jednego `queryid` | Wywołanie z `queryid` odrzuca statystyki odpowiadające podanym parametrom. Przykład `pg_stat_statements_reset(0,0,s.queryid)` zostawia na liście inne zapytania z ich poprzednimi `calls`. | fakt: reset jednego `queryid` nie jest opisany jako odrzucenie wszystkich statystyk widoku. Przykład nie pokazuje `stats_reset` ani `dealloc`. |
| Skutek resetu jednego `queryid` dla `stats_reset` i `dealloc` | Strona nie mówi, że taki reset aktualizuje `stats_reset`, zeruje `dealloc` albo rusza tylko liczniki wpisu. Warunek „if all statistics … are discarded” nie jest wprost zastosowany do jednego `queryid`. | `UNPROVEN`. Warunek dowodu: odczyt `pg_stat_statements_info` przed i po `pg_stat_statements_reset` z konkretnym `queryid` na jawnym celu. Tego odczytu nie wykonano. |
| Spec §7 a reset selektywny | Spec §7 zalicza do zmiany `stats_reset` ręczny `pg_stat_statements_reset`, „full or selective”. | rozjazd. Otwarte strony nie mówią, że reset selektywny zmienia `stats_reset`. Komórka skutku zostaje `UNPROVEN`, nie faktem specyfikacji. |

### `dealloc` i `pg_stat_statements.max`

| Wiersz | PG14 | PG15 | PG16 | Status |
|---|---|---|---|---|
| Znaczenie `max` | „the maximum number of statements tracked by the module (i.e., the maximum number of rows in the `pg_stat_statements` view).” Domyślnie 5000. Tylko przy starcie serwera. | To samo. | To samo. | fakt. Spec §3: domyślnie 5000, tylko przy starcie. Zgodne. |
| Wyrzucenie wpisu | „If more distinct statements than that are observed, information about the least-executed statements is discarded.” Liczbę takich odrzuceń widać w `pg_stat_statements_info`. | To samo. | To samo. | fakt. To jest znaczenie `dealloc` z tabeli info. Spec §3 mówi, że przepełnienie wyrzuca najmniej wykonywane wpisy i zwiększa `info.dealloc`. Zgodne z tym opisem. |
| Skutek delty po wyrzuceniu | Strona nie mówi o delcie per wpis ani o częściowej delcie. | Strona nie mówi. | Strona nie mówi. Dodaje tylko, że przy częstym dealloc tekst może zachować stałe. | fakt źródła kończy się na wyrzuceniu i liczniku. Spec §7 dopowiada, że delty per wpis po eksmisji są „silently partial”. Tego skutku strona nie opisuje. Ta macierz nie projektuje delty. |
| `pg_stat_statements.save` | Czy zapisać statystyki przez wyłączenie serwera. `off`: nie zapisuje przy wyłączeniu i nie wczytuje przy starcie. Domyślnie `on`. | To samo. | To samo. | fakt. Spec §7: domyślnie `on`, `off` gubi statystyki przy wyłączeniu. Zgodne. Bez reguły delty. |

### Odwołania lokalne

| Odwołanie | Co otwarto | Status |
|---|---|---|
| `docs/superpowers/specs/2026-09-26-postgres-statement-diagnostics.md` §3 | Podzbiór kolumn PG14, `toplevel` i `pg_stat_statements_info` na 14+, osiem `jit_*` od PG15, zera przy wyłączonym `track_planning` i `track_io_timing`, `track` domyślnie `top`, `track_utility` bez `MERGE`, `max` 5000. | hipoteza. Zgodna z tabelami tam, gdzie wiersze wyżej mówią „zgodne”. Rozjazd: brak `temp_blk_read_time` i `temp_blk_write_time`; `track_utility` bez `MERGE` na PG15 i PG16. |
| Ta sama specyfikacja §6 | Niesuperuser widzi statystyki, a tekst i `queryid` cudzych zapytań wymagają superusera albo membership `pg_read_all_stats`. | hipoteza. Widoczność statystyk jest zgodna ze zdaniem „Other users can see the statistics”. Membership jest zgodne ze sformułowaniem PG14 i rozjeżdża się ze sformułowaniem PG15 i PG16. Wartość ukryta (null albo brak wiersza) nie jest w §6 rozstrzygnięta i na stronach jest `UNPROVEN`. |
| Ta sama specyfikacja §7 | Każda zmiana `stats_reset`, także selektywny `pg_stat_statements_reset`, psuje ciągłość. Wzrost `dealloc` to eksmisja pod `pg_stat_statements.max`. | hipoteza. Opis `dealloc` i `max` jest zgodny ze stroną. Zdanie, że reset selektywny zmienia `stats_reset`, nie jest zdaniem otwartych stron. Rozjazd; skutek zostaje `UNPROVEN`. |
| `src/SqlHarness.Mcp/McpOperationMapper.cs` | `BuildCapabilities`: `pg` jest prawdziwe, gdy `engine` to `"postgres"`. Dokument capabilities dostaje `QueryStoreAvailable: !pg`. | fakt kodu, punkt odniesienia. Na Postgresie flaga Query Store jest fałszywa. To nie jest odczyt `pg_stat_statements` i nie jest nową capability. |

### URL-e otwarte

Strony modułu:

- https://www.postgresql.org/docs/14/pgstatstatements.html
- https://www.postgresql.org/docs/15/pgstatstatements.html
- https://www.postgresql.org/docs/16/pgstatstatements.html

Wersja rozszerzenia i upgrade:

- https://www.postgresql.org/docs/14/sql-createextension.html
- https://www.postgresql.org/docs/15/sql-createextension.html
- https://www.postgresql.org/docs/16/sql-createextension.html
- https://www.postgresql.org/docs/14/sql-alterextension.html
- https://www.postgresql.org/docs/15/sql-alterextension.html
- https://www.postgresql.org/docs/16/sql-alterextension.html
- https://www.postgresql.org/docs/16/extend-extensions.html
- https://www.postgresql.org/docs/14/pgupgrade.html
- https://www.postgresql.org/docs/15/pgupgrade.html
- https://www.postgresql.org/docs/16/pgupgrade.html
- https://www.postgresql.org/docs/14/catalog-pg-extension.html
- https://www.postgresql.org/docs/15/catalog-pg-extension.html
- https://www.postgresql.org/docs/16/catalog-pg-extension.html
- https://www.postgresql.org/docs/14/view-pg-available-extensions.html
- https://www.postgresql.org/docs/15/view-pg-available-extensions.html
- https://www.postgresql.org/docs/16/view-pg-available-extensions.html

Plik kontrolny, tagi odpowiadające `.0` i stopce dokumentacji:

- https://raw.githubusercontent.com/postgres/postgres/REL_14_0/contrib/pg_stat_statements/pg_stat_statements.control
- https://raw.githubusercontent.com/postgres/postgres/REL_14_24/contrib/pg_stat_statements/pg_stat_statements.control
- https://raw.githubusercontent.com/postgres/postgres/REL_15_0/contrib/pg_stat_statements/pg_stat_statements.control
- https://raw.githubusercontent.com/postgres/postgres/REL_15_19/contrib/pg_stat_statements/pg_stat_statements.control
- https://raw.githubusercontent.com/postgres/postgres/REL_16_0/contrib/pg_stat_statements/pg_stat_statements.control
- https://raw.githubusercontent.com/postgres/postgres/REL_16_15/contrib/pg_stat_statements/pg_stat_statements.control

Odwołania lokalne:

- `docs/superpowers/specs/2026-09-26-postgres-statement-diagnostics.md`
- `src/SqlHarness.Mcp/McpOperationMapper.cs`

## Sekwencja połączenia

Ta sekcja jest krokiem T2. Macierzy nie przepisuje. Nie dodaje komend, stubów ani capabilities. Nie projektuje rankingu, delty ani listy plików. Nie uruchamiano live DB ani `dotnet test`.

Kolejność jest jedna: resolve, auth, identity, wersja serwera, wersja rozszerzenia, potem SQL zależny od kolumn. Wyjścia ustala istniejący `OperationFailureMapper.Map`. Numery są w `SqlHarnessExitCode`. Adapter nie dostaje własnego klasyfikatora. `QueryStoreAvailable: !pg` w `src/SqlHarness.Mcp/McpOperationMapper.cs` zostaje. Ta sekwencja nie jest nową capability.

1. **Resolve.** `TargetResolver.Resolve` w `src/SqlHarness.Core/Targets/TargetResolver.cs` buduje `ResolvedTarget` z profilu albo ze ścieżki `--unsafe-direct`. `TargetProfile` w `src/SqlHarness.Core/Targets/TargetProfile.cs` nie ma pola wersji serwera. `ResolvedTarget` też jej nie niesie: ma `Server`, `Database`, `Auth`, `Mode`, `Engine` i `Transport`. Silnik bierze `SqlEngineNames.Parse`. Pominięty `engine` zostaje `SqlEngine.SqlServer` i od kroku auth nie wchodzi w tę sekwencję. Postgres wymaga `AuthStrategy.Sql`. Inna strategia, nieznany profil, złe `--var` i zły transport są `SqlHarnessSafetyException` przed otwarciem połączenia. Mapper daje exit `2` (`SqlHarnessExitCode.Safety`). Podłogi PostgreSQL 14 nie da się sprawdzić na profilu, bo wersji tam nie ma.

2. **Auth.** `EngineSessionFactory` kieruje `SqlEngine.Postgres` do `NpgsqlSessionFactory.ConnectAsync`. W `ConnectAsync` najpierw działa `PostgresConnectionString.Build`. Brak użytkownika albo pusta lub nieobecna zmienna hasła to `SqlHarnessSafetyException` przed `NpgsqlConnection.OpenAsync`. Mapper daje exit `2`, nie exit `3`, także gdy wołający ustawił już `OperationPhase.Authentication`. Komunikat może nazwać zmienną. Nie zawiera wartości hasła. Samo `OpenAsync` jest uwierzytelnieniem. `NpgsqlException` w fazie `Authentication` mapuje się na exit `3` (`SqlHarnessExitCode.Authentication`). To jest przed potwierdzeniem tożsamości i przed odczytem rozszerzenia. Po nieudanym `OpenAsync` sesja nie wraca do wołającego.

3. **Identity.** Po udanym otwarciu, nadal wewnątrz `ConnectAsync`, `ReadIdentityAsync` wykonuje `PostgresPing.IdentitySql`: `current_database()` i `COALESCE(inet_server_addr()::text, 'localhost')`. To nie jest `server_version` i nie jest wersja rozszerzenia. Komentarz w `NpgsqlSessionFactory` zostawia `inet_server_addr()` na raporcie. O tym, czy cel się zgadza, decyduje ustanowione połączenie w `PostgresEndpointIdentity.Matches`. Fałsz kończy się `SqlTargetMismatchException` z `src/SqlHarness.Core/SqlExecution.cs`. Mapper daje exit `4` (`SqlHarnessExitCode.TargetMismatch`) niezależnie od fazy. `ConnectAsync` w `catch` zamyka sesję przez `DisposeAsync` i rzuca dalej. Exit `3` i exit `4` są oba przed próbą rozszerzenia. `NpgsqlException` zapytania tożsamości, dopóki faza wołającego to nadal `Authentication`, zostaje exit `3`. To samo dotyczy braku wiersza tożsamości: `ReadIdentityAsync` rzuca wtedy `InvalidOperationException`, a gałąź mappera dla fazy `Authentication` też daje exit `3`. Nie jest to exit `4` ani exit `5`.

4. **Wersja serwera.** Odczyt wersji nie wchodzi do `PostgresPing.IdentitySql` i nie wchodzi do `ConnectAsync`. Dopóki faza wołającego to `Authentication`, `NpgsqlException` tego odczytu mapowałby się na exit `3`. Wersja pada dopiero, gdy `ConnectAsync` zwróci sesję i wołający ustawi `OperationPhase.Sql`, tak jak dzisiejsze operacje w `SqlHarnessModule`. Odczyt idzie istniejącym `ISqlSession.ExecuteReaderAsync`. SQL to `SELECT current_setting('server_version_num')`. Preset `server_version_num` jest typu `integer` (`PG_VERSION_NUM`). `current_setting` zwraca `text`. Porównanie używa liczby sparsowanej niezmiennie kulturowo, nie napisu `server_version`. Kodowanie jest to samo co `PQserverVersion`: major razy `10000` plus minor. Strona libpq podaje wersję 11.0 jako `110000`, więc próg PostgreSQL 14.0 to `140000`. Podłoga: liczba mniejsza niż `140000`. Exit zostaje `2`. Wyjątek to `SqlHarnessSafetyException`, żeby ten sam mapper dał `Safety`, a nie błąd SQL. Wzorzec `qstop` tego nie jest: `ExecuteQueryStoreTopAsync` rzuca `InvalidOperationException` po połączeniu, w fazie `Sql`, i domyślna gałąź mappera daje exit `5`. Faza podłogi nie wraca do `Validation` ani do `Authentication`. Gdy odczyt rzuci `NpgsqlException` albo tekst nie jest liczbą, exit to `5` (`SqlHarnessExitCode.SqlExecution`) i sekwencja staje. To nie jest `SqlHarnessSafetyException` i nie jest podłoga. Przy podłodze połączenie zostało otwarte i zamknięte. Sesja zwrócona przez `ConnectAsync` jest zwalniana (`await using`, `NpgsqlSession.DisposeAsync` zamyka `NpgsqlConnection`) zanim wyjdzie exit `2`. Do wersji rozszerzenia i do SQL kolumn ta ścieżka nie dochodzi.

5. **Wersja rozszerzenia.** Osobny odczyt po udanej podłodze, nadal w fazie `Sql`. Kolumna to `pg_available_extensions.installed_version` przy `name` równym `pg_stat_statements`. To nie jest `server_version`, nie jest `server_version_num` i nie jest `default_version`. Macierz: `installed_version` jest NULL, gdy rozszerzenie nie jest zainstalowane. `pg_extension.extversion` jest nazwą wersji, gdy wiersz istnieje. `default_version` z pliku kontrolnego to `1.9` na PostgreSQL 14 oraz `1.10` na PostgreSQL 15 i 16. Zainstalowana wersja może zostać w tyle za serwerem, bo `ALTER EXTENSION … UPDATE` jest osobno od upgrade serwera. NULL `installed_version` albo brak wiersza `pg_stat_statements` to brak rozszerzenia w rozstrzygniętej bazie. To niepowodzenie, nie pusta lista i nie exit `0`. Przyczyna jest osobna od braku uprawnień. Exit to `5`. To nie jest `SqlHarnessSafetyException`, bo tamto dałoby exit `2`. Nowej gałęzi mappera nie ma: wyjątek w fazie `Sql`, którego mapper nie bierze jako safety, mismatch ani auth, daje `SqlExecution`. Sekwencja nie woła `CREATE EXTENSION` ani `ALTER EXTENSION`. SQLSTATE `42P01` i `42883` nie opisują tego kroku. Macierz pokazuje brak instalacji jako NULL `installed_version`, nie jako brak relacji. Zostają wyłącznie hipotezą specyfikacji §2, gdyby późniejsze polecenie i tak dotknęło brakującego widoku lub funkcji. Ta sekwencja do takiego polecenia nie przechodzi, gdy katalog już pokazał brak instalacji.

6. **Probe.** SQL, który wymienia kolumny widoku, stoi za oboma odczytami. Nie startuje, gdy podłoga odrzuciła serwer, gdy wersja rozszerzenia nie wróciła, albo gdy rozszerzenia nie ma. Lista kolumn jest listą macierzy dla głównej wersji serwera. Nie jest funkcją samego `extversion` ani samego `server_version_num`. Para, którą macierz opisuje wprost: serwer 14 z zainstalowanym `1.9` używa kolumn PG14 i nie wymienia `temp_blk_read_time`, `temp_blk_write_time` ani `jit_functions`, `jit_generation_time`, `jit_inlining_count`, `jit_inlining_time`, `jit_optimization_count`, `jit_optimization_time`, `jit_emission_count`, `jit_emission_time`. Serwer 15 albo 16 z zainstalowanym `1.10` może użyć kolumn, które macierz oznacza jako obecne dla tej wersji, w tym tych dwóch kolumn I/O i tej ósemki `jit_*`. Każda inna para, także serwer 15 lub 16 z wersją inną niż `1.10` oraz serwer nowszy niż 16, nie dostaje SQL kolumn. Kształt takiej pary jest `UNPROVEN`. Warunek: odczyt nazw kolumn widoku na jawnym celu dla tej pary. Tego odczytu nie wykonano. Brak uprawnień przy odczycie katalogu albo przy tym późniejszym SQL jest osobnym niepowodzeniem. Też exit `5`, nigdy pusta lista sukcesu. SQLSTATE `42501` zostaje hipotezą specyfikacji §2 i §9. Mapper nie czyta `SqlState` i tego numeru nie obala: `NpgsqlException` w fazie `Sql` i tak jest exit `5`. Klasyfikatora, który zmienia exit według SQLSTATE, nie dodajemy. Treść błędu nazywa klasę przyczyny i nie zawiera tekstu SQL. Probe nie woła `pg_stat_statements_reset`. Zmiana `stats_reset` nie jest dowodem resetu jednego `queryid`. Ten skutek w macierzy jest `UNPROVEN`. Probe nie agreguje `queryid` i nie rozstrzyga, czy ukryty `queryid` jest NULL, czy brakiem wiersza. To zostaje `UNPROVEN` i należy do rankingu, nie tutaj.

Specyfikacja §9 kładzie brak rozszerzenia i brak uprawnień na ten sam exit `5`. Ta sekcja ten numer zachowuje i rozdziela przyczyny. Czytelny widok z zerem wierszy nie jest żadną z tych dwóch przyczyn. Specyfikacja §9 nazywa taki czytelny pusty wynik exit `0`. Ta sekcja tego raportu nie projektuje. Exit `6`, `7` i `8` ta sekwencja nie rusza. Nowego kodu wyjścia nie ma.

Źródła odczytu wersji serwera, otwarte w tym kroku:

- https://www.postgresql.org/docs/14/runtime-config-preset.html (`server_version` jest `string`, `server_version_num` jest `integer`)
- https://www.postgresql.org/docs/14/libpq-status.html (`PQserverVersion`: major razy `10000` plus minor; wersja 11.0 to `110000`)

Odwołania lokalne tego kroku:

- `src/SqlHarness.Core/Targets/TargetResolver.cs`
- `src/SqlHarness.Core/Targets/TargetProfile.cs`
- `src/SqlHarness.Core/SqlEngine.cs`
- `src/SqlHarness.Core/EngineSessionFactory.cs`
- `src/SqlHarness.Core/Postgres/PostgresConnectionString.cs`
- `src/SqlHarness.Core/Postgres/NpgsqlSessionFactory.cs`
- `src/SqlHarness.Core/Postgres/PostgresPing.cs`
- `src/SqlHarness.Core/Postgres/PostgresEndpointIdentity.cs`
- `src/SqlHarness.Core/SqlExecution.cs`
- `src/SqlHarness.Core/OperationFailureMapper.cs`
- `src/SqlHarness.Core/Contracts.cs`
- `src/SqlHarness.Core/SqlHarnessModule.cs`
- `src/SqlHarness.Mcp/McpOperationMapper.cs`
- `docs/superpowers/specs/2026-09-26-postgres-statement-diagnostics.md`, sekcje 2 i 9, jako hipoteza tam, gdzie kroki 5 i 6 jej nie obalają

## Osobny kontrakt od qstop

Ta sekcja jest krokiem T3. Macierzy i sekwencji nie przepisuje. Nie dodaje komend, stubów ani capabilities. Nie projektuje delty ani listy plików. Nie uruchamiano live DB ani `dotnet test`.

`qstop` zostaje kontraktem SQL Server. W `ExecuteQueryStoreTopAsync` silnik `SqlEngine.Postgres` dostaje `InvalidOperationException` z tekstem „Query Store is available only on SQL Server.” Faza jest już `Sql`, więc mapper daje exit `5` (`SqlHarnessExitCode.SqlExecution`). `QueryStoreAvailable: !pg` w `src/SqlHarness.Mcp/McpOperationMapper.cs` zostaje. Ten kontrakt nie jest zmianą `qstop` i nie jest nową capability.

### Czego ten kontrakt nie bierze z `qstop`

| Z `qstop` | Źródło w kodzie | Ten kontrakt |
|---|---|---|
| `--window`, `windowMinutes` (1..44640 minut, domyślnie `24h`) | `QueryStoreTopCommand.Settings`, `QueryStoreWindowParser`, predykat `DATEADD` w `QueryStoreTopQuery.Sql` | Nie ma `--window`. Liczniki są skumulowane. Brak `--window` jest granicą kontraktu. |
| `lastExecutionAt` | `MAX(rs.last_execution_time)`; pole `QueryStoreTopItemReport.LastExecutionAt` | Nie ma pola recency. W macierzy kolumn widoku nie ma czasu na wpis. |
| `totalCpuMilliseconds`, `averageCpuMilliseconds`, `maximumCpuMilliseconds` | Drugi klucz `ORDER BY` i pola raportu | Nie ma metryki CPU. Kontrakt nie wstawia zera, żeby kształt wyglądał jak `qstop`. `CpuTimeMs` równe 0 na `measure` i `compare` nie jest polem tego raportu. |
| `queryHash` | Kolumna Query Store i pole linii `queries.jsonl` | Widok nie ma tej kolumny. Kontrakt jej nie dorabia z tekstu. |
| `querySqlText` | Trzecie pole linii; `RequiredText` odrzuca null | Linia niesie `query`. Null w `query` nie jest błędem odczytu. |
| `totalLogicalReads`, `averageLogicalReads`, `maximumLogicalReads`, `objectName`, `planCount` | Pola `QueryStoreTopItemReport` | Nie wchodzą. Odczyty tutaj to liczniki bloków widoku, nie strony logical reads. |

Porządek `qstop` to `total_duration_milliseconds` DESC, `total_cpu_milliseconds` DESC, `execution_count` DESC, `query_id` ASC. Ten kontrakt tego porządku nie używa. Rekord `SqlHarnessQueryStoreTopReport` nie jest rekordem tego kontraktu: nie ma tu `windowMinutes`.

Nazwy JSON linii `qstop` to `queryId`, `queryHash`, `querySqlText` (`QueryTextArtifact` przy `JsonSerializerDefaults.Web`; test `QueryStoreArtifactWriterTests` sprawdza tę trójkę). Z tej trójki ten kontrakt zostawia pisownię `queryId`. Tekst linii nazywa się `query`, nie `querySqlText`.

### Filtr bieżącej bazy

Strony modułu: gdy `pg_stat_statements` jest aktywne, śledzi statystyki we wszystkich bazach serwera. Kolumna `dbid` to „OID of database in which the statement was executed” i odwołuje się do `pg_database.oid`. Sam widok nie jest filtrem bieżącej bazy.

Filtr tego kontraktu jest po stronie serwera, przed agregacją. Zostają wiersze, których `dbid` jest OID bazy tego połączenia. OID jest wierszem `pg_database`, którego `datname` jest `current_database()` tej sesji. Porównanie jest OID do OID. Klient nie podstawia nazwy. Nie ma argumentu użytkownika z nazwą bazy: ani flagi, ani osobnego `--var`, ani nazwy z profilu wstawionej jako predykat. Tożsamość sesji została sprawdzona w sekwencji. Ten filtr nie jest drugim sprawdzeniem endpointu.

Wiersz z innym `dbid` nie jest pozycją i nie dodaje się do pozycji o tym samym numerze `queryid`. Strony mówią, że kolizja hasha nie scala zapytań należących do różnych baz w jeden wpis widoku. Ten kontrakt i tak nie sumuje baz.

### Znany `queryid`

Wpis widoku, z macierzy, to jedna kombinacja database ID, user ID, query ID i tego, czy polecenie jest top-level. Pozycja tego kontraktu nie jest wpisem. Pozycja jest jednym znanym `queryid` w bazie filtra.

Znany `queryid` to nie-null `bigint` zwrócony w wierszu po filtrze. Pole raportu nazywa się `queryId`: pisownia specyfikacji §5 i JSON `qstop`. Wartość jest tą liczbą, ze znakiem. Nie ma wartości bezwzględnej i nie ma drugiego identyfikatora.

`queryid` nie jest kluczem między serwerami. Strony: hash z drzewa po analizie składni; drop i odtworzenie tabeli rozdziela wpisy; wynik zależy od architektury; nie jest stabilny między wersjami głównymi; replika logiczna nie zachowuje użyteczności `queryid` do sumowania kosztów. Pozycja jest kluczem w tym serwerze, w tej wersji głównej i w tej bazie połączenia. To nie jest delta i nie jest porównaniem między serwerami.

### Ukryty albo null `queryid`

Ukrycie zostaje `UNPROVEN`, wierszem macierzy „Wartość ukryta: null albo brak wiersza”. Strona nie mówi, czy `query` i `queryid` cudzego zapytania są NULL, czy wiersz znika. PG14: „only superusers and members of the `pg_read_all_stats` role”. PG15 i PG16: „only superusers and roles with privileges of the `pg_read_all_stats` role”. Inni użytkownicy widzą statystyki, jeśli widok jest zainstalowany w ich bazie. Warunek dowodu zostaje ten z macierzy. Tego odczytu nie wykonano. Ta sekcja nie dodaje pola, które wymagałoby rozstrzygnięcia kształtu.

Reprezentacja bez wymyślonego identyfikatora:

- Brak wiersza nie jest pozycją.
- NULL w `queryid` nie jest znanym kluczem. `QueryStoreTopQuery.RequiredInt64` nie zamienia null na `long`. Null nie staje się `QueryId`. Tutaj NULL też nie staje się pozycją.
- Wiersze bez znanego `queryid` nie są scalane. Nie łączy ich wspólny null, tekst `query`, `userid`, `toplevel`, numer wiersza ani `queryHash`. Tekst nie jest kluczem.
- Liczba `0` w `pg_stat_statements_reset` znaczy parametr invalid. Nie jest identyfikatorem nieznanych wierszy. Nie-null `queryid` równe `0`, gdyby widok je zwrócił, byłoby znaną wartością `bigint`, nie wspólną pozycją ukrytych.
- Takie wiersze nie wchodzą do porządku, nie zajmują miejsca w `--top` i nie dostają linii `queries.jsonl`. Nieznane id nie tworzą jednej pozycji ani jednej pary agregacji. Delta jest poza tą sekcją.

Widoczność statystyk w widoku nie tworzy pozycji bez znanego `queryid`. Raport nie jest zrzutem każdego wiersza widoku.

`qstop` przy null `query_id` przerywa cały odczyt (`InvalidOperationException`). Ten kontrakt tego rzutu nie kopiuje. Macierz zostawia kształt ukrycia jako `UNPROVEN` i mówi, że statystyki mogą być widoczne. Exit `5` na cały odczyt uznałby ten kształt za błąd. Odczyt, który doszedł do rankingu i nie dał żadnego znanego `queryid`, jest pustą listą pozycji.

Pusta lista pozycji po udanym odczycie to exit `0` (`SqlHarnessExitCode.Success`). Nie jest brakiem rozszerzenia i nie jest brakiem uprawnień. Te dwa zostają exit `5` w sekwencji, zanim ranking wystartuje. Pusta lista nie twierdzi, że widok nie miał wierszy. Twierdzi, że w bazie filtra nie było pozycji o znanym `queryid`. Specyfikacja §8 i §9 nazywa czytelny brak wierszy exit `0`. Ta sekcja zostawia ten numer także dla listy bez znanego `queryid`.

Ścieżka sukcesu `ExecuteQueryStoreTopAsync` zapisuje artefakt po `ReadAsync` i nie ma osobnej gałęzi dla zera metryk. Pusta lista `qstop` też jest exit `0` z artefaktem. Ten kontrakt robi to samo dla pustej listy pozycji: artefakt jest, `queries.jsonl` nie ma linii.

### Agregacja

Agregacja dotyczy tylko wierszy o tym samym znanym `queryid` po filtrze bazy. `userid` i `toplevel` nie rozcinają pozycji. Kilka wierszy widoku o tym samym `queryid` staje się jedną pozycją. Powtórzenie `queryid` przed agregacją nie jest błędem. `qstop` rzuca przy zduplikowanym `query_id` w zbiorze metryk (`RequireMatchingTexts`), bo jego SQL już zgrupował plany. Tutaj ziarno widoku jest drobniejsze niż pozycja, więc ten rzut nie obowiązuje. Po agregacji jest jedna pozycja na jeden znany `queryid`. Pozycja nie liczy własnego hasha. Bierze `queryid` z widoku.

Formuły są hipotezą specyfikacji §4. Kolumny są w zbiorze PG14, więc wolno je wybrać i przy zainstalowanym `1.9` na PostgreSQL 14, i przy zainstalowanym `1.10` na PostgreSQL 15 albo 16. `total_exec_time` jest już w milisekundach. Dzielenia przez 1000 z SQL `qstop` nie ma.

- `executionCount` = `SUM(calls)`
- `totalDurationMs` = `SUM(total_exec_time)`
- `maximumDurationMs` = `MAX(max_exec_time)`
- `averageDurationMs` = `totalDurationMs / SUM(calls)`, tylko gdy `SUM(calls)` nie jest 0
- `totalPlanMs` = `SUM(total_plan_time)`
- `maximumPlanMs` = `MAX(max_plan_time)`
- `averagePlanMs` = `totalPlanMs / SUM(calls)`, tylko gdy `SUM(calls)` nie jest 0
- `rowsReturned` = `SUM(rows)`
- `sharedBlocksHit` = `SUM(shared_blks_hit)`
- `sharedBlocksRead` = `SUM(shared_blks_read)`
- `localBlocksRead` = `SUM(local_blks_read)`
- `localBlocksWritten` = `SUM(local_blks_written)`
- `tempBlocksRead` = `SUM(temp_blks_read)`
- `tempBlocksWritten` = `SUM(temp_blks_written)`
- `walBytes` = `SUM(wal_bytes)`
- `blockReadMs` = `SUM(blk_read_time)`
- `blockWriteMs` = `SUM(blk_write_time)`
- `toplevelOnly` jest true wtedy i tylko wtedy, gdy każdy wiersz tej pozycji ma `toplevel` true

`tempBlocksRead` i `tempBlocksWritten` są licznikami `temp_blks_read` i `temp_blks_written`. Nie są czasami `temp_blk_read_time` i `temp_blk_write_time`.

`mean_exec_time`, `mean_plan_time`, `min_*` i `stddev_*` nie są polami pozycji. Średnia wiersza nie jest średnią pozycji. Specyfikacja §4 mówi też ogólnie o sumie liczników odczytu, zapisu, buforów i WAL. Lista pól jest węższa i jest listą ze specyfikacji §5. Kolumny spoza tej listy nie dostają pól. W szczególności nie dostają ich `shared_blks_dirtied`, `shared_blks_written`, `local_blks_hit`, `local_blks_dirtied`, `wal_records` i `wal_fpi`.

Specyfikacja dzieli przez `SUM(calls)` i nie podaje wyniku dla zera. Strona mówi, że `plans` i `calls` nie zawsze się zgadzają, bo statystyki planu i wykonania aktualizują się osobno. Przy `SUM(calls) = 0` kontrakt nie podstawia 0, null ani `mean_*`. Pól `averageDurationMs` i `averagePlanMs` w tej pozycji nie ma. To nie jest exit `5`. Klucz rankingu i tak jest sumą `total_exec_time`, nie średnią.

Tekst linii jest kolumną `query`, verbatim, bez obcięcia. Null, gdy serwer zwrócił null, zostaje null. Macierz: po odrzuceniu tekstów `query` jest null, a statystyki przy `queryid` zostają. To nie jest błąd. Null z `pg_stat_statements(showtext := false)` jest osobnym faktem macierzy i nie jest tekstem linii. Odczyt z `showtext := false` nie jest źródłem `queries.jsonl`.

Gdy każdy wiersz pozycji ma `query` null, linia ma null. Gdy każdy wiersz ma ten sam nie-null tekst, linia ma ten tekst. Porównanie tekstu jest równością tej treści, nie kolejnością wierszy. Gdy wartości się różnią, w tym gdy obok tekstu jest null, wybór „pierwszego” wiersza zależałby od skanu. Tego wyboru nie ma. Strona wiąże resztę tekstu z pierwszym zapytaniem, które miało dany `queryid` przy tym wpisie, nie z pozycją złożoną z kilku `userid` albo z obu `toplevel`. Który tekst należy do takiej pozycji, jest `UNPROVEN`. Warunek: odczyt `query` dla jednego znanego `queryid` przy dwóch `userid` albo przy obu wartościach `toplevel`, na jawnym celu. Tego odczytu nie wykonano. Do tego czasu linia zachowuje `queryId` równe temu znanemu `queryid`, ze znakiem (`bigint`), i ustawia tylko `query` na null. Nie zawiera żadnego z tych tekstów. Ten null nie jest twierdzeniem, że serwer zwrócił null.

### Ranking

Ranking jest po kroku 6 sekwencji: wersja serwera i zainstalowana wersja rozszerzenia są już znane. SQL kolumn jest tylko dla PostgreSQL 14 z zainstalowanym `1.9` oraz dla PostgreSQL 15 albo 16 z zainstalowanym `1.10`. Inna para nie dostaje SQL kolumn. Ta sekcja nie nazywa jej kodu wyjścia i nie daje jej porządku. Zostaje to przy sekwencji. Braku wiersza rozszerzenia ta sekcja też nie poprawia.

Klucze używają tylko kolumn, które obie dozwolone pary mogą wybrać. Nie ma wśród nich `temp_blk_read_time`, `temp_blk_write_time` ani `jit_functions`, `jit_generation_time`, `jit_inlining_count`, `jit_inlining_time`, `jit_optimization_count`, `jit_optimization_time`, `jit_emission_count`, `jit_emission_time`. Sekwencja przy parze `1.10` może te kolumny odczytać. Ten kontrakt nie używa ich w porządku i nie wstawia ich do stdout.

Porządek jest porządkiem pozycji po agregacji, nie porządkiem wierszy widoku. Klucze, hipoteza specyfikacji §4, dają pełny porządek:

1. `SUM(total_exec_time)` malejąco
2. `SUM(calls)` malejąco
3. `SUM(shared_blks_hit) + SUM(shared_blks_read)` malejąco
4. `queryid` rosnąco

Każda pozycja ma jeden znany `queryid`, więc czwarty klucz rozstrzyga remis wcześniejszych. Dwie pozycje nie mają równych czterech kluczy. Kolejność wierszy, `userid` i tekst nie są kluczami. Remis nie zależy od kolejności wierszy.

Zostaje co najwyżej `--top` pierwszych pozycji tego porządku. Zakres to 1..500, ten sam co `OperationLimits.IsTop` (`TopMin` 1, `TopMax` 500) i sprawdzenie w `ExecuteQueryStoreTopAsync`. Ta sekcja nie przenosi domyślnego `20` z `QueryStoreTopCommand.Settings`. Nie stawia `--window` obok `--top`. `--top` w tej sekcji jest cięciem listy, nie nową capability i nie nową komendą.

Liczniki pozycji są wartościami widoku po agregacji. Kontrakt nic od nich nie odejmuje. Nie nadaje im czasu trwania. Skutek resetu selektywnego dla `stats_reset` zostaje `UNPROVEN` z macierzy. Ta sekcja nie projektuje delty.

### Stdout i `queries.jsonl`

Stdout pozycji ma metryki i `queryId`. Nie ma pola `query`, tekstu SQL, `queryHash`, CPU, recency ani `windowMinutes`. Nazwy są pisownią specyfikacji §5: `queryId`, `toplevelOnly`, `executionCount`, `totalDurationMs`, `averageDurationMs`, `maximumDurationMs`, `totalPlanMs`, `averagePlanMs`, `maximumPlanMs`, `rowsReturned`, `sharedBlocksHit`, `sharedBlocksRead`, `localBlocksRead`, `localBlocksWritten`, `tempBlocksRead`, `tempBlocksWritten`, `walBytes`, `blockReadMs`, `blockWriteMs`. Specyfikacja zapisuje część z nich skrótem ze slashem (`sharedBlocksHit/Read`, `localBlocksRead/Written`, `tempBlocksRead/Written`, `blockReadMs/blockWriteMs`, `totalPlanMs / averagePlanMs / maximumPlanMs`). Rozwinięcie jest tą listą, nie nowym polem. Średnie znikają tylko przy `SUM(calls) = 0`, jak wyżej.

Kontekst raportu, nie pozycja:

- `server_version_num` już odczytany w sekwencji. To nie jest napis `server_version` i nie jest `installed_version`. Specyfikacja §5 mówi „server version string”. Ta sekcja zwęża to do liczby, którą sekwencja już ma. `installed_version` zostaje progiem sekwencji i nie zastępuje tej liczby na raporcie.
- `pg_stat_statements.track`, `pg_stat_statements.track_utility`, `pg_stat_statements.track_planning`, w pisowni GUC. Wartość jest tą z serwera. Nie ma skróconego aliasu.
- `dealloc` i `stats_reset`, w pisowni kolumn widoku info. `stats_reset` nie jest `lastExecutionAt` i nie jest etykietą „ostatnie 24h”.

Te odczyty kontekstu stoją za tym samym progiem co probe: po znanej wersji serwera i znanej zainstalowanej wersji rozszerzenia. Niepowodzenie w fazie `Sql` jest exit `5` istniejącym mapperem. Nowej gałęzi nie ma. Odczyty nie wołają `pg_stat_statements_reset` i nie interpretują zmiany `stats_reset`.

`artifactDirectory` jest nazwą JSON pola `ArtifactDirectory` z raportu `qstop`: katalog artefaktu, bez tekstu SQL w nazwie pliku. Układ katalogu i nazwy typów należą do planu plików, nie tutaj. Raport nie przyjmuje nazwy bazy jako argumentu rankingu.

Tekst SQL jest tylko w lokalnym `queries.jsonl`. Jedna linia na pozycję, która weszła do wyniku. Linia ma tylko `queryId` i `query`. Stdout, stderr, treść błędu i nazwy plików tekstu nie zawierają. Linia jest lokalnie wrażliwa tak jak `queries.jsonl` przy `qstop`: bez wklejania i bez publikacji bez przeglądu. Nie wchodzi do gain. Specyfikacja §6 stawia tę samą granicę.

Niepowodzenie zapisu artefaktu jest exit `6` (`SqlHarnessExitCode.LocalStorage`). W `ExecuteQueryStoreTopAsync` faza `Artifact` mapuje wyjątek na ten numer, a wynik nie niesie raportu. Ten kontrakt zostawia to samo: exit `6` bez raportu.

### Odwołania tego kroku

- `src/SqlHarness.Core/QueryStoreTop.cs`
- `src/SqlHarness.Core/QueryStoreArtifacts.cs`
- `src/SqlHarness.Core/Contracts.cs` (`QueryStoreTopItemReport`, `SqlHarnessQueryStoreTopReport`, `SqlHarnessExitCode`)
- `src/SqlHarness.Core/SqlHarnessModule.cs` (`ExecuteQueryStoreTopAsync`)
- `src/SqlHarness.Core/OperationLimits.cs`
- `src/SqlHarness.Cli/Commands/QueryStoreTopCommand.cs`
- `src/SqlHarness.Mcp/McpOperationMapper.cs`
- `tests/SqlHarness.Tests/QueryStoreArtifactWriterTests.cs` (nazwy pól linii `qstop`)
- `docs/superpowers/specs/2026-09-26-postgres-statement-diagnostics.md`, sekcje 4, 5, 6 i 8, jako hipoteza porządku i pól stdout tam, gdzie ta sekcja ich nie zwęża
- strony modułu z macierzy, otwarte ponownie dla filtra `dbid`, tekstu reprezentatywnego i granic `queryid`: https://www.postgresql.org/docs/14/pgstatstatements.html , https://www.postgresql.org/docs/15/pgstatstatements.html , https://www.postgresql.org/docs/16/pgstatstatements.html

## Delta

Ta sekcja jest krokiem T4. Macierzy, sekwencji i sekcji qstop nie przepisuje. Nie dodaje komend, stubów ani capabilities. Nie projektuje listy plików. Nie uruchamiano live DB ani `dotnet test`. `QueryStoreAvailable: !pg` zostaje.

Specyfikacja §7 jest hipotezą. Ta sekcja zwęża ją do tego, czego dowodzi macierz. Nie wkleja jej. Macierz delty nie projektuje. Reguły niżej nie zastępują wierszy macierzy o `stats_reset`, `dealloc` i resecie.

Delta jest porównaniem dwóch udanych wyników kontraktu z sekcji qstop. Kolejność jest kolejnością wołającego: pierwszy i drugi. Nie wyznacza jej `stats_reset` ani zegar. Delta nie ma `--window` i nie ma `windowMinutes`. Nie ma etykiety „ostatnie N godzin” ani „ostatnie 24h”. Nie ma czasu trwania i nie ma ilorazu na jednostkę czasu. Liczniki zostają skumulowane. Sekcja qstop zostawia je bez odejmowania i bez czasu trwania. Ta sekcja też nie odejmuje: status nie niesie różnicy.

Niepowodzenie odczytu zostaje przy sekwencji i przy sekcji qstop. Ta sekcja nie nadaje mu statusu delty i nie zmienia numerów wyjścia. Nie woła `pg_stat_statements_reset`.

### Para

Para wymaga tego samego rozstrzygniętego celu, tej samej głównej wersji serwera i tej samej bazy filtra. Główna wersja jest ilorazem całkowitym `server_version_num` przez `10000`, tym samym kodowaniem co próg sekwencji. `queryid` nie jest kluczem między serwerami, architekturami, wersjami głównymi ani repliką logiczną. Poza tą granicą pary nie ma. To nie jest nowy identyfikator.

W tej granicy para jest jednym znanym, nie-null `queryid` obecnym w obu wynikach. Wartość jest tą liczbą `bigint`, ze znakiem. Pole zostaje `queryId`.

Nieznane albo null `queryid` nie są pozycjami. Nie są scalane między wynikami. Nie są parą. Nie łączy ich null, tekst `query`, `userid`, `toplevel`, numer wiersza ani `queryHash`. `0` w `pg_stat_statements_reset` nie jest ich identyfikatorem.

Znane `queryid` tylko w jednym wyniku nie jest parą. Brak wpisu nie jest wykrytym resetem selektywnym. Nie wstawia się zera. Ciągłość takiego id ma status `unknown`.

Rozjazd tekstu `query` zostawia znane `queryid` i ustawia tylko `query` na null, jak w sekcji qstop. Tekst nie jest przyczyną statusu. Null tekstu nie jest resetem.

Pusta lista pozycji zostaje exit `0` z sekcji qstop. Ta sekcja nie zmienia tego numeru i nie robi z pustki pary.

### Status

Tokeny statusu są dwa: `incomparable` i `unknown`. Nie mają polskich zamienników. Nie ma tokenu porównawalności.

Specyfikacja §7 mówi, że delta jest porównawalna tylko wtedy, gdy równe są trzy znaczniki: `stats_reset`, `dealloc` i `pg_postmaster_start_time()`. Macierz tego zdania nie dowodzi. To zdanie nie obowiązuje. Równość odczytów nie jest deltą porównawalną.

Przyczyny `incomparable`, i tylko te, to: reset, dealloc, restart, spadek licznika. Przyczyna, która zaszła, zostaje nazwana przy statusie. Kilka naraz nie zmienia tokenu i nie nazywa się resetem selektywnym. Gdy nie zaszła żadna, status pary jest `unknown`.

Status nie jest kodem wyjścia.

### Reset

`stats_reset` znaczy: „Time at which all statistics in the `pg_stat_statements` view were last reset.” Gdy oba wyniki mają odczyt `stats_reset` i odczyty się różnią, czas ostatniego resetu wszystkich statystyk widoku jest inny. Każda para tego porównania jest `incomparable`, przyczyna reset. Gdy par nie ma, porównanie i tak niesie tę przyczynę. Nie tworzy par z brakujących id i nie wstawia zer.

To nie jest dowód, że wołano `pg_stat_statements_reset`. Sekwencja zostawia zdanie, że zmiana `stats_reset` nie jest dowodem resetu jednego `queryid`. Ta sekcja go nie cofa. Reset jednego `queryid` nie jest opisany jako odrzucenie wszystkich statystyk widoku. Jego skutek dla `stats_reset` i `dealloc` zostaje `UNPROVEN`. Warunek jest wierszem macierzy „Skutek resetu jednego `queryid` dla `stats_reset` i `dealloc`”. Tego odczytu nie wykonano. Zdanie specyfikacji §7, że reset selektywny zmienia `stats_reset`, zostaje rozjazdem z macierzy. Nie jest faktem źródła.

Reset bazy nie jest osobną przyczyną. Jego skutek dla `stats_reset` i `dealloc` zostaje `UNPROVEN`, wierszem macierzy o tym skutku. Tego odczytu nie wykonano.

Czy pełny reset, bez argumentów albo same zera, ustawia `stats_reset`, zeruje `dealloc`, czy robi oba, zostaje `UNPROVEN`. Warunek jest wierszem macierzy „Skutek resetu całości dla `stats_reset` osobno i dla `dealloc` osobno”. Tego odczytu nie wykonano. Różnica kolumny nie rozstrzyga tej komórki. Kolumna jest jedna na cały widok, nie na bazę filtra. Przyczyna reset nie mówi, że reset dotyczył tylko tej bazy.

Gdy któregoś odczytu `stats_reset` brak, w tym gdy jest NULL, porównanie nie jest równością i nie jest różnicą. Przyczyna reset nie zachodzi. Macierz nie mówi, czy kolumna bywa NULL. To zostaje `UNPROVEN`. Warunek: odczyt `pg_stat_statements_info` na jawnym celu, który pokaże, czy `stats_reset` bywa NULL. Tego odczytu nie wykonano.

### Dealloc

`dealloc` jest łączną liczbą wyrzuceń wpisów o najmniej wykonywanych poleceniach, gdy zaobserwowano więcej różnych poleceń niż `pg_stat_statements.max`. Gdy oba wyniki mają odczyt `dealloc` i drugi jest większy, każda para jest `incomparable`, przyczyna dealloc. Gdy par nie ma, porównanie i tak niesie tę przyczynę i nie wstawia zer.

Strony nie opisują delty per wpis po tym wyrzuceniu. Nie wskazują `queryid` ani bazy. Nie ma liczb „tylko dla wpisów, które zostały”. Zdanie specyfikacji §7, że delty per wpis są wtedy „silently partial”, nie jest zdaniem źródła. Ta sekcja go nie przyjmuje.

Spadek `dealloc` nie jest tą przyczyną. Jest spadkiem licznika, niżej. Równe `dealloc` nie jest dowodem, że wpisy trwają.

Brak odczytu albo NULL nie jest wzrostem. Przyczyna dealloc nie zachodzi. Czy `dealloc` bywa NULL, jest `UNPROVEN` na tym samym warunku co NULL w `stats_reset`. Tego odczytu nie wykonano.

### Restart

`pg_postmaster_start_time()` nie jest znacznikiem ciągłości. Macierz go nie dowodzi. Ta sekcja nie otwiera strony tej funkcji i nie robi z niej trzeciego znacznika. Równość albo różnica tego odczytu sama nie jest `incomparable` i sama nie jest deltą porównawalną. Nowego tokenu ciągłości nie ma.

Przyczyna restart zachodzi tylko łącznie. Oba wyniki mają odczyt tej funkcji i te odczyty się różnią. Oba wyniki mają `pg_stat_statements.save` równe `off`. Fakt macierzy: `off` nie zapisuje statystyk przy wyłączeniu i nie wczytuje ich przy starcie. Każda para jest wtedy `incomparable`, przyczyna restart. Domyślne `on` nie jest podstawiane, gdy `save` nie zostało odczytane. Samo `off`, bez różnicy odczytu startu, nie jest tą przyczyną. Dwa odczyty `save` nie są odczytem w chwili wyłączenia. Innego faktu o restarcie macierz nie ma, więc inny układ `save` tej przyczyny nie daje.

Gdy `save` jest `on`, macierz nie podaje reguły delty, także przy różnym odczycie startu. Przyczyna restart nie zachodzi. Delta nie staje się porównawalna. Skutek jest `UNPROVEN`. Warunek: zdanie źródła albo odczyt na jawnym celu, które mówi, czy statystyki po starcie przy `save = on` są statystykami sprzed wyłączenia. Tego odczytu nie wykonano.

Brak odczytu startu nie znaczy, że restartu nie było. Nie uprawnia odejmowania. Zmiana `save` między wynikami, bez koniunkcji wyżej, nie jest przyczyną. Jej skutek dla liczników jest `UNPROVEN`. Warunek: zdanie źródła, które mówi, czy zmiana `save` bez wyłączenia rusza statystyki. Macierz go nie ma. Tego odczytu nie wykonano.

Odczyt startu i `save` nie jest polem stdout sekcji qstop. Ta sekcja nie dopisuje tam pól. Bez tych odczytów przyczyna restart nie zachodzi.

### Spadek licznika

Przyczyna spadek licznika zachodzi, gdy w kolejności wołającego drugi wynik ma mniejszą wartość niż pierwszy. Dotyczy `dealloc`, gdy obie wartości są obecne. Dotyczy pary, gdy oba wyniki mają to samo pole po agregacji z sekcji qstop i druga wartość jest mniejsza:

- `executionCount`
- `totalDurationMs`
- `maximumDurationMs`
- `totalPlanMs`
- `maximumPlanMs`
- `rowsReturned`
- `sharedBlocksHit`
- `sharedBlocksRead`
- `localBlocksRead`
- `localBlocksWritten`
- `tempBlocksRead`
- `tempBlocksWritten`
- `walBytes`
- `blockReadMs`
- `blockWriteMs`

Jedno mniejsze pole wystarcza. Para jest `incomparable` w całości. Żadne pole nie dostaje różnicy. Średnie `averageDurationMs` i `averagePlanMs` nie są tym testem. Ich spadek przy niespadających sumach nie jest tą przyczyną. Ich brak przy `SUM(calls) = 0` też nie jest. `toplevelOnly` i `query` nie są tym testem. Brak pola po jednej stronie nie jest zerem i nie jest spadkiem.

Spadek nie jest wykrytym resetem selektywnym i nie jest wykrytym resetem bazy. Nazywa obserwację, nie funkcję, której strony na `stats_reset` nie pokazują.

### Ciągłość `unknown`

Gdy dla pary nie zaszła żadna przyczyna, status jest `unknown`. Równe `stats_reset`, równe `dealloc`, brak koniunkcji restartu i liczniki, które nie są mniejsze, nie wykluczają resetu jednego `queryid` ani resetu bazy. Skutek tych resetów dla obu kolumn jest `UNPROVEN`, jak w macierzy. Tego odczytu nie wykonano. Ta równość nie jest deltą porównawalną.

Kontrakt nie obiecuje pełnego wykrycia resetu selektywnego. W macierzy nie ma metadanej resetu jednego `queryid`. Tej metadanej się nie wymyśla. Reset, którego źródło nie pokazuje, zostaje `unknown`. Nie jest wykrytym resetem i nie jest deltą porównawalną.

`unknown` też nie niesie różnicy liczników. Wzrost licznika nie jest ciągiem. Mógł powstać po resecie, którego te kolumny nie pokazują, a potem powyżej wartości z pierwszego wyniku.

### Przypadki statusu

To nie jest tabela testów implementacji. Pliki, typy i testy należą do osobnego planu, nie tutaj. Lista jest tylko statusami z tej sekcji.

| Przypadek | Obserwacja | Status |
|---|---|---|
| Różne `stats_reset` | Inny czas ostatniego resetu wszystkich statystyk widoku. To nie jest nazwa wywołania. | `incomparable`, reset |
| `dealloc` większe w drugim wyniku | Wyrzucenie pod `pg_stat_statements.max`. Bez delty per wpis. | `incomparable`, dealloc |
| Różny start i `save = off` w obu wynikach | Nie zapisano przy wyłączeniu i nie wczytano przy starcie. | `incomparable`, restart |
| `save = on`, także przy różnym starcie | Macierz nie ma reguły delty. | `unknown`; skutek `UNPROVEN` |
| Mniejszy licznik pary albo mniejszy `dealloc` | Spadek w kolejności wołającego. | `incomparable`, spadek licznika |
| Równe `stats_reset` i równe `dealloc`, liczniki nie mniejsze, restart nie zaszedł | Reset jednego `queryid` i reset bazy nie są wykluczone. | `unknown` |
| Znane `queryid` w jednym wyniku | Nie para. Brak nie jest wykrytym resetem selektywnym. | ciągłość `unknown` |
| Null albo nieznane `queryid` | Nie pozycja i nie para. Bez scalenia. | nie para |
| Rozjazd `query` | Zostaje `queryId`. Nulluje się tylko `query`. | status liczników bez zmiany z powodu tekstu |

### Odwołania

- Macierz tego pliku: `stats_reset`, `pg_stat_statements_reset`, `dealloc`, `pg_stat_statements.max`, `pg_stat_statements.save` i rozjazd specyfikacji §7. Ta sekcja tych wierszy nie zastępuje.
- Sekcja qstop tego pliku: znane `queryid`, brak pary z nieznanego id, rozjazd tekstu nulluje tylko `query`, liczniki bez odejmowania i bez czasu trwania. Zdanie, że ta sekcja nie projektuje delty, zostaje przy niej. Projekt delty jest tutaj.
- Sekwencja tego pliku: zmiana `stats_reset` nie jest dowodem resetu jednego `queryid`. To zdanie zostaje.
- `docs/superpowers/specs/2026-09-26-postgres-statement-diagnostics.md` §7, jako hipoteza zwężona powyżej. Zdanie o trzech równych znacznikach nie obowiązuje.
