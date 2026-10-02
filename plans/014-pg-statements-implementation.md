# Plan wykonawczy pgstop (014/T5)

Status funkcji: **PLANNED**. Ten dokument nie jest wdrożeniem. Nie dodaje komend, stubów, testów ani capabilities. Nie zmienia kodu. Nie zmienia `plans/014-pg-statements-contract.md`, `plans/README.md` ani `plans/014-postgres-diagnostics-design.md`.

Data: 2026-10-02. Gałąź: `docs/plan-014-postgres-diagnostics`. Baza tego planu: `7334c17`. Autorytet zachowania: `plans/014-pg-statements-contract.md`. Sekwencja jest T2, osobność od `qstop` jest T3, delta jest T4, macierz wersji rozszerzenia jest T1. Wersja serwera nie jest wersją rozszerzenia.

Specyfikacja `docs/superpowers/specs/2026-09-26-postgres-statement-diagnostics.md` §10 i §11 jest szkicem plików i fixture. Tam, gdzie kontrakt zwęził obietnicę, obowiązuje kontrakt. Spec §7 (delta porównawalna i trzy znaczniki) oraz spec §9 (odrzucenie serwera poniżej 14 przed połączeniem) nie są tym planem.

Zadania poniżej są przyszłe. Ten commit żadnego nie wykonuje. Nie uruchamia `dotnet test` ani live DB.

## Decyzje, które zadania niosą

Nie są nowym projektem. Zadanie, które je rusza, nie wybiera innego kształtu.

- Kolejność jest jedna: resolve, auth, identity, potem `server_version_num`, potem `pg_available_extensions.installed_version`, potem SQL kolumn. Podłoga `140000` jest exit `2` (`SqlHarnessExitCode.Safety`) dopiero po otwarciu i zamknięciu połączenia. Exit `3` to auth. Exit `4` to `SqlTargetMismatchException`. Exit `5` to brak rozszerzenia, brak uprawnień albo zły odczyt wersji. Exit `6` to zapis artefaktu. Nowego `SqlHarnessExitCode` nie ma. `OperationFailureMapper` nie dostaje gałęzi.
- SQL kolumn jest tylko dla PostgreSQL 14 z zainstalowanym `1.9` oraz dla PostgreSQL 15 albo 16 z zainstalowanym `1.10`. Inna para nie dostaje SQL kolumn. Kształt zostaje `UNPROVEN`. Nie jest exit `0` i nie jest pustym sukcesem.
- Pozycja jest jednym znanym, nie-null `queryid`. Nieznane id nie są scalane i nie dostają wymyślonego id. Rozjazd tekstu zostawia `queryId` i ustawia tylko `query` na null.
- Nie ma `--window`, pola recency ani metryki CPU. Tekst SQL jest tylko w lokalnym `queries.jsonl`. Stdout ma metryki i identyfikatory.
- Tokeny delty są tylko `incomparable` i `unknown`. Nie ma odejmowania i nie ma ścieżki porównawalnej. Reset selektywny nie jest wykrywany. `pg_postmaster_start_time()` nie jest znacznikiem ciągłości. Przyczyna restart zachodzi tylko wtedy, gdy oba odczyty mają `pg_stat_statements.save` równe `off` i czas startu się różni.
- Nie ma `CREATE EXTENSION`, `ALTER EXTENSION`, zmiany GUC ani wywołania `pg_stat_statements_reset`.
- `QueryStoreAvailable: !pg` w `src/SqlHarness.Mcp/McpOperationMapper.cs` zostaje. Ten dokument tej linii nie zmienia. Żadne zadanie poniżej też jej nie zmienia: flaga nie jest odczytem `pg_stat_statements`.

## Nity odłożone

Ledger: `D:\Dev\sqlharness\.superpowers\sdd\014-postgres-diagnostics-design\progress.md`, wiersze `minor (deferred)`. Tekst kontraktu zostaje. Zadanie, które ich dotyka, nie poszerza go i nie edytuje kontraktu.

- Brak wiersza `pg_available_extensions` wobec NULL `installed_version` (kontrakt, linia 203). Zadanie realizuje zdanie kontraktu: NULL albo brak wiersza to brak rozszerzenia, exit `5`. Nie rozdziela tych dwóch przypadków i nie oznacza braku wiersza jako nowego `UNPROVEN`.
- Para inna niż 14+`1.9` i 15/16+`1.10` nie ma numeru wyjścia (kontrakt, linia 205). Zadanie nie dopisuje „to nie jest exit 0” do kontraktu. Ścieżka i tak nie jest exit `0` ani pustym sukcesem. Numeru nie nazywa.
- `track_io_timing` obok `blockReadMs` / `blockWriteMs` (kontrakt, linia 349). Kontekst zostaje przy trzech GUC z kontraktu. Zadanie nie dopisuje `track_io_timing`.
- Trzy nity tabeli delty (kontrakt, linie 427, 482 i 487). Zadanie nie dopisuje „and no other cause”, nie dopisuje słowa `dealloc` do warunku NULL i nie zamienia komórki „nie para” na token statusu. Obowiązuje proza sekcji Delta, nie poszerzona tabela.

## Korekta szkicu §10 i §11

| Szkic | Ten plan |
|---|---|
| §9: serwer poniżej 14 odrzucony przed połączeniem | Odrzucenie jest exit `2` po `ConnectAsync`, gdy sesja została już zamknięta. Faza nie wraca do `Validation` ani do `Authentication`. |
| §2 i §10: brak rozszerzenia jako SQLSTATE `42P01` / `42883` | Probe katalogu. NULL `installed_version` albo brak wiersza. Do widoku, którego nie ma, ta ścieżka nie przechodzi. `42P01` i `42883` zostają hipotezą specyfikacji. |
| §9 i §10: `42501` zmienia exit | `OperationFailureMapper` nie czyta `SqlState`. `NpgsqlException` w fazie `Sql` jest exit `5`. Klasyfikatora według SQLSTATE nie dodajemy. |
| §10 `reset-between-snapshots`: `comparable: false` i brak arytmetyki per zapytanie | Token `incomparable` i nazwana przyczyna. Różnica liczb nie powstaje. Tokenu porównawalności nie ma. |
| §10: jeden kształt 35 kolumn i „extra `jit_*` ignorowane” przy `SELECT *` | Dwa SQL kolumn, bo kolumny zależą od wersji rozszerzenia, nie od samego serwera. Nie ma `SELECT *`. |
| §5 i §11: ranking przez `showtext := false`, tekst tylko dla top-N | `showtext := false` nie jest źródłem `queries.jsonl`. Tekst linii jest kolumną `query` widoku. |
| §7: trzy równe znaczniki dają deltę porównawalną | To zdanie nie obowiązuje. Równość odczytów nie jest deltą porównawalną. |
| §11: jedna paczka z probe, info, GUC i `pg_postmaster_start_time()` | Osobne odczyty, w kolejności sekwencji. Komenda nie woła `pg_postmaster_start_time()` i nie czyta `save` po to, żeby mieć znacznik ciągłości. |
| §11: reklama w `Capabilities.cs` razem z plikami | Reklama jest osobnym zadaniem po istnieniu komendy. Ten dokument jej nie wykonuje. |
| §11: nazwa komendy do ewentualnej zmiany | Nazwa jest `pgstop`. |

## Granice, których nie ruszamy

Safety zostaje w Core. `PostgresSafetyClassifier` w `src/SqlHarness.Core/Postgres/PostgresSafetyClassifier.cs` jest klasyfikatorem SQL użytkownika. Ta diagnostyka nie przyjmuje SQL użytkownika i nie dostaje drugiej kopii klasyfikatora w CLI ani w MCP. Wyjścia zostają przy `OperationFailureMapper.Map` oraz przy fazie `Artifact`, która już dziś daje `SqlHarnessExitCode.LocalStorage`. Plan 009 tej granicy nie przerabia.

Scope artefaktów jest modelem planu 003: właściciel to `{ profile, vars-kanoniczne, engine, server, database }` z `plans/003-scope-contract.md`. Brak właściciela oznacza odmowę MCP. Sama nazwa bazy i sama nazwa katalogu nie są autoryzacją. Lokalny pisarz procesu jest poza granicą integralności. Ten plan nie dodaje rodzaju do `ArtifactReader.SupportedKinds` i nie dodaje sekcji obok `summary`, `metrics`, `operators`. `queries.jsonl` nie wchodzi do `sqlharness_artifact` i nie wchodzi do gain. `GainRecord` nie dostaje tekstu `query`.

Gate operacji DB jest bramą planu 004: `McpExecutionGate.IsDbTool` i `RunDbAsync` dla `sqlharness_inspect`. Drugie równoległe wywołanie DB dostaje `busy`. Tej bramy nie zastępujemy kolejką i nie dokładamy drugiej. Nowy inspect kind, jeśli powstanie, jest zadaniem 6. Ten etap nie wpisuje go do katalogu.

`qstop` zostaje kontraktem SQL Server. `ExecuteQueryStoreTopAsync` i tekst „Query Store is available only on SQL Server.” nie są tą zmianą.

## Pliki, których jeszcze nie ma

Tylko te:

1. `src/SqlHarness.Core/Postgres/PgStatementTopQuery.cs` — stałe SQL i wybór SQL kolumn. Bez I/O i bez połączenia.
2. `src/SqlHarness.Core/Postgres/PgStatementTop.cs` — rola Reader: `PgStatementTop.ReadAsync` na `ISqlReader`, agregacja, ranking i czysta delta. Osobnego pliku Reader nie ma. To jest ten sam podział co `QueryStoreTopQuery.ReadAsync`, nie nowy klasyfikator.
3. `src/SqlHarness.Core/PgStatementArtifactWriter.cs` — `PgStatementArtifactWriter` i `IPgStatementArtifactWriter`.
4. `src/SqlHarness.Cli/Commands/PgStatementTopCommand.cs` — komenda `pgstop`.
5. `tests/SqlHarness.Tests/PgStatementTopTests.cs` — fixture, error, empty, reset i version na `FakeReader` oraz na szwie modułu z `QueryStoreTopTests`.
6. `tests/SqlHarness.Tests/Cli/PgStatementTopCommandTests.cs` — dispatch CLI, obok `QueryStoreTopCommandTests`.

Innych nowych plików ten plan nie wprowadza. Nie ma pliku skill, nie ma nowego narzędzia MCP i nie ma magazynu migawek delty.

## Edycje istniejących plików

To nie są pliki zestawu. Bez nich komenda nie wejdzie w istniejący rdzeń.

- `src/SqlHarness.Core/Contracts.cs`: `SqlHarnessPgStatementTopOperation(SqlTargetRequest Target, int Top, int TimeoutSeconds)`. Nie ma `WindowMinutes`. `PgStatementTopReport` i `PgStatementTopItemReport`. To nie jest `SqlHarnessQueryStoreTopReport`.
- `src/SqlHarness.Core/SqlHarnessPaths.cs`: `PgStatementsDir` = `Path.Combine(Home, "pg-statements")`.
- `src/SqlHarness.Core/SqlHarnessModule.cs`: nowa gałąź obok `ExecuteQueryStoreTopAsync`. Pisarz idzie konstruktorem tak jak `IQueryStoreArtifactWriter`. Zachowanie `qstop` zostaje.
- `src/SqlHarness.Cli/SqlHarnessCli.cs`: w `SqlHarnessCli.Create`, przy rejestracji `qstop`, linia `c.AddCommand<PgStatementTopCommand>("pgstop")`. Ten commit tej linii nie dodaje.
- `src/SqlHarness.Cli/Commands/Renderer.cs`: osobne ramię tekstowe. Nie używa `RenderQueryStoreTop`.

Zadania 1–4 nie edytują `src/SqlHarness.Core/Capabilities.cs`, `src/SqlHarness.Mcp/McpOperationMapper.cs`, `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs`, `AGENTS.md`, `README.md` ani `docs/mcp.md`.

## SQL

Stałe, porównywane w teście jako tekst. Nie idą przez `PostgresSafetyClassifier`.

- Wersja: `SELECT current_setting('server_version_num')`. Liczba jest parsowana niezmiennie kulturowo. Próg mniejszy niż `140000` to podłoga.
- Rozszerzenie: `SELECT installed_version FROM pg_available_extensions WHERE name = 'pg_stat_statements'`. To nie jest `default_version` i nie jest `server_version_num`.
- Kontekst, dopiero po dozwolonej parze: `current_setting` dla `pg_stat_statements.track`, `pg_stat_statements.track_utility` i `pg_stat_statements.track_planning`, oraz `dealloc` i `stats_reset` z `pg_stat_statements_info`. Bez `track_io_timing`. Bez `pg_stat_statements.save`. Bez `pg_postmaster_start_time()`.
- SQL kolumn jest jednym z dwóch stałych napisów albo żadnym. `PgStatementTopQuery.ColumnSql` zwraca null, gdy pary nie ma. Null nie jest sukcesem.

Filtr bazy jest w SQL, przed agregacją: `dbid` równe `oid` z `pg_database`, którego `datname` jest `current_database()` tej sesji. Porównanie jest OID do OID. Klient nie podstawia nazwy bazy. Nie ma parametru nazwy.

Para 14 i `1.9` wymienia z nazwy kolumny macierzy PG14 i nie wymienia `temp_blk_read_time`, `temp_blk_write_time`, `jit_functions`, `jit_generation_time`, `jit_inlining_count`, `jit_inlining_time`, `jit_optimization_count`, `jit_optimization_time`, `jit_emission_count`, `jit_emission_time`.

Para 15 albo 16 i `1.10` może te nazwy wymienić. Jedna stała obsługuje obie główne wersje. Reader nie robi z nich pól ani kluczy rankingu.

Porównanie `installed_version` jest równością ordinalną z dokładnie `1.9` albo dokładnie `1.10`. Główna wersja jest ilorazem całkowitym `server_version_num` przez `10000`. Serwer 15 z `1.9` i serwer 14 z `1.10` nie dostają SQL kolumn.

Zakazane w każdym stałym SQL: `CREATE EXTENSION`, `ALTER EXTENSION`, `pg_stat_statements_reset`, `set_config`, `ALTER SYSTEM`, `SELECT *`, `showtext` oraz `--window` i `DATEADD`.

## Reader i ranking

`PgStatementTop.ReadAsync` czyta wiersze z `ISqlReader`. Test daje `FakeReader`. Agregacja jest po odczycie, tylko dla wierszy o tym samym znanym `queryid`.

- Brak wiersza nie jest pozycją. NULL w `queryid` nie jest pozycją i nie scala wierszy. Nie ma wspólnego id z tekstu, `userid`, `toplevel` ani numeru wiersza.
- Nie-null `queryid` równe `0` jest znaną wartością `bigint`, gdy widok je zwróci. Nie jest wspólną pozycją wierszy ukrytych.
- `userid` i `toplevel` nie rozcinają pozycji. Powtórzenie `queryid` przed agregacją nie jest błędem. Po agregacji jest jedna pozycja. Rzutu `qstop` przy zduplikowanym `query_id` tu nie ma. Rzutu `qstop` przy null `query_id` tu nie ma.
- Formuły są listą z kontraktu, sekcja „Agregacja”. `total_exec_time` jest już w milisekundach. Dzielenia przez 1000 nie ma. `averageDurationMs` i `averagePlanMs` istnieją tylko, gdy `SUM(calls)` nie jest 0. Przy zerze tych pól nie ma: nie 0 i nie JSON null. To nie jest exit `5`.
- `toplevelOnly` jest true wtedy i tylko wtedy, gdy każdy wiersz pozycji ma `toplevel` true.
- `tempBlocksRead` i `tempBlocksWritten` są licznikami `temp_blks_read` i `temp_blks_written`, nie czasami.
- Kolumny spoza listy pól nie dostają właściwości. W szczególności `shared_blks_dirtied`, `shared_blks_written`, `local_blks_hit`, `local_blks_dirtied`, `wal_records`, `wal_fpi`, `mean_*`, `min_*`, `stddev_*` i `plans`.
- Tekst jest verbatim, bez obcięcia. Sam null z serwera zostaje null. Gdy teksty pozycji się różnią, także gdy obok tekstu jest null, linia zostawia `queryId` i ma `query` null. Żadnego z tych tekstów nie niesie. Ten null nie jest twierdzeniem, że serwer zwrócił null. Zadanie nie dodaje drugiego tokenu, który by te nulle rozdzielił.
- Porządek pozycji: `SUM(total_exec_time)` malejąco, `SUM(calls)` malejąco, `SUM(shared_blks_hit) + SUM(shared_blks_read)` malejąco, `queryid` rosnąco, ze znakiem. Czwarty klucz rozstrzyga remis. Zostaje co najwyżej `--top` pozycji. `--top` jest 1..500, `OperationLimits.IsTop`. Domyślnego `20` z `QueryStoreTopCommand.Settings` nie ma.

Sumy `bigint` są licznikami całkowitymi. `wal_bytes` zostaje `decimal`, bo widok ma `numeric`. Czasy fixture są liczbami, które asercja porównuje dokładnie. Reader nie dokłada trybu zaokrąglenia. Odwzorowanie żywego `double` z Npgsql jest w braku dowodu live, nie w tym planie.

JSON pozycji używa nazw kontraktu (`JsonSerializerDefaults.Web` daje `queryId` i resztę listy). Średnie mają `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`. Klucze kontekstu, których camelCase zepsułby pisownię, mają `JsonPropertyName`: `server_version_num`, `pg_stat_statements.track`, `pg_stat_statements.track_utility`, `pg_stat_statements.track_planning`, `dealloc`, `stats_reset`, oraz `artifactDirectory` jak pole `ArtifactDirectory` raportu `qstop`. `installed_version` nie zastępuje liczby wersji i nie jest polem raportu. Nie ma `windowMinutes`, `query`, `queryHash`, CPU ani recency.

## Delta

`PgStatementTop.Compare` jest czystą funkcją dwóch udanych wyników, w kolejności wołającego. Nie otwiera połączenia, nie czyta pliku i nie woła `pg_stat_statements_reset`. Nie zapisuje migawki. Komenda `pgstop` zwraca jeden wynik i tej funkcji nie potrzebuje, żeby zwrócić raport.

Wejście pary: ten sam rozstrzygnięty cel, ta sama główna wersja, ta sama baza filtra, ten sam znany `queryid` w obu wynikach. Poza tym pary nie ma. Id tylko w jednym wyniku nie jest parą, nie dostaje zera i ma ciągłość `unknown`. To nie jest wykryty reset selektywny.

Status pary albo całego porównania, gdy przyczyna jest widokowa, jest tylko `incomparable` albo `unknown`. Przyczyny `incomparable`, i tylko te: `reset`, `dealloc`, `restart`, `spadek licznika`. Kilka naraz nie zmienia tokenu i nie nazywa się resetem selektywnym. Gdy nie zaszła żadna, status jest `unknown`.

`reset`: oba `stats_reset` są obecne i różne. NULL albo brak nie jest różnicą. `dealloc`: oba odczyty są obecne i drugi jest większy. Spadek `dealloc` jest przyczyną `spadek licznika`, nie `dealloc`. `restart` wymaga obu odczytów startu, różnicy tych odczytów i `save` równego `off` w obu. Samo `off` nie wystarcza. `save` równe `on` nie jest tą przyczyną i nie kasuje innej przyczyny, która zaszła. Brak odczytu startu albo `save` znaczy, że przyczyna restart nie zachodzi. Domyślnego `on` się nie podstawia.

Test spadku jest tylko „druga wartość jest mniejsza”. Dotyczy pól z listy kontraktu „Spadek licznika” oraz `dealloc`, gdy obie wartości są. Jedno mniejsze pole wystarcza na całą parę. Żadne pole nie dostaje różnicy. Średnie, `toplevelOnly` i `query` nie są tym testem. Brak średniej przy `SUM(calls) = 0` nie jest spadkiem. Brak pola nie jest zerem.

Odczyty startu i `save` są argumentami funkcji w teście. Nie są polami `PgStatementTopReport`, nie są stdout i nie są w `report.json`. Komenda ich nie czyta. Bez nich przyczyna restart nie zachodzi. To jest zdanie kontraktu, nie brakujący znacznik do dopisania.

`save = on` w wierszu tabeli przypadków da się przeczytać tak, jakby kasowało reset, dealloc albo spadek. Zadanie trzyma się prozy: każda przyczyna, która zaszła, zostaje nazwana. Wiersza tabeli nie poprawia.

## Artefakt

`PgStatementArtifactWriter` używa `ArtifactDirectoryPublisher` tak jak `QueryStoreArtifactWriter`. Katalog jest pod `SqlHarnessPaths.PgStatementsDir`. Segment bazy przechodzi przez `ArtifactDirectoryPublisher.SanitizeTarget`. Nazwa katalogu nie zawiera tekstu SQL i nie jest prawem dostępu.

`report.json` jest tym samym raportem bez tekstu SQL. `queries.jsonl` ma jedną linię na pozycję wyniku. Linia ma tylko `queryId` i `query`. Klucz `query` jest obecny także przy null. Nie ma `queryHash` i nie ma `querySqlText`. `RequiredText` z `qstop`, które odrzuca null, tu nie obowiązuje. Pusta lista pozycji też zapisuje artefakt, a `queries.jsonl` nie ma linii.

Niepowodzenie pisarza zostaje wyjątkiem. Moduł w fazie `Artifact` zwraca exit `6` i nie niesie raportu. Tekst SQL trafia do `knownSecrets` razem ze stałymi SQL, tak jak w `ExecuteQueryStoreTopAsync`, i nie wchodzi do stdout, stderr, treści błędu ani gain.

## Sekwencja w module

Wzorzec faz jest wzorcem `ExecuteQueryStoreTopAsync`. Nowa metoda go nie zastępuje.

1. Faza `Validation`. `--top` poza 1..500 albo `--timeout` poza 1..300, w tym wartość 0 przy braku flagi, to `SqlHarnessSafetyException` przed połączeniem. Exit `2`. Liczba połączeń zostaje 0.
2. `TargetResolver.Resolve`. Potem faza `Authentication` i `ConnectAsync`. Auth, tożsamość i zamknięcie sesji po błędzie zostają w `NpgsqlSessionFactory`. Exit `3` i exit `4` są przed jakimkolwiek SQL rozszerzenia. Brak wiersza tożsamości przy fazie `Authentication` zostaje exit `3`, nie `4` i nie `5`.
3. Po zwróconej sesji faza `Sql`. Silnik inny niż `SqlEngine.Postgres` dostaje `InvalidOperationException` z tekstem `pg_stat_statements top is available only on Postgres.` Zero poleceń tej diagnostyki. Exit `5`.
4. Odczyt `server_version_num`. Tekst, który nie jest liczbą, albo `NpgsqlException`, to exit `5`. Sekwencja staje. To nie jest `SqlHarnessSafetyException`. Liczba mniejsza niż `140000` jest `SqlHarnessSafetyException` wewnątrz `await using`, więc sesja jest zamknięta, zanim wyjdzie exit `2`. Do rozszerzenia ta ścieżka nie dochodzi.
5. Odczyt `installed_version`. NULL albo brak wiersza: brak rozszerzenia w rozstrzygniętej bazie. Wyjątek nie jest `SqlHarnessSafetyException`. Exit `5`. Komunikat: `pg_stat_statements is not installed in the resolved database.` Brak artefaktu sukcesu. W wykonanych paczkach nie ma `CREATE EXTENSION` ani `ALTER EXTENSION`.
6. Brak uprawnień przy katalogu albo przy późniejszym SQL: `NpgsqlException` w fazie `Sql`, exit `5`. Komunikat nazywa klasę przyczyny i nie zawiera stałego SQL ani tekstu `query`. Gałęzi `SqlState` nie ma.
7. `ColumnSql` null: nie wysyłać SQL kolumn, nie agregować, nie zapisywać artefaktu sukcesu, nie zwracać `SqlHarnessExitCode.Success`. Nie rzucać `SqlHarnessSafetyException`, bo to jest wyjątek podłogi, nie tej pary. Nie dodawać gałęzi mappera i nie dopisywać numeru do kontraktu. Test sprawdza brak SQL kolumn, raport null i zero zapisów sukcesu. Nie wiąże pary z wartością `SqlHarnessExitCode`. Numer, który nada istniejący mapper, nie jest decyzją tego planu i ten plan go nie nazywa.
8. Dozwolona para: kontekst, potem SQL kolumn, potem reader. Pusta lista znanych `queryid` jest exit `0` z artefaktem i z pustym `queries.jsonl`. To nie jest twierdzenie, że widok nie miał wierszy.
9. Faza `Artifact`. Wyjątek pisarza: exit `6`, raport null.

Komunikat błędu nie zawiera tekstu SQL. Hasło zostaje w zmiennej środowiska. Tego planu nie obchodzi wartość tej zmiennej.

## CLI

`PgStatementTopCommand` bierze `TargetSettings`. Flagi: `--top`, `--timeout`, `--json`, `--json-summary`. Nie ma `--window`, `--allow-mutation` i `--confirm-database`.

Brak `--top` albo `--timeout` oraz wartość poza zakresem kończą się jak inne odrzucenie granic: exit `2`, bez połączenia. Tekst: `--timeout must be 1..300 and --top must be 1..500.` Domyślnych `20` i `30` nie ma.

`--json` i `--json-summary` wykluczają się tak jak measure. Tekst zostaje: `Choose only one of --json or --json-summary.` Kontrakt nie definiuje osobnej projekcji. `--json-summary` jest tym samym raportem bez `query`. Nie ucina pól metryk i nie dodaje tekstu SQL. `ProjectSummary` nie dostaje ramienia, które budowałoby drugi kształt.

Tekst stdout nie używa kolumn `qstop`. Nie ma linii okna, CPU ani last execution. Pusta lista nie mówi o oknie 24h. Brakującą średnią wypisuje jako brak, nie jako 0.

Profil, `--var` i `--unsafe-direct` zostają przy istniejącym celu. `--engine` tylko tam, gdzie już jest legalny. Ta komenda nie dokłada własnej ścieżki celu.

## Zadania

Każde zadanie zostawia zielony filtr i nie łączy się z bazą. Nie instaluje rozszerzenia i nie zmienia GUC. Fixture PG14 i PG15+ są osobnymi faktami, bo SQL kolumn zależy od wersji rozszerzenia.

### Zadanie 1. Reader, SQL i delta

Pliki: `PgStatementTopQuery.cs`, `PgStatementTop.cs`, `PgStatementTopTests.cs`. Klasy tabeli: fixture, reset, version co do kształtu SQL. Bez modułu i bez sesji.

Fakty: F14, F15, F-text, F-nullid, F-zero, R-reset, R-dealloc, R-restart, R-save-on, R-drop, R-unknown, R-one, R-hidden, V-shape. SQL pary 14+`1.9` nie zawiera zakazanych nazw. SQL pary 15/16+`1.10` może je zawierać i raport ich nie ma. `ColumnSql` dla innej pary zwraca null.

**Weryfikacja:** `dotnet test .\SqlHarness.sln --filter "FullyQualifiedName~PgStatementTopTests" --verbosity minimal` → exit 0.

### Zadanie 2. Pisarz artefaktu

Plik: `PgStatementArtifactWriter.cs`. Fakty dopisane do `PgStatementTopTests`.

Fakt: tekst `query` jest tylko w `queries.jsonl`. `report.json` go nie ma. Null `query` zostaje kluczem. Pusta lista daje plik bez linii. Nazwa katalogu jest pod rootem pisarza i jest wynikiem `SanitizeTarget`. Rzut pisarza nie zostawia katalogu końcowego.

**Weryfikacja:** `dotnet test .\SqlHarness.sln --filter "FullyQualifiedName~PgStatementTopTests" --verbosity minimal` → exit 0.

### Zadanie 3. Sekwencja

Edycje: `Contracts.cs`, `SqlHarnessPaths.cs`, `SqlHarnessModule.cs`. Fakty na szwie `QueryStoreTopTests`: podmieniona sesja, licznik poleceń, pisarz. Klasy: error, empty, version co do odmowy pary.

Fakty: E-floor, E-auth, E-identity, E-mismatch, E-version, E-ext, E-perm, E-engine, E-bounds, E-artifact, P-empty, V-pair. `QueryStoreTopTests` zostaje zielony. `ExecuteQueryStoreTopAsync` nie zmienia tekstu odmowy Postgresa.

**Weryfikacja:** `dotnet test .\SqlHarness.sln --filter "FullyQualifiedName~PgStatementTopTests|FullyQualifiedName~QueryStoreTopTests" --verbosity minimal` → exit 0.

### Zadanie 4. CLI `pgstop`

Pliki: `PgStatementTopCommand.cs`, `PgStatementTopCommandTests.cs`. Edycje: `SqlHarnessCli.cs`, `Renderer.cs`.

Fakty: brak `--window` na ustawieniach i na operacji; brak domyślnego `20`; dispatch `--top` i `--timeout`; exit `2` bez połączenia przy złej granicy; stdout i JSON bez tekstu SQL; `--json` i `--json-summary` się wykluczają. Help zaczyna wymieniać komendę, bo rejestracja jest tą komendą. `Capabilities.cs` nadal milczy.

**Weryfikacja:** `dotnet test .\SqlHarness.sln --filter "FullyQualifiedName~PgStatementTop" --verbosity minimal` → exit 0.

### Zadanie 5. Reklama po komendzie

To zadanie startuje dopiero, gdy zadanie 4 jest w drzewie. Ten dokument go nie wykonuje.

Edycje: `src/SqlHarness.Core/Capabilities.cs` (lista w `SqlHarnessCapabilitiesProvider.Get`), `AGENTS.md`, `README.md`. Dopisują komendę `pgstop`. Nie ustawiają `SupportsQstop` na true dla `postgres`. Nie zmieniają `QueryStoreAvailable: !pg`. Nie dodają limitu okna.

Istniejący fakt zostaje: `tests/SqlHarness.Mcp.Tests/McpMappingTests.cs` wymaga `QueryStoreAvailable` false na Postgresie i true na SQL Serverze.

**Weryfikacja:** `dotnet test .\SqlHarness.sln --filter "FullyQualifiedName~CapabilitiesCommandTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~PgStatementTop" --verbosity minimal` → exit 0.

### Zadanie 6. Inspect kind, później

Ten etap nie wpisuje kindu do katalogu. Zadanie jest opcjonalne i też startuje dopiero po zadaniu 4. Ten dokument go nie wykonuje. Nie jest nową capability i nie jest dwunastym narzędziem.

Jeśli powstanie, kind nazywa się `pgstop` i jest argumentem istniejącego `sqlharness_inspect`. `McpLimits.MaxTools` zostaje. `InspectKinds` i `AllowedValues` w `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs` dostają tę nazwę. `MapInspect` nie buduje `SqlHarnessQueryStoreTopOperation`. Nie przyjmuje `window` (`allowWindow: false`). Na SQL Server rzuca `McpMappingException` przed modułem, na wzór `ThrowIfPostgres`. Na Postgresie idzie w istniejącym `RunDbAsync("sqlharness_inspect", ...)`. `IsDbTool` już zawiera `sqlharness_inspect`. Drugiej bramy nie ma.

Wtedy, i tylko wtedy, aktualizuje się zdanie kindów w `docs/mcp.md` oraz asercja `["ping", "schema", "counts", "space", "qstop", "indexes"]` w `tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs`. Do tego czasu ta szóstka zostaje. `QueryStoreAvailable` zostaje.

**Weryfikacja:** `dotnet test .\SqlHarness.sln --filter "FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~McpLifecycleTests" --verbosity minimal` → exit 0.

## Tabela testów

To jest tabela tego planu. Nie zastępuje wierszy `UNPROVEN` w kontrakcie.

| Id | Klasa | Wejście | Wynik |
|---|---|---|---|
| F14 | fixture | `FakeReader`, para 14 i `1.9`, wiersz z null `query`, bez kolumn `jit_*` | ranking po `total_exec_time`, potem `calls`; null tekstu zostaje; SQL nie ma zakazanych nazw |
| F15 | fixture | `FakeReader`, para 15 albo 16 i `1.10`, kolumny I/O i `jit_*`, dwa `userid` jednego `queryid` | jedna pozycja; extra kolumn nie ma na raporcie; te same klucze rankingu |
| F-text | fixture | dwa różne `query` przy jednym znanym `queryid` | zostaje `queryId`; `query` jest null; żaden tekst nie wchodzi do linii |
| F-nullid | fixture | wiersze z NULL `queryid` obok znanego | NULL nie jest pozycją, nie scala się i nie dostaje wymyślonego id |
| F-zero | fixture | `SUM(calls) = 0` | brak `averageDurationMs` i `averagePlanMs`; nie exit `5` |
| E-floor | error | `server_version_num` mniejsze niż `140000` | exit `2` po otwarciu i zamknięciu; brak SQL rozszerzenia i kolumn |
| E-auth | error | `NpgsqlException` w fazie `Authentication` | exit `3`; brak próby rozszerzenia |
| E-identity | error | brak wiersza tożsamości przy fazie `Authentication` | exit `3`, nie `4` i nie `5` |
| E-mismatch | error | `SqlTargetMismatchException` | exit `4` przed rozszerzeniem |
| E-version | error | tekst wersji nie jest liczbą albo `NpgsqlException` tego odczytu | exit `5`; brak SQL kolumn |
| E-ext | error | `installed_version` NULL albo brak wiersza | exit `5`; nie pusta lista; nie exit `0`; brak `CREATE EXTENSION` |
| E-perm | error | `NpgsqlException` katalogu albo SQL kolumn | exit `5`; komunikat bez tekstu SQL; brak gałęzi SQLSTATE |
| E-engine | error | `SqlEngine.SqlServer` | exit `5`; zero poleceń tej diagnostyki |
| E-bounds | error | `--top` poza 1..500 albo `--timeout` poza 1..300, także brak flagi | exit `2`; zero połączeń |
| E-artifact | error | pisarz rzuca | exit `6`; raport null |
| P-empty | empty | ranking bez znanego `queryid` | exit `0`; artefakt; `queries.jsonl` bez linii |
| R-reset | reset | dwa wyniki, różne `stats_reset` | `incomparable`, przyczyna `reset`; brak różnicy liczb |
| R-dealloc | reset | `dealloc` większe w drugim wyniku | `incomparable`, przyczyna `dealloc`; brak delty per wpis |
| R-restart | reset | oba `save` równe `off` i różny start | `incomparable`, przyczyna `restart` |
| R-save-on | reset | `save` równe `on` i różny start, bez innej przyczyny | `unknown`; nie staje się porównawalna |
| R-drop | reset | mniejszy licznik pary albo mniejszy `dealloc` | `incomparable`, przyczyna `spadek licznika`; żadne pole nie dostaje różnicy |
| R-unknown | reset | równe `stats_reset` i `dealloc`, liczniki nie mniejsze, restart nie zaszedł | `unknown`; reset jednego `queryid` nie jest wykluczony |
| R-one | reset | znane `queryid` tylko w jednym wyniku | nie para; ciągłość `unknown`; nie wykryty reset selektywny |
| R-hidden | reset | null albo nieznane `queryid` | nie para; bez scalenia |
| V-pair | version | para inna niż 14+`1.9` i 15/16+`1.10`, w tym serwer nowszy niż 16 | brak SQL kolumn; nie exit `0`; nie pusty sukces; numer nienazwany |
| V-shape | version | osobno SQL 14+`1.9` i SQL 15/16+`1.10` | listy zależą od pary, nie od samego `server_version_num` i nie od samego `extversion` |

## UNPROVEN

Warunek dowodu zostaje w kontrakcie. Żadne zadanie powyżej go nie spełnia i nie oznacza wiersza jako fakt.

- Wydania pośrednie `default_version` między tagiem `.0` a stopką dokumentacji. Warunek: odczyt każdego tagu pośredniego.
- Ukryte `query` i `queryid`: NULL albo brak wiersza. Warunek: odczyt widoku jako rola bez uprawnień `pg_read_all_stats`, dla wiersza innego użytkownika, na jawnym celu.
- Skutek pełnego resetu osobno dla `stats_reset` i osobno dla `dealloc`. Warunek: odczyt obu kolumn przed i po `pg_stat_statements_reset()` bez argumentów na jawnym celu.
- Skutek resetu bazy dla tych kolumn. Warunek: odczyt przed i po resecie jednej bazy, przy statystykach także w innej bazie.
- Skutek resetu jednego `queryid` dla tych kolumn. Warunek: odczyt przed i po `pg_stat_statements_reset` z konkretnym `queryid`.
- Czy `stats_reset` bywa NULL. Warunek zostaje zdaniem kontraktu: odczyt `pg_stat_statements_info` na jawnym celu. Czy `dealloc` bywa NULL, stoi na tym samym warunku. Zadanie nie dopisuje `dealloc` do tego zdania.
- Skutek restartu przy `save = on`. Warunek: zdanie źródła albo odczyt, które mówi, czy statystyki po starcie są statystykami sprzed wyłączenia.
- Zmiana `save` bez wyłączenia. Warunek: zdanie źródła, którego macierz nie ma.
- Który tekst należy do pozycji z dwóch `userid` albo z obu `toplevel`. Do tego czasu linia nulluje tylko `query`. Warunek: odczyt na jawnym celu.
- Kształt kolumn pary innej niż dwie dozwolone. Warunek: odczyt nazw kolumn widoku na jawnym celu dla tej pary. Ta para nie dostaje SQL kolumn i nie jest sukcesem.

Reset selektywny nie jest wykrywany. Metadanej resetu jednego `queryid` się nie wymyśla.

## Brak dowodu live i z innej platformy

Brak dowodu live jest jawny. Live proof nie jest zadaniem 1–6. Zostaje osobnym, niezaplanowanym tu wykonaniem. Wymaga jawnego celu od użytkownika. Ten plan go nie uruchamia i nie wskazuje profilu.

Brak dowodu z innej platformy jest jawny. Macierz i ten plan nie czytają widoku na innym systemie niż źródła już cytowane w kontrakcie. Tego braku nie wolno uzupełnić wynikiem z pamięci.

Fixture-first znaczy: `FakeReader` i podmieniona sesja. Bez instalacji rozszerzenia, bez zmiany GUC, bez `SQLHARNESS_PG_INTEGRATION_CONNECTION_STRING` i bez `dotnet test` w tym commicie.

## Poza tym commitem

W commicie tego dokumentu jest tylko `plans/014-pg-statements-implementation.md`. Nie ma plików `.cs`, testów, komend, stubów ani wpisów capabilities. Katalog inspect zostaje przy `ping`, `schema`, `counts`, `space`, `qstop`, `indexes`. `QueryStoreAvailable: !pg` zostaje.
