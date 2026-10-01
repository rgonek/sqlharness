# 012-typed-matrix-proof — dowód zamknięcia

## Status

**Kod i dokumentacja kompletne. Pełny PASS gate'ów T4.** Weryfikacja planu
(filtr), build `-warnaserror` i pełny suite bez `Integration` zakończyły się
bez błędów w przebiegach tej sesji (zob. "Dowody testów" poniżej). Brak
dowodu live-DB/platformowego — zob. sekcja dedykowana niżej.

Branch `feat/plan-012-typed-matrix`, worktree
`.worktrees/plan-012-typed-matrix`. Branch zaczął się od `3d56a81` (nie od
bazy planu `8aa01f8` — pomiędzy nimi scalono poprzedników 007/009/011, co
`progress.md` odnotowuje jako oczekiwany rozjazd). Brak merge, brak push.

Zakres commitów: `3d56a81..da73e14`, 15 commitów: T1 `8bf6331..7b6afd4` (4),
T2 `a451014..879526e` (5), T3 `e57b1fc..30950eb` (4), T4 `c06e940..da73e14`
(2 kodowo-testowe; dokumenty tego dowodu są kolejnymi commitami powyżej
`da73e14`, patrz niżej — commit nie może nazwać własnego hasha).

| Zadanie | Commity | Co dostarczono |
|---|---|---|
| T1 | `8bf6331` (red, charakteryzacja), `a4bfeb1` (refactor), `4b03997` (typed NULL), `7b6afd4` (operacje + typed matrix) | Addytywny model Core `SqlHarnessParameterInput`/`SqlHarnessParameterMatrixInput`; jeden binder (`SqlParameterParser.Bind`, `SqlParameterMatrixParser.Bind`, `ISqlDialect.BindParameters`); adapter kompatybilności tekst→model; typed NULL odróżniony od tekstu `"null"` (R3); `TypedParameters`/`TypedMatrix` jako pola addytywne na rekordach operacji. |
| T2 | `a451014` (red), `5ba5419` (fix), `bb119b3` (validate), `fb39d61` (mapper), `879526e` (docs) | `McpOperationMapper` przestał składać/rozcinać tekst (`FormatParameters`/`FormatMatrix` usunięte); `MapParameters`/`MapMatrix` przekazują strukturę; `McpMatrixArgument.Values` dopuszcza JSON `null`; `ValidationOptions.TypedParameters` (R5) dla `sqlharness_validate`; schemat `matrix.values.items.type` = `["string","null"]`. |
| T3 | `e57b1fc`, `8890dc1`, `55fe55a`, `30950eb` | Domknięcie trzech zaległości z review T1: Unicode/para surogatów przez prawdziwy binder, `decimal(p,s)` na torze typed, case-insensitive duplikat nazw, odrzucenie typu Postgres przez `PostgresDialect.BindParameters` i przez `SqlHarnessModule.ExecuteAsync`, `MeasureParameterSetValidator.ParseShaped` domknięte do końca, oraz `SqlHarnessModule.ExecuteWatchNdjsonAsync` (nie „ExecuteWatchStreamingAsync” — korekta niżej) na torze typed. |
| T4 | `c06e940`, `da73e14` | Ten dowód. Inwarianty runnera matrix (nowa sesja/setup per cela, kolejność, first-failure, zachowane artefakty/partial report) dowiedzione dla typed wejścia z komórkami przecinek/puste/null; naprawiony test redakcji, który wcześniej nie mógł zawieść. |

## Korekty zakresu (rulingi kontrolera, kopiowane z `progress.md`)

- **R1** — korekta zakresu: `src/SqlHarness.Core/SqlHarnessModule.cs` (miejsce
  parsowania matrix/parametrów i zbieranie znanych sekretów) jest w zakresie.
  Tylko jeśli jeden tor wiązania tego naprawdę wymaga, można minimalnie
  dotknąć `Dialect/SqlServerDialect.cs`, `Postgres/PostgresDialect.cs`,
  `MeasureParameterSets.cs`, `CompareOperationPreparer`, źródła
  opisu/schematu narzędzi MCP, `docs/mcp.md` i ich testy, tam gdzie
  stwierdzają stare ograniczenie przecinka/pustej wartości. Każdy plik poza
  listą planu musi być nazwany w raporcie z jednowierszowym uzasadnieniem.
  Bez niepowiązanej refaktoryzacji.
- **R2** — brak nowego argumentu/formatu pliku CLI. Legacy `--matrix
  name:type=v1,v2` zachowuje dokładne znaczenie (przecinek rozdziela
  wartości, pusta wartość odrzucona, te same teksty błędów). Pliki projektu
  CLI nietknięte.
- **R3** — typed NULL w matrix: `values` MCP przyjmuje elementy JSON `null` =
  typed NULL typu matrix. `null`, `""` i tekst `"null"` to trzy różne
  wartości; dwa null to duplikat. `ParameterValue` komórki raportu jest JSON
  `null` tylko dla komórki typed NULL; dla każdego legacy wejścia JSON
  pozostaje bajtowo identyczny.
- **R4** — tor typed obejmuje matrix ORAZ parametry stałe każdej operacji
  mapowanej przez MCP (brak text-join dla żadnej z nich), przez pola
  addytywne. Legacy publiczne konstruktory `IReadOnlyList<string> Parameters`
  / `string Matrix` działają dalej przez adapter kompatybilności. Jeden
  binder, te same odrzucenia typów na obu torach.
- **R5** — R4 obejmuje też narzędzie MCP `validate` —
  `src/SqlHarness.Core/SqlValidation.cs` dostaje addytywny typed punkt
  wejścia (`ValidationOptions.TypedParameters`), istniejące przeciążenia i
  wywołanie CLI nietknięte — `validate` jest operacją mapowaną przez MCP, a
  pozostawienie jej na text-join zachowałoby most, który plan usuwa.

(R1-R5 verbatim z `.superpowers/sdd/012-typed-matrix/progress.md`.)

## Korekty do zacytowania dokładnie (z briefu T4)

- `tests/SqlHarness.Tests/WatchNdjsonTests.cs` został dotknięty w T3, bo
  **brief T3 niósł tę zaległość pokrycia** (zaległość T1 review: "ExecuteWatchNdjsonAsync
  typed path untested") — **nie z powodu R1**. T3-raport błędnie przypisał to
  R1; `progress.md` odnotowuje to jako zaległość minor T3: "controller brief
  named ExecuteWatchStreamingAsync; real method is
  SqlHarnessModule.ExecuteWatchNdjsonAsync". Metoda strumieniowego watch to
  **`SqlHarnessModule.ExecuteWatchNdjsonAsync`** (nie
  `ExecuteWatchStreamingAsync`, która nie istnieje w tym kodzie). Ten dowód
  cytuje nazwę poprawnie.

## Zadanie T4 — co dowiedziono

### 1. Inwarianty runnera matrix dla typed wejścia (comma/empty/null)

Zaległość z review T2 (`progress.md`): "No MCP-project test executes a
matrix cell with comma / empty / null values (Core fakes are not visible
there); executed cells are proven only by Core fake-session tests. Item 1
above must therefore cover all three value kinds at the Core
runner/module level."

Inwentaryzacja przed dodaniem: `CompareMatrixTests.cs` już dowodził nową
sesję/setup/kolejność dla typed `a,b`/`""`/`null`/`"null"` na ścieżce sukcesu
(`Typed_matrix_runs_comma_empty_null_and_null_text_as_four_cells`) i
redakcję jednej wartości z przecinkiem na ścieżce porażki od razu w komórce
0 (`Typed_matrix_failure_redacts_a_value_that_contains_a_comma`, zero
zakończonych komórek). Brakował test, w którym **porażka następuje po
zakończonych typed komórkach** (przecinek, pusty string) — dowodzący
jednocześnie zatrzymania przy pierwszej porażce, zachowania wcześniejszych
artefaktów/partial report i nie-otwierania dalszych połączeń.

Dodano `Typed_matrix_failure_stops_the_run_after_comma_and_empty_cells_and_keeps_only_their_artifacts`
(`tests/SqlHarness.Tests/CompareMatrixTests.cs`, commit `c06e940`): 5
wartości typed (`"se,cret"`, `""`, `null`, `"x4"`, `"x5"`), porażka SQL na
komórce 2 (typed NULL). Dowodzi:

- `factory.ConnectCount == 3` — komórki 3 i 4 nigdy nie otworzyły połączenia;
- `factory.Sessions.Count == 3`, każda sesja odrębnym obiektem
  (`Assert.NotSame`);
- komórki 0 i 1 (`"se,cret"`, `""`) dostały `SetupCount == 1` każda;
- `partialReport.Cells` = indeksy `[0, 1]` z `ParameterValue` `["se,cret",
  ""]`, w kolejności wywołania;
- katalogi artefaktów zachowane dla obu zakończonych komórek
  (`artifacts.Directories.Count == 2`, istnieją na dysku, powiązane z
  właściwymi komórkami raportu);
- komunikat błędu nazywa `cell 2` i `@BatchSize`, nie zawiera `se,cret`,
  `x4` ani `x5`.

Charakteryzacja już poprawnego zachowania `CompareMatrixRunner` — RED
potwierdzony tymczasową mutacją (`catch` zamienił `throw` na `continue`):
```
Assert.Equal() Failure: Values differ
Expected: SqlExecution
Actual:   Success
Failed: 1, Passed: 0
```
Mutacja odwrócona przed commitem; `git diff --stat
src/SqlHarness.Core/CompareMatrixRunner.cs` pusty po odwróceniu, potwierdzone
przed commitem `c06e940`.

### 2. Kontrakt poufności matrix vs. param-set — niezmieniony

- R3 (JSON null dla typed NULL) był już udowodniony w T1
  (`Typed_matrix_runs_comma_empty_null_and_null_text_as_four_cells`:
  `{"Index":2,"ParameterValue":null}`); T4 nie zmienia tego zachowania i nie
  wymagał nowego testu.
- `--param-set` (plik `.sqljson`) **nie** otworzył nowej trasy przez typed
  wejście: `SqlHarnessParameterSetInput.Parameters` pozostaje tekstem
  deklaracji w T1/T2/T3 (odnotowane jako zaległość nieblokująca w każdym z
  tych raportów); `TypedParameters`/`TypedMatrix` nie dotykają ścieżki
  param-set. Zgodnie z briefem T4 ("Add a test if typed input opened a new
  route") — nie dodano nowego testu, bo trasa się nie zmieniła; istniejące
  pokrycie poufności param-set (`tests/SqlHarness.Tests/ParameterSetFileReaderTests.cs`,
  `tests/SqlHarness.Tests/Cli/MeasureParameterSetCommandTests.cs`) jest
  niezwiązane z tym planem i nietknięte.

### 3. Testy redakcji nowych typed błędów

Zaległość z review T2 (`progress.md`): test
`McpSecretRedactionTests.Typed_values_with_separator_characters_...`
asercje `DoesNotContain` nie mogły zawieść, bo komunikaty bindera Core są
stałe (nigdy nie interpolują wartości) — jedyną realną linią obronną była
asercja równości komunikatu. Naprawione w commicie `da73e14`:

1. Test zmieniony na
   `Typed_values_with_separator_characters_reach_core_whole_and_its_rejection_is_a_constant_message`
   z komentarzem dokumentującym dokładnie co dowodzi (struktura bez
   join/split + stały komunikat), plus dodana asercja równości dla ścieżki
   `query` (wcześniej pinowana tylko dla `matrix`):
   `"Invalid value for SQL parameter 'n' of type 'int'."`.
2. Nowy test
   `Execution_phase_failure_that_echoes_a_typed_matrix_value_is_redacted_only_when_collected_as_a_known_secret`
   prowadzi dokładnie ten kształt komunikatu, który **rzeczywiście** echo'uje
   wartość — porażkę fazy wykonania (ten sam kształt, jaki produkuje
   `CompareMatrixTests.Typed_matrix_failure_redacts_a_value_that_contains_a_comma`
   przez prawdziwą fałszywą sesję) — przez `McpResultAdapter.Adapt`, raz z
   wartością zarejestrowaną jako znany sekret (redakcja), raz bez (wyciek).
   Druga wywołanie jest kontrprzykładem potwierdzającym, że pierwsza
   asercja nie jest jałowa.

   RED potwierdzony tymczasową mutacją `McpResultAdapter.RedactError`
   (ominięcie `SecretRedactor.Redact` dla `Message`):
   ```
   Assert.DoesNotContain() Failure: Sub-string found
   String: ···"measured-run-failed:fikcyjna-exec-polowa-"···
   Found:  "fikcyjna-exec-polowa-2201,fikcyjna-exec-r"···
   Failed: 1, Passed: 0
   ```
   Mutacja odwrócona przed commitem; `git diff --stat
   src/SqlHarness.Mcp/McpResultAdapter.cs` pusty po odwróceniu.

3. Pozostałe przypadki z briefu ("invalid typed value", "duplicate value",
   "unsupported type", "engine-rejected type") mają już nietrywialne
   pokrycie z T1-T3, sprawdzone w tej sesji bez potrzeby nowego testu:
   - *invalid typed value* (nieznany typ) —
     `McpMappingTests.Duplicate_and_unknown_type_parameters_fail_in_core_without_value_echo`
     (`DoesNotContain(SecretValue, ...)`, komunikat interpoluje tylko nazwę
     typu: `"Unsupported SQL parameter type '{type}'."`).
   - *duplicate value* — pinowane równością dokładną na stały komunikat
     `"... contains a duplicate value."`
     (`SqlParameterMatrixTests.Model_matrix_rejects_two_nulls_as_a_duplicate`,
     `McpMappingTests.Matrix_value_rejections_come_from_the_core_binder`) —
     równość jest silniejsza niż `DoesNotContain`, bo wyklucza dowolny inny
     tekst, nie tylko jedną znaną wartość.
   - *unsupported/engine-rejected type* (Postgres `money`/`hierarchyid`/...) —
     `CompareMatrixTests.Postgres_rejects_unsupported_matrix_types_before_connect`,
     `Postgres_rejects_unsupported_typed_fixed_parameter_before_connect`
     (Core, `DoesNotContain(secret, ...)`, komunikat interpoluje tylko nazwę
     typu: `"SQL parameter type '{typeName}' is not supported on
     Postgres."`), oraz `McpMappingTests.Postgres_rejected_parameter_types_stay_core_owned`
     (MCP, przez `validate`). Brief dopuszcza pokrycie na poziomie Core
     ("McpSecretRedactionTests, and Core where applicable").

Żadna z tych trzech kategorii nie wymagała nowego testu T4.

## Dowody testów (ta sesja, ten worktree, Windows/x64)

Komenda weryfikacji (`constraints.md`):
```
dotnet test SqlHarness.sln --filter 'FullyQualifiedName~SqlParameterMatrixTests|FullyQualifiedName~CompareMatrixTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~McpSecretRedactionTests' --verbosity minimal
```
→ `SqlHarness.Tests.dll`: `Passed! - Failed: 0, Passed: 134, Skipped: 0, Total: 134`.
→ `SqlHarness.Mcp.Tests.dll`: `Passed! - Failed: 0, Passed: 73, Skipped: 0, Total: 73`.
(Stan po T3: 133 / 72; T4 dodał +1 w każdym projekcie — po jednym nowym
teście w każdym, pozostałe zmiany T4 to zamiana istniejącego testu, nie
dodanie.)

```
dotnet build SqlHarness.sln --no-restore -warnaserror
```
→ `Build succeeded. 0 Warning(s) 0 Error(s)`, exit 0.

```
dotnet test SqlHarness.sln --no-build --filter "FullyQualifiedName!~Integration" --verbosity minimal
```
→ `SqlHarness.Tests.dll`: `Passed! - Failed: 0, Passed: 2808, Skipped: 0, Total: 2808`.
→ `SqlHarness.Mcp.Tests.dll`: `Passed! - Failed: 0, Passed: 212, Skipped: 4, Total: 216`
  (4 skip = 2 live opt-in `McpStdioLiveTests` + 2 foreign-RID publish-smoke
  `McpStdioProcessTests`, jak w każdym wcześniejszym zadaniu tego planu).
  **Brak awarii w tym przebiegu** — żaden z dwóch znanych niestabilnych
  testów hosta (`McpStdioProcessTests.Inprocess_host_returns_zero_on_precancelled_token_without_stdout_bytes`,
  `McpLifecycleTests.Eof_on_stdin_shuts_the_host_down_cleanly`) nie zawiódł.

Projekt MCP uruchomiony powtórnie samodzielnie, z uwagi na jego historię
niestabilności w T2 (jeden `McpStdioProcessTests` i jeden `McpLifecycleTests`
zawiodły pojedynczo w T2, przeszły po powtórce):
```
dotnet test tests/SqlHarness.Mcp.Tests --no-build --filter "FullyQualifiedName!~Integration" --verbosity minimal
```
→ `Passed! - Failed: 0, Passed: 212, Skipped: 4, Total: 216` (drugi,
niezależny przebieg, również bez awarii).

Dwa czyste przebiegi projektu MCP w tej sesji nie dowodzą, że te dwa testy
nigdy nie zawiodą (T1 i T2 odnotowały pojedyncze awarie tych samych
testów, obie przechodzące po powtórce) — tylko że w tej sesji nie
zaobserwowano awarii. Żaden plik tego planu nie dotyka kodu hosta
stdio/lifecycle (plan 005 jest właścicielem tej niestabilności).

```
git diff --check
```
→ exit 0 (puste wyjście).

```
git status --short
```
→ puste po ostatnim commicie tego zadania (`da73e14`); dokumenty tego dowodu
dodane kolejnym commitem, patrz "Pliki zmienione" niżej.

## Pliki zmienione w T4

W zakresie listy planu:
- `tests/SqlHarness.Tests/CompareMatrixTests.cs` (commit `c06e940`).
- `tests/SqlHarness.Mcp.Tests/McpSecretRedactionTests.cs` (commit `da73e14`).

Poza listą planu: **brak**. T4 nie dotknął żadnego pliku produkcyjnego i
żadnego pliku testowego poza tymi dwoma, które są dosłownie wymienione w
zakresie planu. Dwie tymczasowe mutacje produkcyjne
(`CompareMatrixRunner.cs`, `McpResultAdapter.cs`) użyte wyłącznie jako dowód
RED zostały odwrócone przed jakimkolwiek commitem — potwierdzone
`git diff --stat` pustym dla obu plików bezpośrednio przed commitami
`c06e940` i `da73e14`.

### Pliki zmienione poza listą planu w T1-T3 (skopiowane z wcześniejszych
### raportów zadań, do kompletności dowodu zamknięcia)

- `src/SqlHarness.Core/SqlHarnessModule.cs` — R1 (miejsce parsowania
  matrix/parametrów, zbieranie znanych sekretów).
- `src/SqlHarness.Core/CompareCellRunner.cs` — R1 (`CompareOperationPreparer.PrepareFixed`
  wiąże parametry stałe compare/matrix).
- `src/SqlHarness.Core/Dialect/ISqlDialect.cs`,
  `src/SqlHarness.Core/Dialect/SqlServerDialect.cs`,
  `src/SqlHarness.Core/Postgres/PostgresDialect.cs` — R1 (nowy człon
  interfejsu `BindParameters`, odrzucenie typu Postgres podłączone przez
  dialekt).
- `src/SqlHarness.Core/MeasureParameterSets.cs` — R1 (measure-sets wiąże
  parametry stałe samodzielnie, czyta token typu z tekstu).
- `src/SqlHarness.Core/BenchmarkSummary.cs`,
  `src/SqlHarness.Core/AgentOutputProjection.cs` — R3 (nullowalność
  `ParameterValue` przeniesiona do projekcji `--json-summary`).
- `tests/SqlHarness.Tests/BenchmarkRunTests.cs`,
  `tests/SqlHarness.Tests/MeasureParameterSetExecutionTests.cs` — fałszywe
  implementacje `ISqlDialect` potrzebowały nowego członu (przepustka).
- `tests/SqlHarness.Tests/BenchmarkSummaryTests.cs` — jedna asercja
  skompilowana pod `string?`.
- `src/SqlHarness.Core/SqlValidation.cs` — R5 (typed punkt wejścia dla
  `validate`).
- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs` — R1 (jeden string
  `[Description]` mówiący "no commas").
- `docs/mcp.md` — R1 (jeden punkt opisujący stare ograniczenie).
- `tests/SqlHarness.Mcp.Tests/McpToolSchemaTests.cs` — R1 (test
  schematu/opisu + test JSON null przez prawdziwy klient/serwer).
- `tests/SqlHarness.Mcp.Tests/McpLifecycleTests.cs` — dwie asercje
  czytające tekst `operation.Parameters` przeniesione na `TypedParameters`.
- `tests/SqlHarness.Tests/Postgres/PostgresParameterTests.cs` — R1 (plik
  testowy `Postgres/PostgresDialect.cs`, nazwany w R1 jako dotykalny).
- `tests/SqlHarness.Tests/WatchNdjsonTests.cs` — **nie R1** (korekta
  powyżej): dotknięty, bo brief T3 niósł zaległość pokrycia
  `ExecuteWatchNdjsonAsync` z review T1; plik i metoda nie są w literalnym
  zakresie planu, ale zaległość nie mogła być zamknięta z
  `CompareMatrixTests.cs`, bo `SqlHarnessModule.ExecuteAsync` nigdy nie
  wywołuje punktu wejścia NDJSON.

## Brak dowodu live-DB / platformowego

Żadne zadanie tego planu (T1-T4) nie otworzyło połączenia do żywego SQL
Server lub PostgreSQL i nie dotknęło profilu `~/.sqlharness` ani
rzeczywistego sekretu. Każda liczba powyżej pochodzi z fałszywych
sesji/czytników/modułów testowych (`MatrixSessionFactory`, `RecordingModule`,
`FakeGainStore`, itd.) i ze `SQLHARNESS_HOME` syntetycznego na potrzeby
testu (`tests/SqlHarness.Mcp.Tests/McpSecretRedactionTests.cs`:
`Typed_values_with_separator_characters_...` tworzy katalog tymczasowy, nie
czyta prawdziwych profili). Uruchomione tylko na jednym worktree
Windows/x64; brak dowodu dla innej platformy/architektury. Zachowanie
serwera, na którym binder Postgres się opiera (rzeczywiste odrzucenie typu
`money`/`geography` przez `npgsql`, rzeczywisty format komunikatu błędu
serwera), pochodzi z dokumentacji typów Postgres/`npgsql`, nie z obserwacji
żywego serwera.

## Otwarte pozycje dla użytkownika

- Zaległości nieblokujące, przeniesione bez zmian z T1-T3 (brak zadania,
  które by ich dotyczyło w zakresie 012): `.sqljson` pliki param-set
  pozostają tekstem deklaracji, nie strukturą JSON MCP; komunikaty błędów
  toru typed wciąż mówią „The --matrix option ...” / „SQL parameters must be
  supplied ...” (tekst współdzielony z CLI, nie specyficzny dla MCP).
  Żadna z nich nie jest luką bezpieczeństwa — opisane w raportach T1 (pkt 3,
  4) i T2 (pkt 2, 5).
- Dwie niestabilne pozycje testów hosta MCP
  (`McpStdioProcessTests.Inprocess_host_returns_zero_on_precancelled_token_without_stdout_bytes`,
  `McpLifecycleTests.Eof_on_stdin_shuts_the_host_down_cleanly`) — nie
  zaobserwowane w tej sesji, ale historycznie niestabilne na tym branchu
  (T1, T2); właściciel plan 005.
