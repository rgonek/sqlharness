# Audyt SQLHarness — 2026-09-28

Stan kodu: `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`. Audyt obejmuje publiczne CLI i MCP, kontrakty Core, klasyfikację SQL Server/PostgreSQL, pliki i artefakty, ograniczenia wykonania, testy i dystrybucję. Źródła aplikacji nie zostały zmienione. Ten dokument jest raportem i propozycją kolejności prac, nie implementacją ani zatwierdzonym planem zmian.

## Ocena

Podział CLI/MCP → Core → dialekt/sesja jest zasadniczo właściwy. Nie ma powodu usuwać jednego transportu ani scalać komend wykonujących różne zadania. Główne problemy dotyczą granic MCP: wycieku przez logowanie, braku powiązania odczytu artefaktu z celem, kontroli ścieżek i niepełnej serializacji operacji DB. Dalsze rozbudowywanie powierzchni API powinno następować po ich naprawie.

## Weryfikacja i ograniczenia

- `dotnet test SqlHarness.sln --filter 'FullyQualifiedName!~Integration' --verbosity minimal`: **exit 1**. Core/CLI: **1895 passed**. MCP: **122 passed, 2 failed, 4 skipped**.
- Niepowodzenia MCP: `Progress_fires_only_for_a_client_token_without_sql_payload` — timeout 15 s przy oczekiwaniu na powiadomienie; `Published_single_file_server_passes_the_stdio_smoke_on_this_rid` — timeout publikacji po około 6 minutach, w `PublishAsync`, przed dowodem działania opublikowanej binarki.
- Powtórzenie wyłącznie testu progress przez `dotnet test tests/SqlHarness.Mcp.Tests --no-build --filter FullyQualifiedName~Progress_fires_only_for_a_client_token_without_sql_payload --verbosity minimal`: **1 passed**, exit 0. To nie unieważnia niepowodzenia całego zestawu i nie dowodzi przyczyny timeoutu.
- `dotnet list SqlHarness.sln package --vulnerable --include-transitive`: exit 0, źródło NuGet nie zgłosiło podatnych pakietów w żadnym z pięciu projektów. To nie jest dowód braku wszystkich podatności.
- Próby procesu MCP: rzeczywisty handshake i narzędzia z bieżącego buildu, wyłącznie sztuczne profile i pliki w automatycznie usuwanych katalogach tymczasowych. Żadnych połączeń do DB ani odczytów rzeczywistych artefaktów użytkownika.
- Próby potwierdziły SQL i wartość parametru w stderr, odczyt raportu innego celu, odrzucenie pliku pod root z końcowym separatorem, brak `outputSchema` dla wszystkich 11 narzędzi oraz zachowanie offline validatora.
- Nie wykonano live SQL Server/PostgreSQL, dowodu TLS, testów na Linux/macOS ani obciążenia milionami wierszy. Ustalenia o złożoności i semantyce ścieżek Unix wynikają z kodu; nie są pomiarem tych platform.
- Przegląd objął główne granice i przepływy każdego projektu. Nie jest formalną weryfikacją całego AST, wszystkich kombinacji dostawców uwierzytelnienia ani każdej instrukcji SQL.

## Priorytety

P1: bezpieczeństwo lub podstawowy kontrakt zasobów. P2: poprawność publicznego API i używalność. P3: utrzymanie. Nakład: S — godziny, M — około dnia, L — kilka dni, łącznie z testami. Ryzyko oznacza ryzyko naprawy.

| ID | Priorytet | Ustalenie | Nakład | Ryzyko | Pewność |
|---|---|---|---|---|---|
| S1 | P1 | Logger MCP zapisuje SQL i parametry do stderr | S | niskie | wysoka, odtworzone |
| S2 | P1 | Odczyt artefaktów MCP nie egzekwuje zakresu celu | M | średnie | wysoka, odtworzone |
| S3 | P1 | Kontrola input-root ignoruje wielkość liter także na Unix | M | średnie | wysoka z kodu; bez próby Unix |
| R1 | P1 | `inspect` omija blokadę jednej operacji DB | S/M | średnie | wysoka z kodu |
| R2 | P1 | Porównanie wyników ma koszt zależny od repeat² i nie obserwuje anulowania | L | wysokie | wysoka z kodu; bez pomiaru obciążenia |
| A1 | P2 | Końcowy separator input-root blokuje poprawne pliki | S | niskie | wysoka, odtworzone |
| A2 | P2 | `validate.usage` jest obowiązkową informacją bez wpływu na walidację | M | średnie | wysoka, odtworzone |
| A3 | P2 | Zdefiniowany schemat odpowiedzi nie trafia do tools/list | S/M | niskie | wysoka, odtworzone |
| A4 | P2 | Granica bezpieczeństwa PostgreSQL jest opisana głębiej niż publiczna obietnica MCP | M | średnie | wysoka z kodu i polityki |
| T1 | P2 | Pełny zestaw testów MCP nie przechodzi | M | niskie | wysoka dla obserwacji; przyczyna nieustalona |
| D1 | P3 | Dwie pętle watch i powielone reguły adapterów zwiększają koszt zmian | M/L | średnie | wysoka z kodu |

## S1 — usuń treść protokołu z loggera MCP

Dowód: `src/SqlHarness.Mcp/McpHost.cs:131` włącza wszystkie poziomy poza None; `:143` wypisuje `formatter(state, exception)` dostarczony przez SDK. Komentarz o bezpiecznym szablonie nie odpowiada zachowaniu: formatter zwraca już sformatowane dane.

W rzeczywistym procesie MCP wysłano sztuczny znacznik wewnątrz SQL oraz, w oddzielnym wywołaniu offline validate, sztuczną wartość parametru. Oba znaczniki znalazły się w stderr. W pierwszej próbie odrzucone argumenty też trafiły do logu. Nie trzeba otwierać połączenia DB, aby doszło do ujawnienia wejścia.

Skutek: log klienta MCP może utrwalać SQL, parametry i inne dane przekazane protokołem mimo sanitacji odpowiedzi. Nie stwierdzono wycieku rzeczywistych sekretów użytkownika — próby używały tylko znaczników.

Naprawa: własne stałe komunikaty i dopuszczone metadane zdarzeń, bez dowolnego formattera/payloadu SDK. Sam filtr poziomów nie powinien być jedynym zabezpieczeniem. Test procesu musi sprawdzać stderr po poprawnym i odrzuconym tools/call, z parametrem i SQL; istniejące testy startu i adaptera odpowiedzi tego nie zastępują.

## S2 — zwiąż artefakty z zakresem MCP

Dowód: `src/SqlHarness.Mcp/McpOperationMapper.cs:457` przyjmuje scope, ale poza sprawdzeniem null nie używa go do autoryzacji. `:465` czyta z globalnego `SqlHarnessPaths.CompareDir`; `src/SqlHarness.Core/SqlHarnessPaths.cs:12` jest wspólną ścieżką pod SQLHARNESS_HOME. Manifest w `src/SqlHarness.Core/ArtifactReader.cs:15` nie ma właściciela/scope. Specyfikacja `docs/superpowers/specs/2026-09-26-mcp-adapter-design.md:60` wymaga powiązania ID ze scope.

Próba: proces przypisany do sztucznej bazy `audit` otrzymał ID syntetycznego raportu z targetem `other-db`; `artifact(summary)` zwrócił raport obcego celu z `isError=false`.

Skutek: klient znający ID może odczytać metryki, nazwy obiektów i identyfikację innego celu dostępne w tym samym lokalnym magazynie. Ograniczenie do bezpiecznych sekcji zmniejsza zakres ujawnienia, ale nie egzekwuje zadeklarowanej izolacji profili. Nie ma tu dowodu odczytu dowolnego pliku systemowego.

Naprawa: jawny identyfikator właściciela obejmujący profil i zatwierdzony zestaw zmiennych/cel, przypisanie przy zapisie i sprawdzenie przed projekcją; ewentualnie rejestr ID wydanych konkretnemu scope, zgodnie z wybraną polityką trwałości. Nie wystarczy nazwa katalogu ani samo porównanie nazwy DB. Zdefiniować migrację starych artefaktów i odmowę tych bez dowodu właściciela. Sprawdzić również globalną przestrzeń nazw snapshotów: `SnapshotDocument` nie zapisuje targetu, a `SnapshotStore` ładuje po nazwie (`SnapshotStore.cs:9,156`); porównanie między profilami może być zamierzone w CLI, lecz wymaga osobnej decyzji dla MCP.

## S3 i A1 — popraw semantykę input-root

Dowód: `src/SqlHarness.Mcp/McpInputReader.cs:235` porównuje ścieżki przez `OrdinalIgnoreCase` i bezwarunkowo dokleja separator (`:239–240`). `src/SqlHarness.Mcp/McpScope.cs:161` wykonuje GetFullPath, ale nie usuwa końcowego separatora.

S3: na systemie z rozróżnianiem wielkości liter dwa różne katalogi o nazwach różniących się tylko case są traktowane jako ten sam dopuszczony root. Może to dopuścić wejście z katalogu poza zatwierdzonym zakresem. Dotyczy plików osiągalnych dla procesu; nie oznacza automatycznie wypisania całej ich treści. Ocena wynika z jawnego StringComparison w kodzie, bez live próby Linux.

A1: na Windows potwierdzono, że poprawny plik wewnątrz katalogu jest odrzucany po uruchomieniu z `--input-root` zakończonym separatorem. Powstaje porównanie z podwójnym separatorem. Root dysku wymaga tej samej uwagi.

Naprawa: jedna funkcja normalizacji i kontroli pochodzenia ścieżek, z właściwą semantyką platformy, zachowaniem katalogów głównych i obecnymi kontrolami linków. Testy: root z/bez separatora, root dysku, katalog o podobnym prefiksie, dwa katalogi różniące się case na Unix, linki i rodzice będący linkami. Sprawdzenia przed/po odczycie oparte o długość i mtime nie są tożsamością uchwytu — odporność na celową podmianę przez równoległego lokalnego pisarza pozostaje osobnym obszarem do zweryfikowania, nie dowiedzionym w tym audycie exploitem.

## R1 — obejmij inspect blokadą DB

Dowód: `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:86` kieruje inspect do RunAsync; `:333` nie zajmuje blokady. RunDbAsync zajmuje ją dopiero w `:393`. MapInspect tworzy rzeczywiste operacje ping/schema/counts/space/qstop/indexes (`McpOperationMapper.cs:152`). `McpExecutionGate.cs:57` i test `McpLifecycleTests.cs:129` jawnie utrwalają listę pięciu operacji bez inspect.

Skutek: równoległy inspect, w tym exact counts, może obciążać DB podczas benchmarku; wiele inspect nie podlega regule busy. Token deadline jest przekazywany także w RunAsync — problemem nie jest całkowity brak timeoutu.

To niespójność projektu: komentarz klasyfikuje inspect jako discovery, ale publiczny kontrakt mówi o jednej operacji DB/proces (`docs/mcp.md:77`). Naprawa: objąć inspect wspólną blokadą, pozostawiając capabilities/validate/plan/artifact jako operacje offline. Testować query↔inspect oraz inspect↔inspect, zamiast wyłącznie listy nazw pięciu narzędzi.

## R2 — ogranicz łączny koszt equivalence

Dowód: `src/SqlHarness.Core/CompareCellRunner.cs:179` zachowuje wszystkie powtórzenia obu wariantów; `:197` uruchamia ResultComparer bez tokenu. `src/SqlHarness.Core/ResultEquivalence.cs:57` porównuje iloczyn wszystkich baseline/candidate. `:135` buduje słownik częstości dla pary. Limit miliona fingerprintów dotyczy pojedynczego przebiegu (`:191`), a repeat może wynosić 100 (`CompareCellRunner.cs:60`).

Skutek: przy dozwolonych granicach nawet 200 milionów fingerprintów w pamięci oraz 10 tysięcy porównań par. Złożoność głównej fazy jest O(repeat² × rows); deadline MCP nie przerywa synchronicznej pętli, która nie sprawdza tokenu. To analiza granic, nie wykonany test obciążeniowy.

Naprawa: budżet całej operacji i jawne sprawdzanie anulowania, bardziej zwarta reprezentacja fingerprintów, ponowne użycie histogramów i rozpoznawanie identycznych przebiegów. Zachować dotychczasowe maximum directional counts i udział wszystkich powtórzeń; nie zastępować dowodu equivalence obciętym wynikiem. Testy charakterystyczne muszą poprzedzać optymalizację algorytmu.

## A2 — nadaj validate.usage rzeczywiste znaczenie albo usuń pozorny wybór

Dowód: `src/SqlHarness.Mcp/McpOperationMapper.cs:284–299` sprawdza enum usage, potem zawsze wywołuje ten sam SqlValidation.Validate. `src/SqlHarness.Core/SqlValidation.cs:40` zawsze używa SqlUsage.Query. Kontrola kształtu mierzonego SQL PostgreSQL jest odrębna (`src/SqlHarness.Core/Postgres/PostgresBenchmark.cs:88`).

Próba: dwa SELECT-y w jednym batchu z MCP `usage=benchmark` dla PG otrzymują `allowed=true`, mimo że wykonanie benchmarku wymaga dokładnie jednej instrukcji. To nie obejście wykonawczej walidacji: measure ponownie sprawdza SQL i odmówi. To zbędny parametr i mylący preflight; dokumentacja uczciwie przyznaje, że usage nie zmienia classifiera.

Naprawa: wspólna analiza Core przyjmująca tryb i, gdy potrzebne, kontekst setup/parametrów; udostępnić ten sam kontrakt w CLI. Alternatywnie wycofać usage i nazwać wynik wyłącznie klasyfikacją zapytania. Brak mutacji po validate i brak sprawdzania uprawnień offline powinny pozostać jawne.

## A3 — publikuj schemat odpowiedzi MCP

Dowód: `src/SqlHarness.Mcp/McpResultAdapter.cs:107` definiuje OutputSchema, lecz `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs:490` nie przypisuje go do rejestrowanych narzędzi. Rzeczywiste tools/list nie zwróciło outputSchema dla żadnego z 11 tools.

Skutek: klient dostaje structuredContent bez odkrywalnego schematu; musi znać format z dokumentacji. Lokalny ValidateEnvelope nie jest publikacją kontraktu. To luka w projektowaniu API, nie dowód naruszenia protokołu: outputSchema jest opcjonalną możliwością.

Naprawa: podłączyć wersjonowany schemat envelope do rejestracji; później zawęzić result do rodzaju narzędzia/operacji. Testować rzeczywiste tools/list i odpowiedzi success/error/controlled outcome. Nie generować drugiego, rozbieżnego katalogu opisów.

## A4 — ujawnij rzeczywistą granicę read-only w publicznym API

`docs/superpowers/specs/2026-09-26-postgres-safety-policy.md:56,69–77` jawnie dokumentuje akceptację funkcji o niewidocznych efektach i brak transakcji READ ONLY. Kod to realizuje: `PostgresSafetyClassifier.cs:663` filtruje nazwy, a `Postgres/NpgsqlSessionFactory.cs:94` przekazuje sklasyfikowany tekst bez zmiany roli/transakcji. Nie traktuję tej świadomej decyzji jako nowo odkrytego obejścia.

Jednak publiczny opis MCP query mówi o read-only i zawsze wyłączonej trwałej mutacji (`McpToolCatalog.cs:507`), bez wyjaśnienia tej granicy. Agent nie powinien interpretować `allowed=true` lub `classification=read-only` jako gwarancji braku skutków funkcji/widoków/operatorów.

Naprawa: capabilities i wynik validate powinny rozróżniać klasyfikację efektów widocznych w tekście od niezweryfikowanych uprawnień/ukrytych efektów. Dokumentacja startowa powinna odsyłać do tej polityki. Opcjonalny profil zaostrzony wymaga osobnego projektu i testów DB; nie dodawać automatycznie transakcji, która łamie setup TEMP i nadal nie zamyka wszystkich efektów zewnętrznych.

## T1 — odzyskaj wiarygodny gate MCP

Dowody i wyniki podano w sekcji weryfikacji. Konkretne miejsca: `tests/SqlHarness.Mcp.Tests/McpLifecycleTests.cs:106,545`, `tests/SqlHarness.Mcp.Tests/McpStdioProcessTests.cs:111,364`.

Naprawa: zdiagnozować odrębnie wyścig/utracone dostarczenie progress oraz etap publish/restore. Zachować logi etapu i poprawne sprzątanie procesu po timeout; nie zwiększać czasu bez ustalenia przyczyny. Nie deklarować zielonego gate na podstawie pojedynczego powtórzenia progress. Brak dowodu awarii produkcyjnego serwera wynika z tego, że timeout dystrybucji nastąpił w publikacji.

## Interfejsy: co zachować, co konsolidować

| Granica | Ocena |
|---|---|
| CLI i MCP | Dwa uzasadnione adaptery do wspólnego Core; nie dublują celu produktu. CLI obsługuje powłokę/pliki/NDJSON, MCP zamrożony scope i protokół. |
| query / measure / compare | Różne semantyki: wykonanie, powtarzany pomiar, dowód porównania. Zachować oddzielnie; współdzielić przygotowanie i wykonanie. |
| measure param-set / compare matrix | Nie scalać: jedna sesja/setup dla param-set, nowa sesja na komórkę matrix; różna interpretacja stabilności i equivalence. |
| inspect / query | Fixed catalog queries ograniczają zakres i upraszczają odkrywanie. Zachować inspect, poprawić gate i opis argumentów zależnych od kind. |
| schema / space / indexes | Częściowo wspólne dane indeksów, ale inne pytania: struktura, rozmiar, potencjał optymalizacji. To nie są zbędne duplikaty. |
| ISqlReader / ISqlSession / ISqlSessionFactory | Dobre granice dostawcy, lifecycle i testów. Nie widać interfejsu, który można bezkosztowo usunąć jako duplikat innego. |
| ISqlDialect | Łączy analizę, parametry, benchmark i SQL katalogowy. Szeroki, ale dwa silniki faktycznie korzystają ze spójnego punktu wyboru; podział tylko przy konkretnej potrzebie konsumenta, bez mnożenia interfejsów dla każdej metody. |
| ICompareArtifactWriter / IQueryStoreArtifactWriter / IIndexAnalysisArtifactWriter | Różne typy danych i zasady poufności; wspólny ArtifactDirectoryPublisher już eliminuje mechaniczne kopiowanie publikacji. Zachować. |
| ISqlHarnessModule / object Report | ADR świadomie zachowuje kompatybilność; OperationReportContract sprawdza pary operacja–raport. Pełna migracja do generyków/union nie jest naprawą bezpieczeństwa. |
| IWatchClock / IMcpClock | Mają podobny UtcNow, ale pierwszy zarządza opóźnieniem i monotonicznym budżetem, drugi throttlingiem postępu. Samo podobieństwo nie uzasadnia sprzęgania warstw. |

Źródła: `Contracts.cs:6`, `Dialect/ISqlDialect.cs:3`, `SqlExecution.cs:17,29,133`, `Artifacts.cs:318`, `QueryStoreArtifacts.cs:6`, `IndexAnalysisArtifacts.cs:6`, `OperationReportContract.cs:13`, `WatchRunner.cs:7`, `McpExecutionGate.cs:14`; ADR `docs/superpowers/plans/2026-09-26-audit-05-architecture-adr-t2-typed-result.md`.

D1: rzeczywiste powielenie jest w implementacji. `WatchRunner.cs:69,269` zawiera dwie pętle (`:122,331`) z tym samym budżetem, odczytem, warunkiem stop i opóźnieniem dla raportu i NDJSON. Rozważyć jeden silnik pętli i dwa odbiorniki zdarzeń. Dodatkowo McpOperationMapper powtarza liczby graniczne i parsowanie czasu (`:540,579,603`), capabilities osobno opisują limity (`Capabilities.cs:59`), a rejestracja/DTO stanowią kolejny opis kontraktu. Konsolidować czyste reguły i metadane Core, pozostawiając specyfikę transportu w adapterze. Najpierw testy zgodności istniejących trybów; bez wykazanej regresji jest to P3.

## Czy SQLHarness blokuje za dużo?

Tak, ale przyczyny są różne i wymagają różnych działań.

| Przypadek | Wynik i ocena |
|---|---|
| Poprawny plik w root zakończonym separatorem | Odtworzony błąd A1; naprawić bez osłabiania polityki. |
| T-SQL scalar DECLARE z inicjalizatorem | Próba zaakceptowana. |
| T-SQL SET zmiennej skalarnej po DECLARE | Próba odrzucona jako unsupported_statement; kandydat wąskiego rozszerzenia. Analizować RHS i typ przypisania. |
| T-SQL zmienna tabelaryczna | Próba odrzucona; świadoma obecna granica (`SqlSafety.cs:272`), sensowne rozszerzenie dla porównania table variable vs #temp po obsłużeniu całego lifecycle. |
| PG CREATE TEMP + TRUNCATE tego obiektu | Próba odrzucona jako unsupported_statement; kandydat rozszerzenia z kontrolą wszystkich celów i opcji, nie dla dowolnej tabeli. |
| PG ANALYZE lokalnej TEMP | Próba daje sql_parse_error; znane ograniczenie parsera, jawne w AGENTS.md. Potrzebne wsparcie AST; nie regex ani obejście safety. |
| PG zwykły CTE z SELECT | Próba zaakceptowana. |
| PG prefiksy lo_/dblink/pg_advisory_ | Świadome nadmiarowe odrzucenie nazw udokumentowane w polityce; bez rozwiązania tożsamości funkcji nie luzować hurtowo. |
| Matrix MCP: pusta wartość lub przecinek w wartości | Jawnie odrzucane w `McpOperationMapper.cs:492`; skutek kodowania struktury JSON z powrotem do składni CLI. Kandydat do wspólnego typowanego modelu matrix w Core, z zachowaniem parsera CLI na brzegu. |
| Persistent DDL, dynamiczny SQL, cross-database, mutacja przez MCP | Uzasadnione granice obecnego produktu; nie zalecam globalnego przełącznika wyłączającego walidację. |

## Kierunki rozwoju

1. **Preflight odpowiadający przyszłemu wykonaniu.** Rozwinąć validate o wspólny tryb query/setup/benchmark i kontekst setup, raportować regułę odmowy i bezpieczną lokalizację AST. Rozwiązuje A2 i ogranicza próbne wywołania DB. Koszt M/L; ryzykiem jest rozjazd z właściwym wykonaniem, dlatego preparer musi być współdzielony.
2. **Typowane wejścia Core dla parametrów i matrix.** MCP już ma struktury, lecz serializuje je do deklaracji tekstowych (`McpOperationMapper.cs:475,492`, `Contracts.cs:49`). Pozwoli obsłużyć wartości z przecinkami bez tworzenia drugiego validatora. Koszt M/L; wymagane zachowanie kompatybilności CLI i redakcji.
3. **Decyzja regresji przydatna w CI.** Istnieje specyfikacja `docs/superpowers/specs/2026-09-26-benchmark-regression-policy.md`; wdrożyć jej pass/fail/inconclusive nad metrykami i equivalence. Nie utożsamiać skutecznego wykonania komendy z brakiem regresji. Koszt L, wymaga zasad dotyczących szumu i brakujących metryk.
4. **Diagnostyka PostgreSQL przez pg_stat_statements.** Istnieje specyfikacja `docs/superpowers/specs/2026-09-26-postgres-statement-diagnostics.md`; uzupełnia asymetrię qstop. Zachować brak CREATE EXTENSION i semantykę snapshot/delta/reset, bez obiecywania sztucznego okna czasu. Koszt L, live testy dopiero na uzgodnionym celu.

## Rozważone i odrzucone jako nowe błędy

- Nie zgłaszam ponownie historycznego OUTPUT INTO, błędnych parametrów PostgreSQL ani podwójnego liczenia buforów bez aktualnego dowodu regresji.
- Znana niepełność statycznego dowodu read-only PostgreSQL jest świadomą polityką; nowe ustalenie A4 dotyczy widoczności tej granicy w publicznym kontrakcie.
- Dwa representations MCP (text i structuredContent) nie są dwoma zbędnymi API; adapter uwzględnia ich łączny rozmiar na wire.
- validate może poprawnie zakończyć analizę z envelope success i result.allowed=false; nie jest to to samo co wykonanie odrzuconego SQL.
- `object Report` ma dokumentowaną decyzję kompatybilności oraz kontrolę typów; nie kwalifikuję go samodzielnie jako błąd.
- Brak mutacji i force snapshot w MCP jest zatwierdzonym ograniczeniem v1.
- Brak badań live nie jest dowodem, że endpoint/TLS są wadliwe; test publikacji, który utknął przed smoke, nie dowodzi awarii serwera.

## Obserwacja instalacji

`Get-Command sqlharness` wskazało `C:\Users\rgone\.local\bin\sqlharness.exe`. Jego `--help` pokazało jedynie starszy zestaw komend (bez capabilities, validate i mcp), podczas gdy bieżący build repo uruchamia MCP. To rozjazd lokalnej instalacji i repozytorium, nie ustalenie o jakości nowego kodu. Nie aktualizowano binarki ani konfiguracji użytkownika.

## Kolejność dalszych prac

Najpierw S1, S2 i S3/A1; potem R1 oraz A3 i diagnoza T1. R2 wymaga osobnych testów charakterystycznych przed zmianą reprezentacji danych. A2/A4 powinny ustalić wspólny kontrakt przed refaktoryzacją D1 i rozszerzeniami składni. Nowe funkcje CI/PG mają już dokumenty intencji — trzeba je uzgodnić z aktualnym stanem zamiast pisać konkurencyjne specyfikacje.

Aktualizacja 2026-09-29: wszystkie ustalenia, ograniczenia i kierunki rozwoju zostały przypisane do 15 planów w [indeksie planów](README.md). Status wszystkich: TODO. Etapy funkcji CI i PostgreSQL zaczynają się od doprecyzowania istniejących specyfikacji; zapis planów nie oznacza wdrożenia.
