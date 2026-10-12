# Plan wykonawczy pgstop (014/T5): skumulowany ranking z kontrolą możliwości runtime

> **REJECTED 2026-10-11** — `pgstop` will not be built. `query` can already read `pg_stat_statements`; a ranked example query in the agent docs (plan 029) covers the need. Kept for history; do not execute.

Status funkcji: **PLANNED**. Aktualizacja: 2026-10-11, baza `729d88f`. Dokument nie jest wdrożeniem. Autorytet zachowania: `plans/014-pg-statements-contract.md`; macierz źródeł jest zestawem dowodów/testów, nie listą dozwolonych par wersji. Etap projektowy 014 pozostaje DONE.

## Instrukcja, zakres i zależności

Przeczytaj kontrakt i plan 037. Przed kodem sprawdź `git status --short` oraz `git diff --stat 729d88f..HEAD -- src/SqlHarness.Core src/SqlHarness.Cli src/SqlHarness.Mcp tests plans/014-pg-statements-contract.md`. Oczekiwany drift przeanalizuj i dopasuj plan; zatrzymaj tylko krok z nierozwiązaną sprzecznością w kontrakcie. Zachowaj cudze zmiany. Pracuj w izolowanym worktree, nie pushuj bez zlecenia.

Zależności: scope 003, gate 004, granice Core 009; plan 037 dostarcza źródła i fixture kształtów PG17/18 oraz strategię runtime. Plan 036 daje użyteczną ścieżkę live proof. Nie wymaga dowodu każdej możliwej pary wersji przed dopuszczeniem jej runtime.

W zakresie nowe pliki:

- `src/SqlHarness.Core/Postgres/PgStatementTopQuery.cs`: SQL katalogów, descriptor możliwości i jawna projekcja kolumn.
- `src/SqlHarness.Core/Postgres/PgStatementTop.cs`: reader na `ISqlReader`, agregacja i ranking.
- `src/SqlHarness.Core/PgStatementArtifactWriter.cs`: pisarz przez `ArtifactDirectoryPublisher`.
- `src/SqlHarness.Cli/Commands/PgStatementTopCommand.cs`.
- `tests/SqlHarness.Tests/PgStatementTopTests.cs` i `tests/SqlHarness.Tests/Cli/PgStatementTopCommandTests.cs`.

Istniejące pliki: `Contracts.cs`, `SqlHarnessPaths.cs`, `SqlHarnessModule.cs` w Core; `SqlHarnessCli.cs`, `Commands/Renderer.cs` w CLI; po komendzie `Capabilities.cs`, `AGENTS.md`, `README.md`, `plans/README.md`. Jeśli zadanie 6 zostaje wykonane, także MCP `McpOperationMapper.cs`, `Tools/McpToolCatalog.cs`, testy mapping/schema/lifecycle oraz `docs/mcp.md`. Wzorce: `QueryStoreTop.cs`, `QueryStoreArtifacts.cs`, `QueryStoreTopCommand.cs`, `QueryStoreTopTests`, `QueryStoreArtifactWriterTests`.

Poza zakresem: delta/Compare, snapshot store, instalacja lub aktualizacja rozszerzenia, reset, zmiana GUC, zapisy na bazie, profile/sekrety użytkownika, nowe narzędzie MCP. Live DB wyłącznie na osobno autoryzowanym celu albo izolowanym środowisku planu 036.

## Decyzje

- `pgstop` to osobna diagnostyka PostgreSQL PG14+. SQL Server `qstop`, jego okno i `QueryStoreAvailable: !pg` zachowują własny kontrakt. Nie kopiuj CPU, recency ani `windowMinutes`.
- V1 odczytuje skumulowane liczniki. Nie tworzy funkcji Compare, statusów ciągłości, nieserializowanych znaczników restartu ani odczytów `save`/czasu startu wyłącznie dla przyszłej delty.
- Numery wersji serwera/rozszerzenia są kontekstem i informacją o testowanym środowisku. PG14+ z nieznanym numerem i kompatybilnym kształtem jest obsługiwany. Brak wymaganej kolumny/niezgodny typ to exit `5`.
- Minimum kolumn i typów oraz opcjonalne metryki określa krok 6 kontraktu. Jawne kolumny dobieraj z wewnętrznej listy po probe, nigdy `SELECT *` ani SQL z niezweryfikowanych nazw. Namespace odkrytego obiektu rozszerzenia cytuj jako identyfikator; nie polegaj na `search_path`.
- Opcjonalna metryka niedostępna to null/unavailable z przyczyną, nigdy 0. Częściowa suma nie udaje pełnej. Zero dostępnej metryki przy wyłączonym tracking opisuj razem z GUC.
- SQL użytkownika nie jest przyjmowany. Safety pozostaje w Core; adaptery nie dostają klasyfikatora. Istniejące `OperationFailureMapper.Map`, fazy i brama DB pozostają wspólnym mechanizmem.
- Tekst query jest tylko lokalnie w `queries.jsonl`; nie ma go na stdout, stderr, w nazwach plików, gain ani `sqlharness_artifact`. Scope artefaktu ma właściciela według 003. Nie dodawaj odczytu `queries.jsonl` przez MCP artifact.

## Sekwencja i SQL

1. Validation: `top` 1..500 i `timeout` 1..300, wymagane jawnie. Niepoprawne lub pominięte: exit `2`, zero połączeń. `--json`/`--json-summary` wzajemnie wykluczone.
2. Istniejący resolve/auth/identity. `ConnectAsync` potwierdza cel. Exit `3` auth, `4` mismatch; brak danych uwierzytelniania przed Open zachowuje obecny exit `2`. Nie dodawaj odczytu rozszerzenia przed identity.
3. Po sesji faza Sql. Inny silnik: exit `5`, bez SQL tej diagnostyki. `SELECT current_setting('server_version_num')`; niepoprawny odczyt exit `5`; PG <14 exit `2` po zamknięciu sesji.
4. Katalog rozszerzenia, namespace i identyfikacja należących do niego widoków. Brak rozszerzenia: exit `5`, bez artefaktu sukcesu. SQL katalogów i zależności obiektów potwierdź w źródłach pierwotnych przed kodowaniem; wykorzystaj PostgreSQL helper cytowania nazw. Nie wysyłaj CREATE/ALTER EXTENSION/reset/set_config.
5. Katalog nazw/typów wymaganych i opcjonalnych kolumn. Descriptor określa dostępne pola, nie wybiera SQL przez równanie server+extversion. Brak minimum lub niezgodny wymagany typ: exit `5`; dodatkowe kolumny ignorowane. Nie wysyłaj projekcji odwołującej się do niepotwierdzonej kolumny.
6. Opcjonalny kontekst GUC/info według kontraktu. Rozpoznany brak/uprawnienia kontekstu oznacz unavailable z przyczyną; inne błędy transportu/wykonania pozostają exit `5`.
7. Jawna projekcja, filtr `dbid = (SELECT oid FROM pg_catalog.pg_database WHERE datname = current_database())`. Brak parametru nazwy bazy. Odczytaj wszystkie przefiltrowane wiersze przed agregacją; nie stosuj LIMIT do surowych wierszy. Timeout/cancellation pozostają ograniczeniem istniejącej sesji.
8. Agregacja znanych queryid, ranking i top. Czytelny pusty zbiór pozycji: exit `0`, raport i pusty queries.jsonl. Brak znanych id nie dowodzi braku wszystkich statystyk.
9. Artifact przez istniejący publisher: błąd zapisu exit `6`, raport null, bez końcowego katalogu sukcesu. Journal używa aktualnego modelu operacji; nie przywracaj GainRecord/gain.jsonl.

## Reader, raport i artefakt

Agreguj znany `queryid` w obrębie bieżącej bazy; `userid`/`toplevel` nie rozcinają pozycji. NULL queryid pomijaj, bez wspólnego sztucznego id; bigint 0 i wartości ujemne są znanymi id. Kolejność: suma total_exec_time malejąco, suma calls malejąco, suma shared_blks_hit+shared_blks_read malejąco, queryid rosnąco ze znakiem. Top stosuj po agregacji.

Formuły i nazwy JSON pozycji są z sekcji Agregacja kontraktu. Czasy wykonania są w ms, bez dzielenia przez 1000. Średnia wykonania znika przy calls=0. Opcjonalna średnia planowania znika również przy niedostępnej sumie. Opcjonalne sumy/maxima są null/unavailable z przyczyną przy braku kompletności. Nie dodawaj nieprzewidzianych kolumn jako pól raportu.

Report zawiera metryki, queryId, kontekst wersji i dostępności, artifactDirectory, bez tekstu SQL. `queries.jsonl` ma jedną linię `{queryId, query}` na pozycję; null tekstu zostaje null. Jednolite teksty zachowaj verbatim. Rozbieżne teksty agregacji dają query=null według kontraktu, nie wybór zależny od kolejności odczytu. Pisarz używa `ArtifactDirectoryPublisher.SanitizeTarget` i scope 003.

`SqlHarnessPgStatementTopOperation(SqlTargetRequest Target, int Top, int TimeoutSeconds)` nie ma WindowMinutes. `SqlHarnessPaths.PgStatementsDir` to katalog `pg-statements` pod Home. Komenda używa `TargetSettings` i istniejącej ścieżki target. Rejestracja `c.AddCommand<PgStatementTopCommand>("pgstop")` przy qstop. Osobny Renderer, bez CPU/okna. JSON summary jest raportem już pozbawionym query; brak osobnego modelu delty.

## Zadania i weryfikacja

### 1. Descriptor, SQL i reader

Dodaj nowe pliki PgStatementTopQuery/PgStatementTop oraz fixture w PgStatementTopTests, wzorując się na FakeReader/QueryStoreTopTests. Przed implementacją sprawdź źródła katalogów i typów; plan 037 opisuje kształty. Pokryj F14/F15/F17/F18/F-new/F-missing/F-type/F-optional/F-schema i ranking. Nie implementuj Compare.

**Weryfikacja:** `dotnet test ./SqlHarness.sln --filter "FullyQualifiedName~PgStatementTopTests" --verbosity minimal` → exit 0.

### 2. Artefakty

Dodaj PgStatementArtifactWriter na publisherze i testy w PgStatementTopTests. Sprawdź tekst tylko w queries.jsonl, pusty plik, scope i brak katalogu sukcesu po błędzie.

**Weryfikacja:** ten sam filtr PgStatementTopTests → exit 0.

### 3. Moduł

Dodaj Contracts/Paths i gałąź modułu obok ExecuteQueryStoreTopAsync. Fake session rejestruje kolejność i zapytania, fake writer rejestruje publikację. Pokryj błędy tabeli poniżej; zachowaj qstop.

**Weryfikacja:** `dotnet test ./SqlHarness.sln --filter "FullyQualifiedName~PgStatementTopTests|FullyQualifiedName~QueryStoreTopTests" --verbosity minimal` → exit 0.

### 4. CLI

Dodaj komendę/testy, dispatch i rendering. Bounds i brak flag to exit 2 przed połączeniem; wzajemne wykluczenie JSON; help wymienia pgstop, żadnego window/mutation.

**Weryfikacja:** `dotnet test ./SqlHarness.sln --filter "FullyQualifiedName~PgStatementTop" --verbosity minimal` → exit 0.

### 5. Capabilities i dokumentacja

Dopiero po komendzie aktualizuj Capabilities/AGENTS/README i indeks. Nie ustawiaj SupportsQstop dla postgres ani QueryStoreAvailable. Opisz cumulative, opcjonalne unavailable i odłożoną deltę.

**Weryfikacja:** `dotnet test ./SqlHarness.sln --filter "FullyQualifiedName~CapabilitiesCommandTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~PgStatementTop" --verbosity minimal` → exit 0.

### 6. Inspect kind (osobny opcjonalny etap)

Dodaj kind pgstop do istniejącego sqlharness_inspect, katalogu i MapInspect; nie twórz dwunastego narzędzia. Request scope/input policies i jedna brama RunDbAsync pozostają. Wymagaj top/timeout według CLI; nie przyjmuj window. Na SQL Server odrzuć przed modułem. Update docs/mcp i schema/mapping/lifecycle testy. Artifact nie ujawnia queries.jsonl.

**Weryfikacja:** `dotnet test ./SqlHarness.sln --filter "FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~McpLifecycleTests|FullyQualifiedName~PgStatementTop" --verbosity minimal` → exit 0.

## Tabela testów

| Id | Wejście | Wynik |
|---|---|---|
| F14/F15/F17/F18 | źródłowo opisane kształty, różne numery wersji | ten sam ranking wymaganych metryk; opcjonalne wg descriptor |
| F-new | nieznany numer wersji/rozszerzenia, zgodne minimum | exit 0; nie ma allowlisty par |
| F-missing/F-type | brak calls lub queryid innego typu | exit 5; zero zapytań rankingowych i artefaktów sukcesu |
| F-optional | brak/niezgodny typ opcjonalnej metryki, dodatkowe kolumny | ranking dostępny, metryka null/unavailable z przyczyną, extra ignorowane |
| F-schema | nietypowy namespace, cudzysłów w nazwie, obiekt o tej samej nazwie w search_path | cytowanie i własność rozszerzenia; brak interpolacji surowej nazwy |
| F-context | brak info/GUC albo rozpoznana odmowa odczytu kontekstu | unavailable z przyczyną; wymagany ranking pozostaje |
| F-text/F-nullid/F-zero | null/rozbieżny tekst, null id, calls=0 | kontrakt readera; bez fałszywej agregacji/średniej |
| F-order | wiele userid/toplevel i remisy, top małe | top po agregacji, deterministyczna kolejność |
| E-auth/E-identity/E-mismatch | błędy istniejących faz | exit 3/4 zgodnie z mapperem, bez SQL rozszerzenia |
| E-floor/E-bounds | PG<14 / złe top-timeout | exit 2; floor po sesji, bounds przed sesją |
| E-version/E-ext/E-perm/E-shape | błędny odczyt wersji, brak rozszerzenia, brak uprawnień rankingu, zły shape | exit 5, bez pustego sukcesu |
| E-engine | SQL Server | exit 5, zero SQL tej diagnostyki |
| E-artifact | błąd pisarza | exit 6; raport null |
| P-empty | czytelny widok bez znanych id | exit 0 i pusty queries.jsonl |
| F-no-delta | zwykła komenda v1 | zero Compare/save/start/reset, brak statusów delty |

## Weryfikacja końcowa i warunki zakończenia

- [ ] Powyższe testy i pełne `pwsh ./scripts/verify.ps1`, `pwsh ./scripts/verify-linux.ps1` → exit 0; Windows kończy literalnym `verify: OK`, Linux wrapper `verify-linux: OK`, bez FAILED. Zachowaj dowód obu gate; fixture nie jest dowodem live. Skrypty nie wypisują VERIFICATION PASS.
- [ ] `git diff --check` → exit 0; scope przeglądnięty, indeks aktualny.
- [ ] Brak allowlisty numerów, brak Compare/delty, brak SQL mutującego; wymagany niezgodny shape daje exit 5.
- [ ] Dowód live na izolowanym środowisku 036 albo autoryzowanym celu zapisany osobno; nie rozszerza deklarowanej listy sprawdzonych środowisk ponad dowód.

Zatrzymaj zależny krok przy nierozwiązywalnej sprzeczności kontraktu/źródeł, konieczności zmiany transportu/identity lub wyjściu poza autoryzowany target. Drift samej nazwy/pliku dostosuj po sprawdzeniu intencji. Nie instaluj rozszerzenia ani nie resetuj bazy użytkownika dla uzyskania dowodu. Dalszy plan delty wymaga nowego kontraktu obserwowanej różnicy i jej ograniczeń; pytania historycznej macierzy resetów nie blokują rankingu v1.
