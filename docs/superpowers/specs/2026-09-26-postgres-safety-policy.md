# Polityka bezpieczeństwa PostgreSQL

**Data:** 2026-09-26
**Status:** obowiązująca dla klasyfikatora i sesji Npgsql
**Dotyczy:** S3 i S4 w [audycie](2026-09-26-project-audit.md). Nie zmienia silnika SQL Server.
**Parser:** SqlParserCS 0.6.5, dialekt `PostgreSqlDialect`.

Ten dokument jest źródłem zachowania `PostgresSafetyClassifier.Classify` oraz zakresu wykonania sesji dla `SqlUsage.Query` i `SqlUsage.CompareSetup`. Nie dodaje nowego interfejsu. Nie upoważnia do zdania „read-only gwarantowane”.

## Model zagrożeń

Aktor wysyłający SQL to agent z dostępem do CLI (albo bezpośrednie wywołanie Core). Tekst batcha, parametry i wybór profilu nie są dowodem braku efektów. Zgoda na trwały DML pozostaje jednorazowa i jawna: `--allow-mutation` oraz `--confirm-database` równe rozstrzygniętej nazwie bazy. Dotyczy tylko `Query`. Setup jej nie przyjmuje.

W bazie już istnieją obiekty, których ciał nie ma w przesłanym tekście:

- funkcje, w tym `VOLATILE` wykonujące DML i funkcje `SECURITY DEFINER`;
- widoki, reguły i triggery;
- przeciążenia operatorów (`+` i inne są w AST węzłem operatora, nie wywołaniem funkcji).

Uprawnienia konta połączenia są osobną granicą. Harness nie czyta `pg_roles` ani `pg_proc` i nie wykonuje `SET ROLE`, `SET SESSION AUTHORIZATION`, `GRANT` ani `REVOKE`.

`search_path` i schemat bieżący rozstrzygają nazwy niekwalifikowane. Ta sama pisownia może wskazać inny obiekt po zmianie ścieżki. Kwalifikacja schematem w tekście też nie jest tożsamością, dopóki harness nie rozwiązuje przeciążenia po typach argumentów. Cytowanie i zwijanie wielkości liter zostają takie jak dziś: identyfikator bez cudzysłowu jest porównywany bez względu na wielkość liter, a klucz tabeli tymczasowej zwija niecytowaną nazwę do małych liter.

Trzy skutki nie są jednym zakazem:

| Skutek | Co miałoby być wykluczone | Co widzi sam tekst SQL |
| --- | --- | --- |
| Brak trwałego DML | `INSERT` / `UPDATE` / `DELETE` / `MERGE` oraz modyfikujące CTE w trwałą tabelę, a także DML schowany w funkcji, widoku, regule albo triggerze | Tylko cele zapisu obecne w AST |
| Brak efektów administracyjnych | zmiana konfiguracji sesji, anulowanie lub zabicie innego backendu, blokady doradcze, zmiana roli | Tylko wywołanie, którego końcowa nazwa jest na liście poniżej |
| Brak dostępu zewnętrznego | `dblink`, FDW, odczyt katalogu lub pliku serwera, eksport large object | Tylko widoczna nazwa z istniejących prefiksów `dblink`, `pg_read_`, `pg_ls_`, `lo_` |

## Porównanie kontroli

Żadna pojedyncza kontrola nie udźwignie wszystkich trzech skutków. Obietnica bezpieczeństwa nie opiera się na samej liście nazw ani na samej transakcji read-only. Harness nie zmienia ról ani uprawnień.

| Kontrola | Trwały DML widoczny w tekście | Trwały DML w funkcji, widoku, regule, triggerze | Efekt administracyjny | Dostęp zewnętrzny | Setup `CREATE TEMP` | Zmiana uprawnień |
| --- | --- | --- | --- | --- | --- | --- |
| Pełne AST SqlParserCS | Tak, dla celów, które parser umieścił w drzewie. Gramatyka biblioteki jest szersza niż gramatyka serwera (S4). | Nie | Tylko gdy wywołanie jest widoczne i nazwa jest objęta filtrem | Tak samo | Nie psuje | Nie |
| Zaufane funkcje po rozstrzygnięciu przeciążeń | Nie zastępuje analizy celów zapisu | Tylko dla wywołań w tekście, i tylko gdy wpis w katalogu jest prawdziwy. Właściciel może oznaczyć funkcję `IMMUTABLE`, a i tak wykonać DML. Nie widzi funkcji schowanej w widoku ani ciała operatora. Wymaga odczytu katalogu zależnego od `search_path` | Tylko przy pełnym i zaufanym katalogu | Tak samo | Wymaga dodatkowych zapytań | Nie, ale nie usuwa `SECURITY DEFINER` |
| Minimalne uprawnienia konta | Tak, jeśli rola nie może pisać | Tak dla DML wykonanego jako ta rola; nie, gdy `SECURITY DEFINER` działa jako właściciel z prawem zapisu | Tylko gdy roli brakuje `pg_signal_backend`, superusera, `pg_write_server_files` i podobnych | Tak samo | Nie psuje, jeśli rola może tworzyć tabele tymczasowe | Harness miałby sam wykonać `SET ROLE` albo `REVOKE`. Tego nie robi |
| Transakcja `READ ONLY` | Tak, także dla DML wewnątrz funkcji, bo dokumentacja PostgreSQL zabrania `INSERT` / `UPDATE` / `DELETE` / `MERGE` do tabeli, która nie jest tymczasowa | Tak dla nietymczasowego DML, również w `SECURITY DEFINER` (ograniczenie dotyczy komendy w transakcji, nie nazwy roli) | Nie. `set_config`, `pg_cancel_backend`, `pg_terminate_backend` i blokady doradcze nie są na liście komend zabronionych w transakcji read-only | Nie. Zdalny SQL i funkcje plikowe nie są tą listą | Psuje. Ta sama dokumentacja zabrania wszystkich `CREATE`, także `CREATE TEMP` | Nie |

Źródła zachowania serwera, nie wynik testu na żywo w tej zmianie: [SET TRANSACTION](https://www.postgresql.org/docs/17/sql-set-transaction.html), [WITH](https://www.postgresql.org/docs/17/queries-with.html), [funkcje administracyjne](https://www.postgresql.org/docs/17/functions-admin.html).

## Macierz decyzji

Identyfikator funkcji to ostatni składnik nazwy, bez względu na schemat i cudzysłów, porównanie bez wielkości liter. Lista jest filtrem tekstu, nie dowodem efektu. Mutacja zatwierdzona flagami nie zdejmuje filtra.

| Pozycja | Dozwolony efekt | Egzekwowanie | Komunikat odmowy |
| --- | --- | --- | --- |
| `set_config` | Żaden. Widoczne wywołanie nie może zmienić konfiguracji, w tym `search_path`, ani jako `Query`, ani jako `CompareSetup` | Odmowa po dokładnej nazwie końcowej, w każdym miejscu AST objętym przejściem z sekcji Classify | `UnsupportedStatement.` |
| `pg_cancel_backend` | Żaden. Brak sygnału do innego backendu | Dokładna nazwa końcowa | `UnsupportedStatement.` |
| `pg_terminate_backend` | Żaden. Brak zakończenia innego backendu | Dokładna nazwa końcowa | `UnsupportedStatement.` |
| Blokady doradcze | Żaden. Brak `pg_advisory_lock`, `pg_advisory_xact_lock`, `pg_try_advisory_lock`, wariantów `unlock` oraz każdej innej widocznej nazwy zaczynającej się od `pg_advisory_` albo `pg_try_advisory_` | Prefiksy `pg_advisory_` i `pg_try_advisory_` na nazwie końcowej. Prefiks obejmuje też hipotetyczną funkcję bez efektu o takiej nazwie; to nadmiarowa odmowa filtra, nie dowód efektu | `UnsupportedStatement.` |
| Funkcje sekwencji | `nextval` i `setval`: żaden (zmiana stanu sekwencji). `currval` i `lastval`: klasyfikator dopuszcza wywołanie. Ich efektem widocznym w dokumentacji jest odczyt stanu sesji, nie zapis sekwencji. To nie jest gwarancja braku skutków przy przesłonięciu nazwy | `nextval` i `setval` są na liście dokładnych nazw. `currval` i `lastval` nie są. Brak rozwiązania przeciążenia | Dla `nextval` i `setval`: `UnsupportedStatement.` Dla `currval` i `lastval`: brak odmowy |
| Funkcja `VOLATILE` wykonująca DML | Żaden efekt nie jest dopuszczony jako „skutek uboczny SELECT”. Klasyfikator nie widzi ciała ani volatility. `SELECT app.write_something()` pozostaje `Allowed`, `HasMutation=false` | Brak listy nazw i brak transakcji read-only. Jedyna kontrola poza tekstem to uprawnienia konta, których harness nie zmienia i nie sprawdza | Brak odmowy. Decyzja `Allowed`. Etykieta raportu `read-only` nie jest gwarancją |
| `SECURITY DEFINER` | Jak wyżej: atrybut nie występuje w tekście wywołania. Dozwolony efekt nie jest „żaden potwierdzony”; efekt właściciela pozostaje poza AST | Nie da się wymusić z samego wywołania. Harness nie robi `SET ROLE` i nie odbiera uprawnień właścicielowi | Brak odmowy. Decyzja `Allowed` |
| Funkcja w widoku | Jak wyżej. `SELECT * FROM app.hidden_view` nie zawiera wywołania | Skan relacji nie jest rozwijany do definicji widoku | Brak odmowy. Decyzja `Allowed` |
| Funkcja o nazwie `lo_` bez efektów | Żaden przez ten filtr. `lo_custom_readonly` jest odrzucane tak samo jak `lo_export` | Prefiks `lo_` na nazwie końcowej. Nazwa nie świadczy o braku efektu; zwężenie prefiksu bez granicy katalogu udawałoby taki dowód. To świadoma nadmiarowa odmowa (audyt B4), nie podatność | `UnsupportedStatement.` |

Te same reguły nazw obejmują dotychczasowe pozycje, których ta zmiana nie luzuje: dokładne `pg_sleep`, `pg_read_file`, `pg_ls_dir`, `lo_import`; prefiksy `dblink`, `pg_read_`, `pg_ls_`. Dotyczą wyrażenia funkcyjnego, funkcji w `FROM` i funkcji tabelowej. Literał tekstowy `'set_config'` nie jest wywołaniem.

Komunikat odmowy nie zawiera tekstu SQL ani argumentów. `RejectionDescription` przy pustym `Detail` to `"{Reason}."`.

## Wybrana polityka i granica zaufania

Polityka nazywa się **efekty widoczne w tekście, sesja bez dokładanych komend**.

Granica jest rozstrzygnięta i dlatego zdanie „read-only gwarantowane” jest fałszywe:

1. **Egzekwowane.** Przed połączeniem klasyfikator odrzuca błąd parsowania, każde niedozwolone wywołanie z macierzy oraz każdy cel zapisu umieszczony w AST. Trwały cel w `Query` wymaga dotychczasowego kontraktu mutacji. `CompareSetup` nie przyjmuje trwałego zapisu. Trwały DDL pozostaje `UnsupportedStatement` i flagi mutacji go nie odblokowują.
2. **Egzekwowane jako brak ingerencji.** Sesja Npgsql wykonuje już sklasyfikowany tekst bez opakowania. Nie ustawia transakcji read-only, `search_path`, roli ani uprawnień.
3. **Poza harnessem.** Skutki funkcji, widoków, operatorów, reguł, triggerów i `search_path` ogranicza wyłącznie rola konta przygotowana poza SQLHarness. Tej roli ta zmiana nie tworzy i nie sprawdza. Brak tej zewnętrznej granicy jest powodem, dla którego punkt 1 i 2 nie składają się w gwarancję read-only.

Etykieta JSON z `ClassificationLabel` zostaje bez zmiany znaczenia i bez zmiany tekstu: `mutation`, `session-local` albo `read-only`. Słowo `read-only` znaczy „brak widocznej mutacji i brak pracy lokalnej sesji”. Nie znaczy braku efektów ukrytych. Tej etykiety ten dokument nie przerabia, bo jest wspólna dla obu silników.

Transakcja read-only nie wchodzi do implementacji. Sama nie blokuje wierszy administracyjnych ani dostępu zewnętrznego, a `CREATE TEMP` jest `CREATE`, więc setup przestałby spełniać kontrakt tabel tymczasowych albo wymagałby osobnych rund i innej transakcji dla DDL. To zwiększyłoby liczbę wywołań i nadal nie domknęłoby granicy zaufania.

Rozwiązanie przeciążeń po katalogu nie wchodzi do tej zmiany. Bez zaufanego wpisu i bez rozwinięcia widoków oraz operatorów byłoby kolejną listą, nie granicą.

## Classify

`Classify(sql, usage, database, allowMutation, confirmDatabase, sessionTempTables)`:

1. Parsuje cały batch. Wyjątek parsera daje `ParseError`. Pusty batch daje `UnsupportedStatement`. Inne użycie niż `Query` i `CompareSetup` daje `UnsupportedStatement`.
2. Przechodzi każde zdanie. Przejście biblioteki nie wchodzi w `TableFactor.Derived`, w zapytanie `CREATE TABLE AS`, w filtr `DELETE`, w element `CREATE TABLE` ani w wyrażenia `CREATE INDEX`. Element `CREATE TABLE` nie jest `IElement`, więc sam `Visit` nie widzi wartości domyślnej kolumny, wyrażenia `GENERATED`, `CHECK` kolumny i tabeli, list `WITH` / `OPTIONS` / `TBLPROPERTIES` (wartość `SqlOption` jest wyrażeniem), `ORDER BY` ani innych wyrażeń tego elementu. Predykat `WHERE`, wyrażenie klucza i lista `WITH` indeksu też zostają poza tym przejściem. Klasyfikator wchodzi w te miejsca sam, tym samym filtrem nazw, rekurencyjnie, oraz w miejsca, które biblioteka już odwiedza (lista `SELECT`, CTE, źródło `INSERT`, `IN` i skalarny podselekt, gdy biblioteka je odwiedza). Widoczne wywołanie w `CREATE TEMP` odrzuca cały batch, także gdy dalej jest `INSERT … DEFAULT VALUES`.
3. Widoczne wywołanie z macierzy odrzuca cały batch jako `UnsupportedStatement`, także przy `allowMutation=true` i także w `CompareSetup`.
4. Potem klasyfikuje zdania po kolei. Nazwy `TEMP` utworzone w tym batchu dopisuje do zbioru zanim oceni cele zapisu tego zdania, żeby późniejsze zdanie widziało tabelę z wcześniejszego `CREATE TEMP`.

Cel zapisu to cel `INSERT`, `UPDATE`, `DELETE` albo `MERGE` znaleziony w zdaniu, w CTE, w źródle `INSERT` i w podzapytaniu, do którego klasyfikator wchodzi. Cel bez rozwiązywalnej nazwy relacji daje `UnsupportedStatement`. `SELECT INTO` daje `SelectIntoNotAllowed` w `Query` (także ze zgodą na mutację) i `NonTemporaryWrite` w `CompareSetup`.

Gdy wszystkie znalezione cele są sesyjnie lokalne, zdanie jest pracą lokalną. Gdy choć jeden nie jest, zdanie jest mutacją, nawet jeśli równocześnie tworzy tabelę tymczasową. Brak celów w `SELECT` bez `INTO` to odczyt tekstowy (`Allowed`, bez mutacji i bez pracy lokalnej).

Cel jest sesyjnie lokalny, gdy pierwszy identyfikator to `pg_temp` albo prefiks `pg_temp_`, albo gdy nazwa jest jednym identyfikatorem obecnym w zbiorze tabel tymczasowych tej sesji i tego batcha. Prefiks w nazwie nie wystarcza.

`Query` przy mutacji: brak `allowMutation` daje `MutationNotAllowed`; brak `confirmDatabase` daje `DatabaseConfirmationRequired`; inna nazwa bazy, porównanie porządkowe, daje `DatabaseConfirmationMismatch`; zgodna para flag daje `Allowed` z `HasMutation=true`. Praca lokalna ustawia `HasSessionLocalWork` i zwraca zbiór nazw tymczasowych.

`CompareSetup` ignoruje flagi mutacji. Odczyt tekstowy i praca wyłącznie tymczasowa są dozwolone. Mutacja, `SELECT INTO` i nietymczasowy zapis dają `NonTemporaryWrite`. Trwały DDL i nieznane zdanie dają `UnsupportedStatement`.

## Sesja: Query i CompareSetup

Oba użycia mają ten sam zakres wykonania:

- `NpgsqlSession` ustawia `CommandText` na SQL przekazany w `SqlExecutionCommand`, `CommandType=Text` i istniejący timeout. Nie dopisuje `BEGIN`, `SET TRANSACTION`, `SET`, `SET ROLE` ani innego prefiksu.
- Łańcuch połączenia nie ustawia `default_transaction_read_only`, `search_path`, `Options` zmieniających rolę ani trybu transakcji.
- Po `Open` nadal jest tylko dotychczasowe zapytanie tożsamości. Polityka nie dodaje rundy.
- `Query` wysyła jeden sklasyfikowany batch. `CompareSetup` wysyła jeden sklasyfikowany batch setupu na tym samym połączeniu, które potem służy powtórzeniom. Obiekt tymczasowy z setupu pozostaje widoczny.
- `measure` i `compare` dalej owijają mierzone zdanie w `EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON)` i, gdy porównują wiersze, wykonują niemierzony sidecar. To istniejące wywołania silnika, nie nowe wywołania tej polityki.

Liczba wywołań serwera nie rośnie. Klasyfikacja jest lokalna.

## Setup i tabele tymczasowe

Bez flag mutacji nadal wolno, w `Query` i w `CompareSetup`:

- `CREATE TEMP` / `CREATE TEMPORARY`, także `CREATE TEMP TABLE AS SELECT` bez celu zapisu w zapytaniu źródłowym;
- `INSERT` / `UPDATE` / `DELETE` / `MERGE` oraz modyfikujące CTE, których każdy cel jest sesyjnie lokalny;
- `CREATE INDEX` i `DROP` tych tabel oraz `DROP` indeksu oznaczonego jako tymczasowy.

`UNLOGGED` i `CREATE TABLE` bez `TEMP` pozostają trwałym DDL. Zapis do `pg_temp_fake` nadal jest traktowany jak lokalny, bo kwalifikator zaczyna się od `pg_temp_`; to istniejąca reguła nazwy schematu tymczasowego, nie nowa obietnica.

Batch tymczasowy, który dodatkowo zawiera widoczny trwały cel zapisu, nie jest „tylko lokalny”. W setupie jest `NonTemporaryWrite`. W `Query` wymaga kontraktu mutacji.

## S4 — pokrycie AST, nie exploit

PostgreSQL pozwala na modyfikujące CTE tylko w `WITH` dołączonym do zdania najwyższego poziomu (`SELECT`, `INSERT`, `UPDATE`, `DELETE`, `MERGE`). `WITH` stoi przed tym zdaniem. Przykład z dokumentacji przenosi wiersze przez `WITH deleted AS (DELETE …) INSERT INTO …`, a nie przez `INSERT … WITH …`.

Następujące kształty SqlParserCS przyjmuje, a ten dokument nie nazywa ich obejściem serwera:

- `INSERT INTO x WITH changed AS (INSERT INTO public.items …) SELECT …` — biblioteka widzi oba cele. Serwerowa gramatyka nie umieszcza `WITH` między `INSERT` a źródłem. Klasyfikator i tak odrzuca trwały cel.
- `CREATE TEMP TABLE x AS WITH changed AS (INSERT INTO public.items …) SELECT …` — biblioteka widzi `INSERT` w zapytaniu `AS`. `CREATE TABLE AS` nie jest zdaniem, do którego dokumentacja dołącza modyfikujące `WITH`. Klasyfikator i tak traktuje `public.items` jako mutację.
- `CREATE TEMP TABLE x AS WITH changed AS (DELETE FROM public.items …) SELECT …` — biblioteka zwraca błąd parsowania. Decyzja `ParseError` jest odmową składni biblioteki, nie dowodem wykonania na serwerze.

Kształt zgodny z dokumentacją serwera, `WITH changed AS (INSERT INTO public.items …) SELECT …` albo `WITH changed AS (…) INSERT INTO …`, pozostaje zwykłą mutacją, gdy cel nie jest sesyjnie lokalny. Odmowa tego kształtu była prawdziwa już przed tą polityką i nie jest „potwierdzonym exploitem S4”.

Testy offline mają nazywać pierwsze dwa kształty składnią przyjętą przez bibliotekę i nie twierdzić, że serwer je wykonał. Testów funkcji i tabel tymczasowych na bazie ta zmiana nie wykonuje. Nie wolno dopisywać syntetycznego wyniku takiego przebiegu.