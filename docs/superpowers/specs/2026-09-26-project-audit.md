# Audyt SQLHarness — 26.09.2026

**Archiwum audytu:** zapisane w repozytorium jako podstawa [planów realizacji](../plans/2026-09-26-audit-roadmap.md). Plany 01–06 wynikają z audytu; plan 07 MCP dodano później na prośbę użytkownika i będzie realizowany na końcu. Poniższe wyniki opisują stan z dnia audytu, nie stan po przyszłych naprawach. Próby syntetyczne są zachowane na końcu dokumentu; plany nie zależą od plików w katalogu tymczasowym.

Commit: `5e655942f4f99f6c84ae47a86ac210c19434b541`.
Zakres: publiczne API Core, CLI, oba dialekty, klasyfikacja bezpieczeństwa, parametry, połączenia i identyfikacja celu, pomiary, artefakty, odpowiedzi dla agentów i koszt ich przetwarzania. Przegląd kodu, specyfikacji i historii ostatnich 100 commitów, testy oraz lokalne reprodukcje. Nie wykonywano zapytań na bazach. Kod projektu pozostał bez zmian.

## Ocena

Projekt ma sensowny podział Core/CLI, użyteczne komendy diagnostyczne i rozbudowane testy, ale **deklarowany kontrakt read-only ma luki**. Priorytetem są ścieżki zapisu błędnie uznawane za pracę na tabelach tymczasowych oraz funkcje PostgreSQL. Nadmiarowe blokady także istnieją: szczególnie walidacja parametrów PostgreSQL przez parser T-SQL i porównywanie nazwy DNS z adresem IP serwera.

Największy problem architektury nie polega na liczbie interfejsów. Reguły dotyczące tego samego pojęcia rozchodzą się między moduł, dialekt, walidatory i CLI. Z punktu widzenia agenta największe straty powodują błędy bez struktury, konieczność ponawiania poprawnych zapytań oraz brak globalnego limitu odpowiedzi.

P1 = naprawić przed poleganiem na gwarancji bezpieczeństwa lub poprawności pomiarów. P2 = istotna użyteczność, zasoby i spójność. Potwierdzenie klasyfikacji offline nie oznacza wykonania zapisu na serwerze; uprawnienia bazy pozostają dodatkową granicą.

## Ustalenia bezpieczeństwa

### S1 / P1 — SQL Server: OUTPUT INTO omija kontrolę trwałego zapisu

Źródła: `src/SqlHarness.Core/SqlSafety.cs:97`, `:167`, `:183`, `:302`.

Visitor wykrywa `HasNonLocalOutputInto`, lecz flaga jest sprawdzana tylko w `ClassifyCompareSetup`. `ClassifyQuery` uznaje INSERT do #temp za pracę lokalną i pomija trwały cel OUTPUT.

Reprodukcja klasyfikatora, `allowMutation=false`:

```sql
CREATE TABLE #t(id int);
INSERT INTO #t OUTPUT inserted.id INTO dbo.audit_sink VALUES (1);
```

Wynik: `Allowed=True`, `HasMutation=False`. Trwała tabela wskazana w OUTPUT nie jest tabelą tymczasową. Ścieżka `query` może zatem wysłać taki batch bez potwierdzenia mutacji, jeśli użytkownik bazy ma wymagane prawa. To dotyczy też innych miejsc używających tej klasyfikacji.

Kierunek naprawy: uwzględniać wszystkie cele zapisu, także OUTPUT, we wspólnej analizie. Po dodaniu trwałego celu operacja nie może pozostać session-local.

### S2 / P1 — SQL Server: alias zaczynający się od # uznawany za tabelę tymczasową

Źródło: `src/SqlHarness.Core/SqlSafety.cs:167`.

```sql
UPDATE #t SET id = 42 FROM dbo.items AS #t;
DELETE #t FROM dbo.items AS #t;
```

Oba przykłady klasyfikator dopuszcza bez mutacji. Sprawdza nazwę celu, bez rozwiązania aliasu z FROM. W takiej postaci #t jest aliasem trwałej tabeli. Kierunek: rozwiązać cel DML względem aliasów, a nie wnioskować o lokalności z prefiksu tokenu.

### S3 / P1 — PostgreSQL: SELECT nie zapewnia braku efektów ubocznych

Źródła: `src/SqlHarness.Core/Postgres/PostgresSafetyClassifier.cs:12`, `:266`, `:488`; `Postgres/NpgsqlSessionFactory.cs:95`.

Offline zaakceptowano jako read-only:

```sql
SELECT set_config('search_path', 'public', false);
SELECT pg_cancel_backend(12345);
SELECT pg_advisory_lock(42);
SELECT app.write_something();
```

Pierwsze trzy funkcje zmieniają konfigurację, próbują anulować cudzą kwerendę lub utrzymują blokadę sesyjną. Ostatni przykład pokazuje, że klasyfikator nie rozpoznaje efektów funkcji użytkownika; rzeczywista funkcja może wykonywać DML. Lista zakazanych nazw obejmuje tylko część takich operacji. Sesja Npgsql nie dokłada granicy transakcji read-only.

Naprawa wymaga określenia modelu uprawnień oraz polityki funkcji i źródeł danych. Transakcja read-only może ograniczyć zapisy do trwałych tabel, ale sama nie blokuje wszystkich efektów administracyjnych, blokad ani dostępu zewnętrznego. Nie wystarczy dopisać kilku nazw do denylisty. Także widoki, funkcje i synonimy mogą ukrywać operacje, których same lokalne nazwy w SQL nie ujawniają.

Źródło semantyki funkcji: https://www.postgresql.org/docs/15/functions-admin.html

### S4 / uwaga do pokrycia AST — brak potwierdzonej luki serwerowej

Źródło: `src/SqlHarness.Core/Postgres/PostgresSafetyClassifier.cs:148` oraz `:170`.

Parser biblioteki przyjmuje INSERT do TEMP z modyfikującym CTE w źródłowym SELECT i klasyfikator pomija taki zapis. Jednak PostgreSQL wymaga, aby modyfikujące CTE było dołączone do instrukcji najwyższego poziomu. Przetestowany przykład z WITH po INSERT INTO jest zatem dowodem niespójności parsera i niepełnej analizy AST, **nie potwierdzonym obejściem zabezpieczenia na rzeczywistym serwerze**. Podobnej ostrożności wymaga forma CTAS. Nie zaliczam tych prób do potwierdzonych podatności P1.

Kierunek: przed rozszerzaniem gramatyki ujednolicić rekursywną analizę wszystkich celów zapisu i dodać testy zgodności parsera z rzeczywistym silnikiem. Samodzielny WITH z INSERT do trwałej tabeli został poprawnie odrzucony przez bieżący klasyfikator.

Reguła serwera: https://www.postgresql.org/docs/17/queries-with.html
### S5 / P1 — PostgreSQL: ustawienie TLS nie pozwala wymusić weryfikacji certyfikatu

Źródło: `src/SqlHarness.Core/Postgres/PostgresConnectionString.cs:37`.

`trustServerCertificate=false` daje `SslMode.Require`, a true daje `Disable`. Dla używanej wersji Npgsql 8 Require zapewnia szyfrowanie bez weryfikacji certyfikatu; true wyłącza również szyfrowanie. Zachowanie jest udokumentowane w projekcie, więc to wada świadomie przyjętego kontraktu, nie rozbieżność implementacji ze specyfikacją.

Kierunek: jawna polityka TLS w profilu, możliwość VerifyFull i konfiguracji CA. Nazwa trustServerCertificate nie powinna jednocześnie decydować o wyłączeniu TLS. Zmianę domyślnego zachowania wdrażać z migracją profili.

Dokumentacja: https://www.npgsql.org/doc/security oraz https://www.npgsql.org/doc/release-notes/8.0.html

### S6 / P1 — błędna wartość parametru może wyciec przez InnerException

Źródła: `src/SqlHarness.Core/SqlSafety.cs:349`, `src/SqlHarness.Core/SecretRedactor.cs:7`, `src/SqlHarness.Core/SqlHarnessModule.cs:179`.

Dla `n:int=private-audit-value` zredagowany błąd zawiera:

```text
Invalid value for SQL parameter 'n'. | The input string 'private-audit-value' was not in a correct format.
```

Przed udanym parsowaniem knownSecrets zawiera pełną deklarację, nie samą wartość. Parser zachowuje FormatException, a redaktor dołącza jej treść. Nowa ścieżka parameter-set ma dodatkowe zabezpieczenia, ale nie rozwiązuje wszystkich starszych ścieżek zwykłego --param.

Kierunek: bezpieczne komunikaty walidacji bez dołączania niesanitowanych wyjątków wewnętrznych; ekstrakcja surowych wartości do redakcji przed parsowaniem. Potrzebne próby dla query, compare, measure, watch i snapshot.

## Nadmiarowe blokady i poprawność

### B1 / P1 — parametry PostgreSQL ponownie parsowane jako T-SQL

Źródła: `src/SqlHarness.Core/SqlSafety.cs:861`, `src/SqlHarness.Core/SqlHarnessModule.cs:197`, `src/SqlHarness.Core/MeasureParameterSets.cs:72`.

Z parametrem n:int=1 `SELECT @n` przechodzi, ale `SELECT @n::int`, `SELECT @n LIMIT 1` i `SELECT @n, $$hello$$` odpadają z komunikatem o parsowaniu referencji parametrów. PostgreSQL classifier przyjmuje pierwsze zapytanie z ::int. Przyczyną jest bezwarunkowe użycie TSql170Parser w SqlParameterReferenceValidator. Błąd dotyczy wielu komend.

Kierunek: analiza referencji parametrów należy do właściwego dialektu i powinna współdzielić sparsowany dokument z analizą bezpieczeństwa.

### B2 / P1 — PostgreSQL: DNS kontra IP blokuje prawidłowe połączenia

Źródła: `src/SqlHarness.Core/SqlExecution.cs:49`, `src/SqlHarness.Core/Postgres/PostgresPing.cs:12`.

Tożsamość serwera pochodzi z inet_server_addr(), natomiast profil może zawierać nazwę DNS. Porównanie tekstowe `db.example.test` z `192.0.2.1` zwraca false mimo zgodnej nazwy bazy. Loopback ma specjalne obejście, co maskuje problem w lokalnym playgroundzie.

Kierunek: odrębne zasady tożsamości dla silników, uwzględnienie DNS/proxy oraz TLS z weryfikacją hosta. Nie usuwać całej kontroli celu jako doraźnej naprawy.

### B3 / P1 — PostgreSQL: logiczne odczyty liczone wielokrotnie

Źródło: `src/SqlHarness.Core/Postgres/PostgresBenchmark.cs:208`.

WalkBuffers sumuje metrykę każdego węzła, choć węzeł nadrzędny obejmuje potomków. Syntetyczny plan Aggregate(10 hitów) → Scan(10 hitów) daje 20 zamiast 10. Zmiana głębokości planu może wyglądać jak zmiana kosztu I/O i prowadzić agenta do błędnej rekomendacji.

Kierunek: całkowite bufory brać z agregatu korzenia; metryki per-relation definiować osobno i zachować schemat relacji. Dodatkowo obecne long zaokrągla ułamkowe milisekundy, a CPU=0 oznacza brak pomiaru, nie udowodnione zerowe zużycie. Raport powinien to rozróżniać maszynowo.

Semantyka buforów: https://www.postgresql.org/docs/15/sql-explain.html

### B4 / P2 — bezpieczne operacje lokalne odrzucane szerzej niż potrzeba

Potwierdzone offline: SQL Server odrzuca `DECLARE @x int=1; SELECT @x` w query, choć DECLARE jest dozwolone w setupie. Odrzuca też TRUNCATE #temp i ALTER TABLE #temp. PostgreSQL odrzuca EXPLAIN SELECT oraz SELECT INTO TEMP; ANALYZE tymczasowej tabeli w użytym parserze kończy się ParseError. Prefiksowa blokada lo_ odrzuca także hipotetyczną bezpieczną funkcję użytkownika lo_custom_readonly.

To kandydaci do precyzyjnych rozszerzeń, nie powód wyłączenia walidatora. Najpierw naprawić rozpoznawanie rzeczywistych celów. ANALYZE temp jest szczególnie użyteczne dla reprezentatywnych benchmarków PostgreSQL. Zachować zakaz trwałego DDL, dynamicznego SQL i niekontrolowanych źródeł.

### B5 / P2 — watch nie ma twardego limitu czasu ani całego raportu

Źródło: `src/SqlHarness.Core/WatchRunner.cs:52`, `:107`.

Deadline jest sprawdzany po zapytaniu; po opóźnieniu do deadline pętla rozpoczyna jeszcze kolejny poll. Zapytanie nadal dostaje pełny timeout, bez tokenu anulowania powiązanego z pozostałym czasem. Każdy zmieniony wynik trafia do List emitted. Limit wierszy jest na poll, a nie na całą historię. Utrzymywane są także wiadomości całej sesji.

Kierunek: budżet czasu całej operacji, weryfikacja deadline przed poll, ograniczenie historii i bajtów, opcjonalny strumień zdarzeń oraz końcowe podsumowanie.

## Interfejsy i duplikacja

| Element | Ocena | Kierunek |
|---|---|---|
| ISqlHarnessModule | Dobry pojedynczy punkt wejścia dla CLI i testów; wynik object? traci związek typu operacji z raportem | Zachować fasadę, rozważyć typowane wyniki wewnątrz |
| ISqlSessionFactory / ISqlSession / ISqlReader | Różne odpowiedzialności: połączenie, sesja, strumień wyników; nie są duplikatami | Zachować; tożsamość po połączeniu nie powinna wymagać publicznego settera |
| ISqlDialect | Zbyt szeroki: bezpieczeństwo, parametry, katalog, plany i wykonanie benchmarku | Uporządkować odpowiedzialność za analizę SQL i możliwości silnika, bez tworzenia interfejsu na każdą funkcję |
| ISqlDialect.IdentitySql / CollectSessionTempTables | Brak produkcyjnych wywołań tych członków; fabryki używają własnych stałych, setup zwraca już SessionTempTables | Usunąć martwy kontrakt albo rzeczywiście scentralizować jego użycie |
| ICompareArtifactWriter / IQueryStoreArtifactWriter / IIndexAnalysisArtifactWriter | Różne dane i wymagania poufności; nie scalać automatycznie domenowych interfejsów | Wspólna implementacja bezpiecznej publikacji plików, stagingu i sprzątania |
| ISnapshotStore / IGainStore | Inna semantyka trwałości i danych, nie duplikaty writerów | Zachować odrębne kontrakty |
| SqlHarnessModule | Gromadzi dispatch, walidację, redakcję, lifecycle, pomiary, raporty i katalog | Wyodrębnić przygotowanie i wykonanie rodzin operacji za istniejącą fasadą |
| HelpProvider | Pusta klasa „na przyszłość”, obecnie nie daje zachowania | Usunąć albo wypełnić dopiero przy konkretnej zmianie helpa |

SqlHarnessModule zmieniał się w 32 spośród ostatnich 100 commitów; ISqlDialect i oba adaptery po 9. To dowód aktywnego miejsca kosztu zmian, nie tylko zarzut rozmiaru pliku. CompareCellRunner nadal wywołuje statyczne pomocniki SqlHarnessModule, a BenchmarkRunner i BenchmarkCollector fizycznie pozostają w jego pliku.

Duplikacja reguł ma już skutki: walidacja targetu w CLI odrzuca profile + --engine, ale TargetResolver nie uwzględnia Engine przy wykrywaniu opcji direct i przy profilu je ignoruje. Analiza SELECT/CTE/INTO jest powtórzona w PostgresSafetyClassifier i PostgresBenchmark. Mapowanie wyjątków powtarza się w module, watch i snapshot. Bezpieczeństwo i legalność targetu powinny być autorytatywne w Core; CLI ma tłumaczyć wejście użytkownika.

Komendy schema, space i indexes częściowo opisują indeksy, ale odpowiadają na inne pytania: struktura, fizyczny rozmiar, pokrycie rekomendacji DMV. Zachowałbym je. Tak samo measure --param-set i compare --matrix mają odmienne cele oraz model sesji; ich scalanie pogorszyłoby jasność kontraktu.

## Używanie przez agenty i oszczędność tokenów

### A1 / P1 — --json nie gwarantuje JSON dla błędów

Źródła: `src/SqlHarness.Cli/Commands/Renderer.cs:12`, `:94`; `src/SqlHarness.Cli/Commands/SqlHarnessCommands.cs:70`.

Potwierdzony wynik renderera dla błędu w trybie Json: `SQLHarness Safety: SQL safety rejection: UnsupportedStatement.`. Walidacja CLI również wypisuje tekst. Agent musi zgadywać format odpowiedzi i analizować komunikaty językowe. Brak stabilnego kodu konkretnej przyczyny, etapu oraz informacji, czy błąd można naprawić zmianą wejścia.

Kierunek: wersjonowana koperta JSON dla sukcesów i błędów, stabilne kody, bezpieczna lokalizacja błędu i wskazówka kolejnego kroku. Powinna obejmować także błędy argumentów, wejścia i zapisu artefaktów.

### A2 / P2 — summary ogranicza niektóre tablice, nie całkowity koszt

Źródła: `src/SqlHarness.Core/BenchmarkSummary.cs:138`, `src/SqlHarness.Cli/Commands/Renderer.cs:11`, `src/SqlHarness.Core/QueryResultCollector.cs:65`.

Limit 10 operatorów jest dobry. Nadal pozostają nieograniczone mapy tabel, warnings, liczba komórek matrix i zestawów parametrów. Query ogranicza wiersze, ale pojedynczy varchar(max) może zajmować megabajty. Kolektor nadal odczytuje i hashuje wszystkie wiersze; --max-rows jest limitem prezentacji, nie kosztu zapytania. OutputCaptureWriter dodatkowo utrzymuje kopię całego wypisanego tekstu.

Syntetyczny raport measure-summary z 1000 wpisów tabel:

- obecny JSON z wcięciami: 91 619 bajtów;
- ten sam raport bez wcięć: 41 376 bajtów;
- redukcja bajtów ok. 55%; rzeczywistych tokenów modelu nie mierzono.

Przykład jest testem braku granicy, nie deklaracją typowego rozmiaru raportu. Potrzebny globalny limit bajtów, limit długości komórki i metadane omitted/truncated. Pełne dane powinny pozostać w świadomie wybieranych artefaktach. Nie obcinać danych używanych do equivalence tylko dlatego, że ograniczono ich prezentację.

### A3 / P2 — odkrywanie możliwości wymaga niepotrzebnego kontekstu

Główny --help pokazuje nazwy komend bez opisów. Agent musi czytać osobne helpy i długi AGENTS.md. Brakuje zwartego opisu możliwości silnika, wspieranego wejścia, limitów, formatu wyjścia i wymagań uprawnień.

W tym środowisku zainstalowany sqlharness.exe ma krótszą listę komend niż HEAD: brak qstop i indexes. To obserwacja rozjazdu instalacji i repozytorium, nie błąd bieżącego kodu. Warto ujawniać wersję kontraktu, wersję programu i identyfikator kompilacji w jednym krótkim wyniku doctor/capabilities. Odpowiedź nie może ujawniać sekretów profili.

### A4 / P2 — gain mierzy heurystykę bajtową, nie rachunek tokenów agenta

Źródła: `src/SqlHarness.Core/CanonicalResults.cs:20`, `src/SqlHarness.Core/SqlHarnessModule.cs:262`, `src/SqlHarness.Core/GainStore.cs:183`.

Szacunek to ceil(bytes/4), a oszczędność pojedynczego wywołania jest obcinana od dołu do zera. Agregat sumuje dodatnie oszczędności i nie odejmuje przypadków, w których odpowiedź była większa niż referencyjny raw. Raw jest wewnętrzną reprezentacją kanoniczną, nie faktycznie dostarczoną alternatywną odpowiedzią innego narzędzia. Nie uwzględnia promptów, helpa, napraw błędów ani kolejnych wywołań.

Kierunek: zachować jawne estimated, raportować również ujemny net delta oraz jawnie opisać punkt odniesienia. Oceniać scenariusze zadaniowe: ile wywołań i bajtów potrzeba od pytania do poprawnej decyzji. Nie utożsamiać 55% redukcji bajtów z 55% redukcji kosztu LLM.

## Proponowane funkcjonalności — kolejność

1. **validate offline**: klasyfikacja SQL, użyte parametry, wspierany dialekt, wykryte cele zapisu i bezpieczne kody powodów; bez połączenia i bez traktowania takiego wyniku jako późniejszego upoważnienia do mutacji.
2. **capabilities / doctor**: mały maszynowy opis wersji, komend, silników, limitów i zależności. Oddzielić diagnostykę lokalną od jawnie wywołanego sprawdzenia bazy.
3. **Spójny tryb odpowiedzi dla agenta**: krótki JSON bez wcięć, limit bajtów, priorytetowe ustalenia, liczby pominiętych elementów i referencje do artefaktów. Zachować pełny JSON dla analizy.
4. **Selektywne czytanie artefaktów**: pobranie jednej sekcji lub wskazanego operatora zamiast ponownego wykonania benchmarku. Nie automatyzować ujawniania SQL tekstu i parametrów.
5. **Watch z budżetem i zdarzeniami**: końcowe podsumowanie, ograniczona historia zmian, opcjonalne NDJSON i poprawne anulowanie przy deadline.
6. **Metryki świadome silnika**: null/unavailable dla CPU PostgreSQL, semantyka buforów i pomiar czasu z ułamkami; informacja o osobnym sidecarze equivalence. Opcjonalne progi regresji dla CI z osobnym wynikiem od poprawności wykonania.
7. **Dalsza diagnostyka PostgreSQL**: pg_stat_statements jako jawnie wspierana opcja po naprawie podstaw; bez udawania identycznych uprawnień i semantyki Query Store.

Każda propozycja dotyczy tego samego zamkniętego profilu i zestawu zmiennych. Ułatwienia dla agenta nie powinny dodawać automatycznego przełączania celów ani retry mutacji.

## Co jest dobrze zaprojektowane

- Parametry są bindowane, a połączenia budowane przez buildery.
- Profile odrzucają nieznane i zduplikowane właściwości, walidacja regex ma timeout.
- Wspólna sesja dla setupu i benchmarków oraz osobne sesje komórek matrix mają jasny kontrakt.
- Wyniki porównań mają odrębne ordered/multiset/set/off, a fingerprinty limit wierszy.
- Snapshot odrzuca zapis obciętego wyniku, diff nie wypisuje wartości.
- Query Store oddziela metryki od poufnego tekstu SQL; nowsze writery mają staging i sprzątanie.
- Destylacja XML ma limity rozmiaru i głębokości, a summary limit operatorów.
- Brak automatycznych modyfikacji indeksów na podstawie samych rekomendacji DMV.

## Weryfikacja i ograniczenia

- `dotnet test SqlHarness.sln --no-restore --filter 'FullyQualifiedName!~Integration' --verbosity minimal`: 1404 zaliczone, 1 nieudany, 0 pominiętych.
- Nieudany `RunAsync_CancellationTerminatesEntireProcessTree` przekroczył czas publikacji PID. Powtórzony osobno z --no-build przeszedł (1/1). Nie przedstawiam całego pierwszego przebiegu jako zielonego; jest sygnał niestabilności testu/środowiska, bez dowodu regresji mechanizmu anulowania.
- `dotnet list SqlHarness.sln package --vulnerable --include-transitive --format json`: źródło NuGet nie zgłosiło podatnych zależności. To nie dowodzi braku wszystkich podatności.
- Reprodukcje w `Program.cs` i `evidence.txt`: wyłącznie parsery, klasyfikatory, renderowanie, syntetyczne metryki i porównanie tożsamości. Użyte dane są sztuczne.
- Nie wykonywano testów integracyjnych ani zapytań na rzeczywistych bazach. W szczególności skutki zaakceptowanego SQL wymagają testów regresyjnych na jednorazowych bazach obu silników przed wdrożeniem napraw.
- Priorytety: najpierw S1–S3 oraz S5–S6; następnie B1–B3 i A1; potem budżety odpowiedzi/czasu oraz uporządkowanie odpowiedzialności. Rozszerzenia składni po zamknięciu luk w analizie zapisów.


## Archiwum reprodukcji offline

Dane syntetyczne. True oznacza decyzję klasyfikatora, nie powodzenie wykonania SQL na serwerze. W szczególności próby PG NESTED nie stanowią potwierdzonych luk serwerowych (patrz S4).

~~~text
PG True mutation=False local=False reason=Allowed: SELECT set_config('search_path', 'public', false)
PG True mutation=False local=False reason=Allowed: SELECT pg_cancel_backend(12345)
PG True mutation=False local=False reason=Allowed: SELECT pg_advisory_lock(42)
PG True mutation=False local=False reason=Allowed: SELECT app.write_something()
PG False mutation=False local=False reason=ParseError: CREATE TEMP TABLE x AS WITH changed AS (DELETE FROM public.items RETURNING *) SELECT * FROM changed
PG False mutation=False local=False reason=ParseError: CREATE TEMP TABLE x(id int); INSERT INTO x WITH changed AS (DELETE FROM public.items RETURNING id) SELECT id FROM changed
PG True mutation=False local=True reason=Allowed: CREATE TEMP TABLE x(id int) ON COMMIT DROP; INSERT INTO x VALUES (1)
PG True mutation=False local=True reason=Allowed: DELETE FROM pg_temp_fake.items
PG True mutation=False local=False reason=Allowed: SELECT @n::int
PG True mutation=False local=False reason=Allowed: SELECT 1;
PG False mutation=False local=False reason=UnsupportedStatement: EXPLAIN SELECT 1
PG False mutation=False local=False reason=ParseError: CREATE TEMP TABLE x(id int); ANALYZE x
PG False mutation=False local=False reason=SelectIntoNotAllowed: SELECT * INTO TEMP x FROM public.items
PG False mutation=False local=False reason=UnsupportedStatement: SELECT lo_custom_readonly()
SQLSERVER True mutation=False reason=Allowed: CREATE TABLE #t(id int); INSERT INTO #t OUTPUT inserted.id INTO dbo.audit_sink VALUES (1);
SQLSERVER False mutation=False reason=UnsupportedStatement: DECLARE @x int = 1; SELECT @x;
SQLSERVER False mutation=False reason=UnsupportedStatement: CREATE TABLE #t(id int); TRUNCATE TABLE #t;
SQLSERVER False mutation=False reason=UnsupportedStatement: CREATE TABLE #t(id int); ALTER TABLE #t ADD v int;
SQLSERVER True mutation=False reason=Allowed: SELECT * FROM dbo.items WITH (UPDLOCK, HOLDLOCK);
PARAM OK: SELECT @n
PARAM REJECT: SELECT @n::int: SQL parameter references could not be parsed.
PARAM REJECT: SELECT @n LIMIT 1: SQL parameter references could not be parsed.
PARAM REJECT: SELECT @n, $$hello$$: SQL parameter references could not be parsed.
PG DNS identity matches: False
PG buffers expected=10 actual=20; sub-ms elapsed=0
SQLSERVER ALIAS True mutation=False reason=Allowed: UPDATE #t SET id = 42 FROM dbo.items AS #t;
SQLSERVER ALIAS True mutation=False reason=Allowed: DELETE #t FROM dbo.items AS #t;
SQLSERVER ALIAS True mutation=False reason=Allowed: INSERT INTO #t OUTPUT inserted.id INTO dbo.audit_sink VALUES (1);
PG NESTED True mutation=False reason=Allowed: CREATE TEMP TABLE x AS WITH changed AS (INSERT INTO public.items VALUES (1) RETURNING *) SELECT * FROM changed
PG NESTED True mutation=False reason=Allowed: CREATE TEMP TABLE x(id int); INSERT INTO x WITH changed AS (INSERT INTO public.items VALUES (1) RETURNING id) SELECT id FROM changed
PG NESTED False mutation=False reason=MutationNotAllowed: WITH changed AS (INSERT INTO public.items VALUES (1) RETURNING id) SELECT id FROM changed
PARAM ERROR REDACTION: Invalid value for SQL parameter 'n'. | The input string 'private-audit-value' was not in a correct format.
JSON ERROR: SQLHarness Safety: SQL safety rejection: UnsupportedStatement.
SYNTHETIC SUMMARY 1000 tables: pretty bytes=91619 compact bytes=41376; bytes/4 estimates=22905/10344 (not tokenizer)

~~~
