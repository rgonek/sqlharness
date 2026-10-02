# Plan implementacji offline decidera regresji (013/T4)

Status funkcji: **PLANNED**. Ten dokument nie jest wdrożeniem. Nie dodaje komend, stubów ani capabilities. Nie zmienia kodu, testów ani `plans/013-regression-contract.md`.

Data: 2026-10-02. Gałąź: `docs/plan-013-regression-policy`. Baza tego planu: `057bcb0`. Autorytet zachowania: `plans/013-regression-contract.md` przez ten commit. Tam, gdzie kontrakt różni się od sekcji 4 i 5 w `docs/superpowers/specs/2026-09-26-benchmark-regression-policy.md`, wygrywa kontrakt. Sekcje 5, 7 i 9 specyfikacji określają przypadki, kształt komendy i przyszłe pliki, ale nie kolejność reguł.

Dwie luki słowne zostają w kontrakcie. Tego pliku się nie poprawia. Obowiązuje brama 2 i niezmiennik CPU, nie węższy wiersz mapy w „Zmiana względem sekcji 4”: na `Engine` równym `postgres` CPU nigdy nie korroboruje, także gdy token to `measured`. Brak `Engine` nie jest `postgres` i nie wolno go wnosić z `CpuTimeMilliseconds` równego 0.

## Zestaw implementacji

Przyszłe pliki są tylko te:

1. `src/SqlHarness.Core/RegressionPolicy.cs` — progi wersji, `regressionPolicyVersion` 1, liczby z kontraktu: czas +10% i 5 ms, logical reads +10% i 100, CPU +15% i 5 ms, minimum 5, spread 25%.
2. `src/SqlHarness.Core/RegressionDecider.cs` — czysta funkcja, bez I/O i bez połączenia. Realizuje pierwsze dopasowanie z kontraktu. Nie czyta `runs.jsonl`.
3. `src/SqlHarness.Cli/Commands/RegressCommand.cs` — offline `regress <artifact-id> --policy v1 --json`, kształt opcji B z sekcji 7 specyfikacji. Bez celu, bez połączenia, bez ponownego benchmarku.
4. `tests/SqlHarness.Tests/RegressionDeciderTests.cs`
5. `tests/SqlHarness.Tests/Cli/RegressCommandTests.cs`

Innych nowych plików ten plan nie wprowadza. Plik progów JSON, skill i katalog MCP nie należą do zestawu.

## Reklama po komendzie

`src/SqlHarness.Core/Capabilities.cs` (lista w `SqlHarnessCapabilitiesProvider.Get`), `AGENTS.md` i `README.md` opisują `regress` dopiero wtedy, gdy komenda już istnieje. To ostatni przyszły krok, nie ten commit dokumentacyjny. Do tego czasu help, capabilities i README milczą o `regress`.

## MCP

Narzędzie MCP nie jest zadaniem tego planu. Byłoby osobnym, uzgodnionym rozszerzeniem katalogu. Tego rozszerzenia się tu nie nazywa i nie projektuje.

## Edycje istniejących plików

To nie są pliki zestawu. Bez nich nowa liczba przebiegów nie wejdzie do sekcji, których brama 1 wymaga.

Liczba przebiegów zmierzonych jednego wariantu jest dziś `unknown`. `SqlHarnessCompareReport.MeasuredRunCount` to `runs.Count` dla obu wariantów naraz (`CompareCellRunner`). `Repetitions` to `request.Repeat`. Żadna z tych liczb nie jest licznikiem wariantu. Nie wolno liczyć `MeasuredRunCount / 2` ani podstawiać `Repetitions`, także gdy iloraz jest całkowity.

Nowa składowa jest opcjonalna i per wariant, na obiektach, które raport compare już zawiera. Nie dodawać jej obok istniejącego `MeasuredRunCount` raportu i nie dodawać drugiego pola silnika. `SqlHarnessTargetIdentityReport.Engine` już jest `string?` i przy null jest pomijane w JSON.

- `CompareVariantReport` w `src/SqlHarness.Core/Artifacts.cs`: `int? MeasuredRunCount` jako właściwość `init`, nie parametr pozycyjny. `[JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]`, ten sam wzorzec co `MetricReport`. Null znaczy brak składowej. Zero jest podaną liczbą.
- `BenchmarkVariantSummary` w `src/SqlHarness.Core/BenchmarkSummary.cs`: ta sama właściwość. `BenchmarkSummaryProjector.ProjectVariant` ją kopiuje.
- `ArtifactNamedMetrics` w `src/SqlHarness.Core/ArtifactReader.cs`: ta sama właściwość. `ArtifactReader.Of` ją kopiuje.
- `HasMissingMembers` zostaje przy obecnym sprawdzeniu (`Target`, `Baseline`, `Candidate`, `Equivalence` dla compare). Brak nowej składowej nie robi raportu nieczytelnym.
- `BenchmarkReports.CreateVariantReport` w `src/SqlHarness.Core/BenchmarkRunner.cs` ustawia liczbę na `runs.Count` listy, którą już dostał. `CompareCellRunner` podaje tam osobno przebiegi baseline i candidate z powtórzeń 1..`Repeat`. Rozgrzewka (numer 0) do tej listy nie wchodzi. Measure i measure-set też wołają tę metodę; zapis liczby ich nie zamienia w parę compare.

JSON jest camelCase (`ArtifactDirectoryPublisher.JsonOptions`, `JsonSerializerDefaults.Web`). Stary `report.json` bez `measuredRunCount` na wariancie nadal się deserializuje. Brak członu to null, nie 0. Summary i metrics wystawiają tę samą kopię, gdy writer ją ustawił. Test ma pokazać trzy rzeczy: klucz pominięty daje null; klucz `0` daje 0; po zapisie nowej pary summary i metrics mają tę liczbę. Tego testu ten commit nie uruchamia.

Spreadu się nie zapisuje. Nie dodawać pola spread do `CompareDistribution`, summary ani metrics. Decider liczy `(Max - Min) / Median` z `ElapsedTimeMilliseconds` typu `CompareDistribution`, z zapisanych `long`. Status zapisanego spreadu zostaje `unknown`. Obcięcie mediany w `Distribution.From` wobec surowych próbek zostaje UNPROVEN. Decider nie czyta `runs.jsonl` i nie odtwarza mediany.

## Miejsce rejestracji

Istniejąca komenda offline `artifact` jest rejestrowana w `src/SqlHarness.Cli/SqlHarnessCli.cs`, w `SqlHarnessCli.Create`, wewnątrz `app.Configure`:

`c.AddCommand<ArtifactCommand>("artifact").WithDescription("Read safe sections of a saved benchmark artifact.");`

Przyszła linia `regress` stoi w tym samym bloku, obok tej rejestracji. Tego pliku ten commit nie zmienia. `RegressCommand` nie bierze `ISqlHarnessModule`. Wzorzec odmowy i wyjątku jest jak `ArtifactCommand`: `ArtifactReadException.ExitCode`, a `IOException` i `UnauthorizedAccessException` spoza readera to wyjście 6.

## RegressionPolicy

Klasa trzyma wyłącznie zestaw v1. Nie ma pliku progów i nie ma selektora `latest`. `--policy` inne niż dokładnie `v1` (puste, `V1`, `latest`, `v2`) nie woła decidera.

Liczby, bez zmiany względem sekcji 3 specyfikacji i sekcji „Niezmienniki”:

| Metryka | Próg względny | Próg absolutny |
|---|---|---|
| Mediana czasu | candidate ≥ baseline × 1.10 | candidate − baseline ≥ 5 ms |
| Mediana logical reads | candidate ≥ baseline × 1.10 | candidate − baseline ≥ 100 |
| Mediana CPU | candidate ≥ baseline × 1.15 | candidate − baseline ≥ 5 ms |

Minimum zmierzonych przebiegów wariantu: 5. Spread: iloraz większy niż 25% zatrzymuje; równy 25% przechodzi. Porównania idą przez `decimal`, nie przez dzielenie całkowite i nie przez odejmowanie `long`, które owija zakres. `regressionPolicyVersion` w JSON to liczba 1.

Metryka jest sygnałem tylko wtedy, gdy spełnia oba progi. Jeden próg jest szumem. `fail` wymaga regresji czasu (oba progi) i co najmniej jednego sygnału kosztów (oba progi): logical reads, albo CPU, gdy brama 2 wymiaru nie zdjęła. Sam czas nie wystarcza. Sam CPU ani same reads bez regresji czasu nie dają `fail`. `missingIndexes` i operatory godne uwagi nie zmieniają werdyktu.

## RegressionDecider

Wejście jest już odczytane. Funkcja nie otwiera katalogu, manifestu, `report.json`, `runs.jsonl`, planów ani SQL i nie łączy się z bazą. Nie zapisuje werdyktu z powrotem do artefaktu.

Werdykt to tylko `pass`, `fail` albo `inconclusive`. Pierwsze dopasowanie wygrywa. Kolejność zastępuje sekcję 4 specyfikacji: kompletność, dostępność, equivalence, stabilność, potem zera, potem progi. R1 i R2 nie skracają bram. R3 nie jest werdyktem.

Pola, których funkcja używa: rodzaj artefaktu; czy manifest wymienia `summary` i `metrics`; `int?` liczby przebiegów na każdym wariancie; `Engine`; `BenchmarkMetricReport` albo jego brak; tokeny dostępności; `CompareDistribution` czasu, reads i CPU; `ResultEquivalenceReport.Mode` i `Equivalent`. Liczniki `DifferingPositions`, `BaselineOnlyCount` i `CandidateOnlyCount` nie są equivalence. `ResultsStable` measure i measure-set nie jest stabilnością pary. `Repetitions` i `SqlHarnessCompareReport.MeasuredRunCount` nie są liczbą wariantu.

### Brama 1

Rodzaj jest pierwszy, przed brakiem sekcji i przed liczbą.

- Czytelny `measure` (`ArtifactReader.MeasureKind`), `measure-set` (`ArtifactReader.MeasureSetKind`) albo rollup macierzy: `inconclusive`. Mediany 0 tego nie zmieniają w Z1.
- Potem para compare bez `summary` albo bez `metrics`, w tym czytelny manifest, który tej sekcji nie wymienia: `inconclusive`.
- Potem liczba wariantu null na baseline albo candidate: `inconclusive`. To nie jest 0.
- Potem liczba podana i mniejsza niż 5 na którymkolwiek wariancie: `inconclusive`. Chodzi o przebiegi zmierzone, nie o rozgrzewkę.
- Dalej tylko jedna para `compare` z obiema sekcjami i z obiema liczbami ≥ 5.

`SqlHarnessCompareMatrixReport` nie jest rodzajem manifestu. Komórka macierzy zapisana jako `compare` jest zwykłą parą i idzie w bramy. Rollup to osobny kształt wejścia (`SqlHarnessCompareMatrixReport`), nie komórka. Decider na rollupie zwraca `inconclusive` i nie ocenia komórek po kolei. Reader dziś takiego rodzaju nie przyjmuje: `ArtifactManifest.ForReport` rzuca `ArgumentOutOfRangeException` dla typu spoza trzech raportów, a obcy `artifactKind` pada jako manifest niepoprawny. Test rollupu woła decider. Test komendy nie dopisuje rodzaju manifestu. Czytelność rollupu na dysku jest UNPROVEN; warunek jest niżej.

### Brama 2

Tokeny porównywać porządkowo z `BenchmarkMetricReport.Measured` (`measured`) i `Unavailable` (`unavailable`). Bez zginania wielkości liter. Zero w `CompareDistribution` nie jest tokenem.

- `MetricReport` null na baseline albo candidate: `inconclusive`. To jest stop, nie zdjęcie CPU.
- `ElapsedTimeAvailability` albo `LogicalReadsAvailability` inne niż dokładnie `measured` na którymkolwiek wariancie, w tym `unavailable`, pusty napis i każda inna wartość: `inconclusive`. Nie wolno iść z tym w zera.
- CPU nie jest wymagane. Wymiar odpada i ocena idzie dalej, gdy `Engine` jest dokładnie `postgres`, przy każdym tokenie CPU, także `measured`. Odpada też na dowolnym silniku, gdy token któregoś wariantu nie jest dokładnie `measured`. Zostaje tylko wtedy, gdy `Engine` nie jest `postgres`, a oba warianty mają `measured`.
- Brak `Engine` (null, składowa pominięta w JSON) nie jest `postgres`. `CpuTimeMilliseconds` równe 0 tego nie zmienia. Przy braku `Engine` token `measured` może korroborować, a `unavailable` wymiar usuwa.
- Wejście polityki v1, które zawiera próg CPU, nie przywraca wymiaru na `postgres`.

Węższy wiersz mapy kontraktu mówi tylko o `unavailable` przy `postgres`. Tego wiersza implementacja nie stosuje. `PostgresBenchmark.ParseStats` i tak zapisuje CPU jako `unavailable`, a `NpgsqlSessionFactory` stawia `Engine` przez `PostgresEndpointIdentity.CreateReport` na `SqlEngineNames.Postgres` (`postgres`). Para `postgres` + token `measured` jest obowiązkiem decidera, nie dzisiejszym zapisem writera.

`SqlExecution.ConnectAsync` buduje `SqlHarnessTargetIdentityReport` bez `Engine`. Artefakt z tej ścieżki ma brak silnika. To nie jest dowód z plików użytkownika; `~/.sqlharness` nie był czytany.

### Brama 3

Czyta enum `ResultComparisonMode`, nie cyfrę JSON. Bez konwertera enum w `JsonOptions` zapisuje się jako liczba; po deserializacji decider widzi enum. `Off` jest sprawdzane przed mismatch.

- `Mode` = `Off`: `inconclusive`. Null w `Equivalent` nie jest ani zgodą, ani mismatch. `Equivalent` false przy `Off` zostaje przy `Off`, nie przechodzi w R7.
- Brak obiektu `Equivalence`, albo `Mode` = `Ordered`, `Multiset` lub `Set` i `Equivalent` null: `inconclusive`.
- `Equivalent` false przy `Ordered`, `Multiset` albo `Set`: `inconclusive`, nigdy `fail`.
- Dalej tylko `Ordered`, `Multiset` albo `Set` oraz `Equivalent` true.

Inicjalizator z zerami liczników jest obiektem obecnym. Decider nie nazywa go brakiem. Pochodzenie tych zer zostaje UNPROVEN. `HasMissingMembers` odrzuca null obiektu `Equivalence` na raporcie compare, więc czytelny artefakt compare nie niesie null obiektu: to `ArtifactReadException`, bez werdyktu, i to nie jest wiersz C-missing-equivalence. Null `Equivalent` przy `Ordered` jest czytelny, bo inicjalizator kopiuje `ResultsEquivalent`.

### Brama 4

Formuła jest tylko na czasie, na obu wariantach, z zapisanych `Min`, `Median`, `Max`. Spread CPU i reads nie wchodzi. Nie wstawiać `stable`. Iloraz: `((decimal)Max - Min) / Median`, stop gdy wynik jest większy niż `0.25m`.

Dla każdego wariantu, pierwsze dopasowanie:

- `Median` > 0 i iloraz > 25%: `inconclusive`. Wystarczy jeden wariant.
- `Median` > 0 i iloraz ≤ 25%: ten wariant formułę przechodzi.
- `Median` jest 0 oraz `Max` i `Min` są 0: brak obserwowanego spreadu. To nie jest zapisane `stable`.
- `Median` jest 0 i `Max` > 0: `inconclusive`.
- Każde inne `Median` równe 0: `inconclusive`.

Brama przechodzi tylko, gdy żaden wariant nie dał `inconclusive`. `Median` < 0 nie trafia w te gałęzie. Łapie ją reguła ujemnej mediany po bramach, zanim zaczną się progi.

### Po bramach

Sprawdzenia ujemnej mediany, zer i progów są terminalne. Nie wracają do bram. Mediana czasu to `CompareDistribution.Median` na `ElapsedTimeMilliseconds`, nie `ElapsedTimeMillisecondsExact`. Brama 2 już wymaga tokenu `measured`, więc zero poniżej jest zmierzonym zerem całych milisekund.

1. Mediana czasu baseline albo candidate jest mniejsza od 0: `inconclusive`. To nie jest zero i nie jest wejściem progów.
2. Obie mediany są 0: `pass`. Mediany reads i CPU tego nie zmieniają.
3. Mediana baseline jest 0, a candidate jest większa od 0: `pass` tylko gdy candidate ≤ 5 ms. Inaczej `inconclusive`. Nigdy `fail`. Równe 5 ms daje `pass`.
4. Mediana baseline jest większa od 0, a candidate jest ≥ 0: progi v1. Candidate równe 0 nie jest tu osobnym zerem.
5. Inaczej `inconclusive`.

`fail` tylko z punktu „Po bramach” progów, gdy czas ma oba progi i jest korroboracja. W pozostałych przypadkach, które doszły do progów, `pass`.

## RegressCommand

Wywołanie: `regress <artifact-id> --policy v1 --json`. Argument id jest nazwą katalogu, jak w `artifact`, nie ścieżką. Flaga `--policy v1` jest obowiązkowa. `--json` jest obowiązkowe. Nie ma trybu tekstowego, `--output agent`, profilu, `--var` ani `--unsafe-direct`. Zły argument, brak id, brak `--json` albo polityka inna niż `v1` to wyjście 2 (`SqlHarnessExitCode.Safety`) i brak pola `verdict`.

Katalog to `SqlHarnessPaths.CompareDir`, ten sam co `ArtifactCommand`. Odczyt zostaje przy `ArtifactReader`. Komenda nie składa ścieżki obok jego sprawdzeń (id, link, rozmiar, wersja manifestu).

`ReadSection` rzuca, zanim zwróci rodzaj, gdy sekcji nie ma na manifeście. Samo złapanie „section is not available” nie wystarcza, gdy manifest wymienia tylko `operators`: komenda nie widzi wtedy rodzaju. Przyszła publiczna metoda na `ArtifactReader` — nie nowy plik — zwraca sprawdzony `ArtifactManifest` albo rzuca te same `ArtifactReadException`, które dziś rzucają rozwiązywanie katalogu i czytanie manifestu. `artifact --section` nadal odmawia brakującej sekcji wyjściem 2. `regress` używa manifestu tak: rodzaj `measure` albo `measure-set` od razu daje `inconclusive` i wyjście 0, nawet gdy którejś sekcji nie ma; rodzaj `compare` bez `summary` albo bez `metrics` daje to samo; gdy obie sekcje są, komenda czyta je przez `ReadSection` i woła decider. Obcy `artifactKind` zostaje odmową readera, bez werdyktu. Nie zamieniać go w rollup.

Sekcja `operators` nie jest czytana. Komenda nie otwiera `runs.jsonl`. Nie zapisuje plików w katalogu artefaktu.

Udany JSON, tymi samymi opcjami web co renderer, bez koperty błędu:

```json
{ "verdict": "pass", "regressionPolicyVersion": 1 }
```

`verdict` to `pass`, `fail` albo `inconclusive`. Kontrakt nie ustala pola `rule`. Tego pola nie ma. Stare id R1–R9 nie są kolejnością decyzji i nie wchodzą do JSON. Zostają tylko śladem w tabeli testów. R3 nie pojawia się jako werdykt.

Wyjście procesu przy werdykcie, także `fail`, to 0. Wyjście 8 nie jest używane. Wyjść 3, 4, 5 i 7 ta komenda nie nadaje: nie uwierzytelnia, nie wykonuje SQL i nie jest `watch` ani `snapshot`. Błąd odczytu nie dokleja `verdict`.

## Legacy i błędy offline

Wyjątek potwierdzony w `src/SqlHarness.Core/ArtifactReader.cs`: `ArtifactReadException` z `SqlHarnessExitCode`. Komenda przekazuje `ExitCode`. Werdyktu domenowego nie ma. Potwierdzone komunikaty i kody:

| Sytuacja | Komunikat | Wyjście |
|---|---|---|
| Nieznany id, traversal, brak katalogu, link | `Unknown artifact id.` | 2 Safety |
| Katalog bez `manifest.json` | `Artifact '<id>' has no versioned manifest and cannot be read selectively. Re-run the benchmark to create a readable artifact.` | 2 |
| Zły rodzaj, zła nazwa pliku raportu, pusta lista albo sekcja spoza trzech | `Artifact manifest is invalid.` | 2 |
| Wersja manifestu inna niż 1 | `Unsupported artifact manifest version <n>.` | 2 |
| Brak `report.json`, JSON, null, `HasMissingMembers` | `Artifact report is invalid.` | 2 |
| Plik ponad limit | `Artifact file exceeds the read limit and cannot be read selectively.` | 2 |
| IO przy katalogu albo pliku | `Artifact storage is unavailable.` | 6 LocalStorage |

Sekcja 4 specyfikacji wrzuca brak manifestu do R5 i `inconclusive`. Kontrakt i to zadanie tego nie robią. Brak manifestu, nieznany id i nieczytelny raport zostają przy readerze.

Czytelny compare bez nowej liczby: `inconclusive`, wyjście 0, nawet gdy `Repetitions` ≥ 5 i `MeasuredRunCount` raportu dzieli się przez 2 na liczbę ≥ 5. Czytelny measure, measure-set albo wejście rollupu: `inconclusive`, wyjście 0.

## Przykład bramki CI

To jest przykład, nie plik workflow do dodania teraz. Bramka patrzy na pole `verdict`. Wyjście 0 przy `fail` nie jest sukcesem CI. Wyjście inne niż 0 jest odmową odczytu albo argumentu i nie niesie werdyktu.

```powershell
$raw = sqlharness regress $artifactId --policy v1 --json
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$verdict = (ConvertFrom-Json -InputObject $raw).verdict
if ($verdict -ne 'pass') { Write-Error "regression verdict is $verdict"; exit 1 }
```

## Testy

Każdy wiersz tabeli ma test w `RegressionDeciderTests`. Wiersze, które mówią o wyjściu procesu albo o readerze, mają też test w `RegressCommandTests`. Testy komendy biorą tymczasowy `SQLHARNESS_HOME`, jak `ArtifactCommandTests`, i moduł, który rzuca przy `ExecuteAsync`. Nie czytają `~/.sqlharness` ani żywych artefaktów. W tym dokumencie nie uruchamia się `dotnet test`.

„Para kompletna” znaczy: manifest v1, rodzaj `compare`, sekcje `summary` i `metrics` są na liście, `MeasuredRunCount` wariantu = 5 na obu, tokeny czasu i reads = `measured` na obu, `Engine` nieobecne, token CPU = `measured` na obu, o ile wiersz go nie zmienia, `Mode` = `Ordered`, `Equivalent` true, a trójki rozkładów mają Min = Median = Max, o ile wiersz nie podaje innej trójki. Operatory i `missingIndexes` są obecne albo puste; werdykt ich nie używa. Czas w tabeli to mediany baseline / candidate w ms. Reads, tam gdzie rozstrzygają, też są medianami. Gdy wiersz milczy o CPU, obie mediany CPU są równe i trójki są zbite, więc CPU nie jest sygnałem.

Osobne asercje, bez nowego id kombinacji: mediana candidate dokładnie 5 ms po bramach daje `pass`; iloraz czasu dokładnie 25% (na przykład Min 90, Median 100, Max 115) nie zatrzymuje bramy 4.

| Id | Wejścia, które rozstrzygają | Pierwsze dopasowanie | Werdykt kontraktu |
|---|---|---|---|
| Z1 | Para kompletna. Czas 0 / 0. Obie trójki czasu 0, 0, 0. Reads i CPU nie zmieniają wyniku. | zera | `pass`. Sekcja 4 też `pass` (R1). Wyjście 0. |
| Z2 | Para kompletna. Czas 0 / 30. Baseline 0, 0, 0. Candidate czasu na przykład 28, 30, 32, więc iloraz ≤ 25%. | zera | `inconclusive`, nigdy `fail`. Sekcja 4 też (R2). |
| Z3 | Para kompletna. Czas 0 / 3. Trójki 0, 0, 0 oraz 3, 3, 3. Granica 5 ms jest osobną asercją, nie innym id. | zera | `pass`. Sekcja 4 też (R2, candidate ≤ 5 ms). |
| ujemna-mediana | Para kompletna. Czas baseline −100, candidate 0 (trójki −100, −100, −100 oraz 0, 0, 0). Reads 1000 / 1200. To nie jest id kombinacji kontraktu. | po bramach, przed zerami i progami | `inconclusive`, nigdy `fail`. Brama 4 tej mediany nie łapie. Progi dałyby `fail` (czas 0 ≥ −110 i +100 ≥ 5, reads 1200 ≥ 1100 i +200 ≥ 100). |
| C-mismatch-00 | Jak Z1, ale `Equivalent` false przy `Ordered`. Bramy 1, 2 i 4 by przeszły. | brama 3 | `inconclusive`. Sekcja 4 dałaby R1 `pass`. |
| C-mismatch-03 | Jak Z3, ale `Equivalent` false. Candidate ≤ 5 ms. | brama 3 | `inconclusive`. Sekcja 4 dałaby R2 `pass`. |
| C-mismatch-30 | Jak Z2, ale `Equivalent` false. Candidate > 5 ms. | brama 3 | `inconclusive`, nigdy `fail`. Sekcja 4 też `inconclusive`, ale z R2, zanim zobaczyłaby R7. |
| C-off-00 | Jak Z1, ale `Mode` = `Off`. `Equivalent` null nie jest zgodą. | brama 3 | `inconclusive`. Sekcja 4 dałaby R1 `pass`. |
| C-missing-shape | Trzy testy przy czasie 0 / 0 i trójkach 0, 0, 0. (1) Czytelny manifest compare bez `summary` albo bez `metrics`. (2) Liczba wariantu null, przy `Repetitions` 5 i `MeasuredRunCount` raportu 10. (3) Liczba 4 na jednym wariancie i 5 na drugim. | brama 1 | `inconclusive`. Sekcja 4 dałaby R1 `pass`. Nie dzielić 10 / 2. |
| C-missing-availability | Jak Z1, ale `MetricReport` null na jednym wariancie, albo token czasu lub reads inny niż `measured` (także `unavailable`, pusty i obcy) przy medianach 0. | brama 2 | `inconclusive`. Sekcja 4 dałaby R1 `pass`. |
| C-missing-equivalence | Jak Z1, ale `Equivalent` null przy `Ordered`. Drugi test decidera: obiekt `Equivalence` null. JSON compare z `equivalence` null nie jest tym wierszem. | brama 3 | `inconclusive`. Sekcja 4 dałaby R1 `pass`. Null obiektu w pliku compare to nieczytelny raport, bez werdyktu. |
| C-unstable-03 | Bramy 1–3 jak para kompletna. Czas 0 / 3. Baseline 0, 0, 0. Candidate czasu Min 0, Median 3, Max 4. Iloraz 4/3 > 25%. | brama 4 | `inconclusive`. Sekcja 4 dałaby R2 `pass`, bo 3 ms ≤ 5 ms. |
| C-undefined-00 | Mediany czasu 0 / 0. Jeden wariant Min 0, Median 0, Max 1. Drugi 0, 0, 0. Bramy 1–3 spełnione. | brama 4 | `inconclusive`. Sekcja 4 dałaby R1 `pass`. |
| C-first-match | Liczba wariantu null i równocześnie `Equivalent` false. Czas 0 / 0. | brama 1, nie brama 3 | `inconclusive`. Sekcja 4 dałaby R1 `pass`. |
| C-measure | Czytelny rodzaj `measure`. Mediany 0 nie robią z tego Z1. | brama 1 | `inconclusive`, wyjście 0. |
| C-measure-set | Czytelny rodzaj `measure-set`. | brama 1 | `inconclusive`, wyjście 0. |
| C-matrix-rollup | Wejście `SqlHarnessCompareMatrixReport`, nie komórka o rodzaju `compare`. | brama 1 | `inconclusive`, wyjście 0, gdy ten werdykt jest emitowany. Komórka `compare` jest zwykłą parą. Obcy `artifactKind` na dysku nie jest tym wierszem. |
| P1 | Para kompletna. Czas 100 / 102. Reads 1000 / 990. Trójki zbite. | progi | `pass`. Względny czas 102 < 110, różnica 2 ms < 5. Zgodne z sekcją 5. |
| P2 | Para kompletna. Czas 100 / 105. Reads 1000 / 1010. | progi | `pass`. Różnica czasu to dokładnie 5 ms, ale 105 < 110, więc próg względny nie strzela. Zgodne z sekcją 5. |
| P3 | Para kompletna. Czas 500 / 510. Reads 10000 / 10050. | progi | `pass`. Ruchy tylko absolutne (+10 ms, +50). Żaden próg względny. Zgodne z sekcją 5. |
| F1 | Para kompletna. Czas 100 / 120. Reads 1000 / 1150. CPU płaskie. | progi | `fail`. Czas: 120 ≥ 110 i +20 ≥ 5. Reads: 1150 ≥ 1100 i +150 ≥ 100. Wyjście procesu i tak 0. |
| F2 | Para kompletna, ale `Engine` = `sqlserver`. Czas 200 / 230. Reads 5000 / 4900. CPU 50 / 70, token `measured`. | progi | `fail`. Czas oba progi. Reads się poprawiają i niczego nie blokują. CPU: 70 ≥ 57.5 i +20 ≥ 5. To nie jest wiersz braku `Engine`. |
| N1 | Para kompletna, `Engine` = `sqlserver`. Czas 100 / 112. Reads 1000 / 1005. CPU 20 / 20, token `measured`. | progi | `pass`. Czas regresuje (112 ≥ 110 i +12 ≥ 5). Reads i CPU nie mają obu progów. Sam czas nie daje `fail`. |
| N2 | Para kompletna. Czas 4 / 8. Reads 50 / 55. Baseline czasu > 0, więc to nie jest zero. | progi | `pass`. Czas: 8 ≥ 4.4, ale +4 < 5. Reads: 55 ≥ 55, ale +5 < 100. Sam próg względny jest szumem. |
| G1 | Para kompletna, `Engine` = `postgres`. Czas 100 / 118. Reads 2000 / 2300. CPU 0 / 0, token `unavailable`. | progi | `fail`. Brama 2 zdejmuje CPU i idzie dalej. Reads korroborują (2300 ≥ 2200 i +300 ≥ 100). Czas: 118 ≥ 110 i +18 ≥ 5. |
| G2 | Para kompletna, `Engine` = `postgres`. Czas 100 / 102. Reads 2000 / 2010. Token CPU `unavailable`. Zestaw v1 i tak zawiera próg CPU. | progi | `pass`. Próg CPU w polityce nie przywraca wymiaru. Czas i reads nie regresują. |
| S1 | Para kompletna. Czas 100 / 130. Reads 1000 / 1300. Candidate czasu Min 100, Median 130, Max 152. (152−100)/130 = 0.40. Baseline czasu 100, 100, 100. | brama 4 | `inconclusive`. Progi dałyby `fail`, ale do nich nie dochodzi. Baseline > 0, więc R1/R2 i tak by nie wygrały. Werdykt zgodny z sekcją 5, pierwsze dopasowanie jest bramą 4. |
| M1 | Jak para, która progami byłaby `fail`: czas 100 / 150, reads 1000 / 1500. Liczba przebiegów 3 na obu wariantach. | brama 1 | `inconclusive`, nie `fail`. Baseline > 0, więc stara kolejność też doszłaby do R5. Werdykt zgodny z sekcją 5. |
| E1 | Liczby jak F1, które progami byłyby `fail`. `Mode` = `Off`. | brama 3 | `inconclusive`. Baseline > 0. Werdykt zgodny z sekcją 5. Pierwsze dopasowanie jest bramą 3, nie progami. |
| E2 | Liczby jak F1. `Ordered`, `Equivalent` false. | brama 3 | `inconclusive`, nigdy `fail`. Werdykt zgodny z sekcją 5. |
| E3 | Liczby jak F1. `Multiset`, `Equivalent` true. Decider nie liczy wierszy ponownie. | progi | `fail`. Zgodność w wybranym trybie przechodzi bramę 3. Wyjście procesu 0. |
| pg-cpu-measured | Para kompletna poza silnikiem. `Engine` = `postgres`. Token CPU `measured` na obu. Czas 200 / 230. Reads 5000 / 4900. CPU 50 / 70. Trójki zbite. | progi | `pass`. CPU nie korroboruje mimo tokenu `measured` i mimo obu progów CPU. Zostają reads, które się poprawiają. Węższy wiersz mapy zostawiłby CPU i dał `fail`. Nie stosować go. |
| engine-absent-cpu-measured | `Engine` null. Token CPU `measured`. Czas 200 / 230. Reads 5000 / 4900. CPU baseline 0, candidate 70, trójki zbite. | progi | `fail`. Token `measured` przy braku silnika może korroborować: 70 ≥ 0 i +70 ≥ 5. Zero milisekund CPU nie ustawia `postgres`. Gdyby ustawiło, werdykt byłby `pass`. |
| legacy-missing-count | Czytelny compare, sekcje są, reszta jak F1 (progami byłoby `fail`). Nowej składowej nie ma: null, nie 0. `Repetitions` 5, `MeasuredRunCount` raportu 10. | brama 1 | `inconclusive`, wyjście 0. Test deserializacji stwierdza null. Obecne 0 to inny fakt, też `inconclusive`, bo 0 < 5, ale nie jest brakiem składowej. |
| unknown-id | Id, którego katalogu nie ma. | odczyt | Brak werdyktu. `ArtifactReadException`, komunikat `Unknown artifact id.`, wyjście 2. |
| brak-manifestu | Katalog artefaktu bez `manifest.json`. | odczyt | Brak werdyktu. `ArtifactReadException` z komunikatem o braku wersjonowanego manifestu, wyjście 2. To nie jest `inconclusive` z R5. |
| raport-nieczytelny | Manifest compare poprawny, `report.json` nie deserializuje się albo `equivalence` jest null. | odczyt | Brak werdyktu. `ArtifactReadException`, `Artifact report is invalid.`, wyjście 2. |

`RegressCommandTests` sprawdza też: brak id, brak `--json`, `--policy` inne niż `v1` (w tym `latest`) dają wyjście 2 i JSON bez `verdict`; F1 przez komendę daje `verdict` `fail`, `regressionPolicyVersion` 1 i wyjście 0; measure i measure-set dają `inconclusive` i wyjście 0; legacy bez liczby daje `inconclusive` i wyjście 0; IO magazynu daje wyjście 6 bez `verdict`.

## UNPROVEN

- Obcięcie mediany wobec surowych próbek. `Distribution.From` przy parzystej liczbie bierze `(long)(((decimal)a + b) / 2)`. Decider używa zapisanego `long` i nie czyta `runs.jsonl`. Dowód: porównać zapisane `Median` z nieobciętą średnią dwóch środkowych próbek z tego pliku. Tego porównania nie ma.
- Rollup macierzy jako czytelny katalog. Dowód: katalog, którego manifest przechodzi sprawdzenia readera, a `report.json` jest `SqlHarnessCompareMatrixReport`. W źródle taki rodzaj nie jest zapisywany. Test decidera nie jest tym dowodem.
- Zapis `Engine` = `postgres` razem z tokenem CPU `measured`. Dowód: `report.json` z obu. `PostgresBenchmark.ParseStats` wstawia na CPU `BenchmarkMetricReport.Unavailable`. Fixture `pg-cpu-measured` jest syntetyczny.
- Mieszane tokeny CPU między wariantami w zapisanym raporcie. Reguła planu: wymiar zostaje tylko, gdy oba są `measured`, a `Engine` nie jest `postgres`. Dowód: raport, w którym warianty mają różne tokeny.
- Pochodzenie zer `Equivalence`. Zostaje otwarte, jak w mapie kontraktu. Dowód: odróżnić inicjalizator od obiektu wstawionego przez `CompareCellRunner` po `ResultComparer.Compare`. Odczyt selektywny tego nie rozdziela i decider tego nie zamyka.
- Inna pisownia niż dokładnie `postgres`. Writer PG używa `SqlEngineNames.Postgres`. Inny napis nie gasi CPU. Dowód: raport, którego `Engine` nie jest tym napisem, a mimo to ma znaczyć PostgreSQL. Takiego zapisu w źródle writera nie ma.
- Deserializacja braku `int?` jako null, nie 0. Tak ma zrobić test legacy. Ten commit go nie uruchamia. Dopóki test nie przejdzie, zachowanie nowej składowej jest UNPROVEN.
- Czy publiczny odczyt manifestu da się dodać bez drugiej ścieżki obok sprawdzeń linków i limitu bajtów. Dowód: test z manifestem compare, który wymienia tylko `operators`, kończy się wyjściem 0 i `inconclusive`, a `artifact --section summary` na tym samym id nadal kończy się wyjściem 2. Oraz test traversal nadal kończy się `Unknown artifact id.`

## Brak dowodu live i z innej platformy

Nie ma dowodu live. Nie ma dowodu z innej platformy. Ten krok nie otwierał bazy, nie czytał `~/.sqlharness` ani cudzych artefaktów i nie uruchamiał `dotnet test`. Ścieżki powyżej są ścieżkami kodu albo przyszłych testów na syntetycznym `SQLHARNESS_HOME`.

## Poza tym commitem

Nie aktualizować tu `plans/README.md`, capabilities, `AGENTS.md`, `README.md`, kontraktu, C# ani testów. Nie tworzyć plików z zestawu implementacji. Przyszłe `dotnet test` dla samych nowych testów, już po kodzie, to filtr `FullyQualifiedName~RegressionDeciderTests|FullyQualifiedName~RegressCommandTests`. Tego polecenia teraz nie ma z czego uruchomić.
