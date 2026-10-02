# Kontrakt pg_stat_statements — macierz źródeł

Status: macierz źródeł (T1).

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
