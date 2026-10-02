# Kontrakt regresji — mapa pól, kolejność decyzji i niezmienniki

Status: draft, mapa pól (T1), kolejność decyzji (T2) i niezmienniki (T3). Planu implementacji tu nie ma.

HEAD mapowania: `39451fcabc7c3eac184e0370bdef7196c7e1e683`. To nie jest baza audytu planu (`8aa01f8`).

Kolejność decyzji jest w sekcji „Kolejność decyzji”. Zastępuje pierwsze dopasowanie z sekcji 4 specyfikacji. Mapy pól poniżej nie zmienia.

Niezmienniki są w sekcji „Niezmienniki” i nie zmieniają mapy pól ani kolejności decyzji.

Ten plik mapuje siedem faktów na typy z HEAD, zapisuje kolejność reguł i spisuje niezmienniki, których ta kolejność nie zmienia. Nie jest planem implementacji. Nie dodaje komend, stubów ani capabilities. Sekcja 2 w `docs/superpowers/specs/2026-09-26-benchmark-regression-policy.md` tylko nazywa wejścia, których polityka chce użyć. Kolejność reguł z sekcji 4 tej specyfikacji nie jest tu przenoszona: jest zastąpiona.

Nazwy z planu: CompareReport to `SqlHarnessCompareReport`, MetricReport to `BenchmarkMetricReport`. Nazwy typów i składowych są w pisowni źródła.

## Odczyt offline

`ArtifactReader.ReadSection` (`src/SqlHarness.Core/ArtifactReader.cs`) czyta wyłącznie `manifest.json` i `report.json`. Nie otwiera `runs.jsonl`, planów ani SQL. Rodzaje manifestu v1 to `compare`, `measure` i `measure-set`. `SqlHarnessCompareMatrixReport` nie jest rodzajem artefaktu; komórka macierzy niesie osobny `SqlHarnessCompareReport` zapisany przez `CompareCellRunner`.

Sekcja `summary` zwraca wynik `BenchmarkSummaryProjector.Project` (`src/SqlHarness.Core/BenchmarkSummary.cs`): dla compare `CompareBenchmarkSummary`, dla measure `MeasureBenchmarkSummary`, dla measure-set `MeasureSetBenchmarkSummary`. Sekcja `metrics` zwraca `ArtifactMetricsSection`. Sekcja `operators` nie niesie żadnego z siedmiu faktów.

`CompareArtifactWriter` (`src/SqlHarness.Core/Artifacts.cs`) serializuje cały raport do `report.json` opcjami `ArtifactDirectoryPublisher.JsonOptions` (`JsonSerializerDefaults.Web`: camelCase). Enum bez konwertera zapisuje się jako liczba. `runs.jsonl` niesie `RunMetadata`: wariant, numer powtórzenia, trzy liczby `long` oraz `ResultHash`. Nie niesie `BenchmarkRunMetrics` ani `BenchmarkMetricReport`.

`present` znaczy, że zapisany artefakt ma tę składową. `unknown` znaczy, że jej nie ma. Zero, pusta lista, null składowej sąsiedniej i tekst specyfikacji nie są tą składową. Nie czytałem artefaktów z `~/.sqlharness` ani innych plików użytkownika. Ścieżka zapisu poniżej jest ścieżką kodu, nie przeglądem dysku.

## Mapa

| Fakt | Składowa | `report.json` | summary | metrics | Status |
|---|---|---|---|---|---|
| Liczba przebiegów | `SqlHarnessCompareReport.MeasuredRunCount` | tak | nie | nie | `present` w pliku raportu; odczyt selektywny nie wystawia |
| Spread | brak | nie | nie | nie | `unknown` |
| Dostępność czasu | `BenchmarkMetricReport.ElapsedTimeAvailability` | gdy `MetricReport` nie jest null | ta sama kopia | ta sama kopia | `present` albo `unknown`, jak niżej |
| Dostępność CPU | `BenchmarkMetricReport.CpuTimeAvailability` | gdy `MetricReport` nie jest null | ta sama kopia | ta sama kopia | `present` albo `unknown`, jak niżej |
| Dostępność logical reads | `BenchmarkMetricReport.LogicalReadsAvailability` | gdy `MetricReport` nie jest null | ta sama kopia | ta sama kopia | `present` albo `unknown`, jak niżej |
| Equivalence pary compare | `SqlHarnessCompareReport.Equivalence` (`ResultEquivalenceReport`) | tak | tak | tak, tylko compare | `present` |
| Stabilność pary compare | brak | nie | nie | nie | `unknown` |

### Liczba przebiegów

Nośnik pary compare to `SqlHarnessCompareReport.MeasuredRunCount` (`src/SqlHarness.Core/Artifacts.cs`). Bieżący `CompareCellRunner.RunAsync` (`src/SqlHarness.Core/CompareCellRunner.cs`) wstawia tam `runs.Count`. Lista `runs` zbiera baseline i candidate dla powtórzeń od 1 do `request.Repeat`. Wywołanie z numerem 0 jest rozgrzewką i do tej listy nie wchodzi. `Repetitions` dostaje `request.Repeat`. Typ nie wiąże tych dwóch liczb. `MeasuredRunCount` nie jest liczbą przebiegów jednego wariantu: w tej ścieżce lista zawiera oba warianty.

`CompareBenchmarkSummary` kopiuje `Repetitions` i nie ma `MeasuredRunCount`. `ArtifactMetricsSection` oraz `ArtifactNamedMetrics` nie mają żadnej z tych liczb. `CompareDistribution` (`Min`, `Median`, `Max`) też nie ma licznika próbek. Próbki, z których dałoby się policzyć przebiegi wariantu, leżą w `runs.jsonl`, a `ArtifactReader` tego pliku nie czyta.

Osobnej składowej „liczba przebiegów wariantu” nie ma. Status tej liczby: `unknown`. `Repetitions` nie jest jej zamiennikiem. UNPROVEN, dopóki raport nie zapisze licznika per wariant, a sekcja summary albo metrics go nie wystawi.

Measure: `SqlHarnessMeasureReport.MeasuredRunCount` jest `runs.Count` po pętli 1..`Repeat` (`src/SqlHarness.Core/SqlHarnessModule.cs`). Summary kopiuje tylko `Repetitions`. Measure-set: `SqlHarnessMeasureSetReport.MeasuredRunCount` jest `repeat * sets.Count`, a `MeasureParameterSetReport.Repetitions` jest `repeat` (`src/SqlHarness.Core/MeasureParameterSets.cs`). `MeasureSetBenchmarkSummary` nie ma `Repeat` ani `MeasuredRunCount`. Potwierdzenie projekcji: test `Measure_set_json_summary_projects_medians_labels_and_capped_operators_without_values_or_plans` oczekuje braku właściwości `measuredRunCount` i `repeat` w JSON summary.

### Spread

W `src/SqlHarness.Core` nie ma typu ani składowej spread. Status: `unknown`.

UNPROVEN, dopóki raport nie zapisze składowej spread i sekcja summary albo metrics jej nie wystawi. Samo sąsiedztwo liczb poniżej nie jest tym faktem i nie jest poleceniem, żeby spread z nich wyliczać:

- `CompareDistribution.Min`, `Median`, `Max` na `CpuTimeMilliseconds`, `ElapsedTimeMilliseconds` i `LogicalReads` wariantu. Summary kopiuje je do `BenchmarkVariantSummary`, metrics do `ArtifactNamedMetrics`.
- `Distribution.From` (`src/SqlHarness.Core/Diagnostics.cs`) zapisuje medianę jako `long`. Przy parzystej liczbie próbek obcina średnią dwóch środkowych do liczby całkowitej. Ułamkowej mediany w `CompareDistribution` nie ma.
- `runs.jsonl` ma próbki `long` per przebieg. Reader selektywny ich nie zwraca.

### Dostępność czasu, CPU i logical reads

Wspólny nośnik to `BenchmarkMetricReport` (`src/SqlHarness.Core/Artifacts.cs`):

- `ElapsedTimeAvailability`
- `CpuTimeAvailability`
- `LogicalReadsAvailability`

Łańcuchy źródłowe to `BenchmarkMetricReport.Measured` (`"measured"`) i `BenchmarkMetricReport.Unavailable` (`"unavailable"`). Zero w `CompareDistribution` nie jest żadnym z tych łańcuchów. Na PostgreSQL `CpuTimeMilliseconds` równe 0 nie jest dowodem, że CPU zmierzono.

`CompareVariantReport.MetricReport` jest `BenchmarkMetricReport?` i ma `JsonIgnore` przy null. `BenchmarkReports.CreateVariantReport` (`src/SqlHarness.Core/BenchmarkRunner.cs`) ustawia je z `BenchmarkMetricReport.FromArtifacts`. `BenchmarkSummaryProjector.ProjectVariant` kopiuje obiekt do `BenchmarkVariantSummary.MetricReport`. `ArtifactReader.Of` kopiuje go do `ArtifactNamedMetrics.MetricReport`. `HasMissingMembers` nie wymaga `MetricReport`: raport bez tej właściwości przechodzi odczyt, a kopia zostaje null. Fixture `ArtifactReaderTests.CompareReport` tak właśnie zapisuje wariant i sekcje czyta.

Gdy `MetricReport` jest null, wszystkie trzy fakty dostępności są `unknown`. UNPROVEN, dopóki wariant w `report.json` nie ma niepustego `metricReport`, a sekcja summary albo metrics nie zachowa odpowiedniego `*Availability`.

Gdy `MetricReport` nie jest null, fakt jest `present` i oba odczyty zachowują ten sam obiekt. Pochodzenie łańcucha, które kod faktycznie zapisuje:

- SQL Server, zwykły przebieg. `SqlServerDialect.TruncatedMetricsOrNull` (`src/SqlHarness.Core/Dialect/SqlServerDialect.cs`) zwraca null, gdy `omittedMessageCount <= 0`. Jeśli każdy przebieg ma `CompareRunArtifact.Metrics` null, `FromWholeMilliseconds` zapisuje wszystkie trzy łańcuchy jako `measured`. To jest zapisany łańcuch, nie odczyt zera. Samych `BenchmarkRunMetrics` w `runs.jsonl` nie ma.
- SQL Server, obcięte komunikaty (`omittedMessageCount > 0`). Ten sam helper zapisuje wszystkie trzy jako `unavailable`. `FromRunMetrics` daje `unavailable`, gdy dowolny przebieg ma dany łańcuch `unavailable`.
- PostgreSQL, `PostgresBenchmark.ParseStats` (`src/SqlHarness.Core/Postgres/PostgresBenchmark.cs`). `CpuTimeAvailability` jest zawsze `unavailable`. Zwracane `CpuTimeMs` to 0. Tekst `BenchmarkMetricText.PostgresCpuUnavailable` mówi wprost, że to zero nie jest zmierzonym zerem; tekst trafia do `Warnings`, ale faktem dostępności jest łańcuch, nie ostrzeżenie. Czas jest `measured` tylko wtedy, gdy plan ma i `Planning Time`, i `Execution Time`; inaczej `unavailable`, a całe milisekundy są 0. `LogicalReadsAvailability` jest `measured` tylko wtedy, gdy liczniki buforów korzenia planu są obecne; inaczej `unavailable`, a `logicalReads` 0 nie jest zmierzonym zerem (`BenchmarkMetricText.PostgresBuffersMissing`).

Sąsiednie składowe, które nie są dostępnością: `ElapsedTimeMillisecondsExact`, `ElapsedWholeMillisecondsAreExact`, `PlanningTimeMilliseconds`, `ExecutionTimeMilliseconds`, `LogicalReadsSource`. Dodatni czas poniżej jednej milisekundy może dać `CompareDistribution` równe 0 przy `ElapsedTimeAvailability` = `measured` i `ElapsedWholeMillisecondsAreExact` = false. `SqlHarnessTargetIdentityReport.Engine` jest na raporcie i w summary (`Target`) i przy null jest pomijane w JSON. Brak `Engine` nie rozstrzyga dostępności CPU.

Measure-set ma drugi, inny nośnik: `MeasureCrossSetSummary.CpuTimeAvailability`, `ElapsedTimeAvailability`, `LogicalReadsAvailability`. Inicjalizator każdej z nich to `measured`. `Summarize` uznaje brak `MetricReport` (`?.`) za „nie unavailable”, a przy unavailable wstawia zera placeholdera i komentarz, że te zera nie są rankingiem. Summary measure-set kopiuje cały `CrossSetSummary`. Metrics kopiuje `MetricReport` zestawu, nie ten krzyżowy obiekt. To nie jest nośnik pary compare.

### Equivalence

Nośnik pary compare:

- `SqlHarnessCompareReport.ResultsEquivalent` (`bool?`)
- `SqlHarnessCompareReport.Equivalence` typu `ResultEquivalenceReport` (`src/SqlHarness.Core/ResultEquivalence.cs`): `Mode` (`ResultComparisonMode`: `Ordered`, `Multiset`, `Set`, `Off`), `Equivalent`, `DifferingPositions`, `BaselineOnlyCount`, `CandidateOnlyCount`

`CompareCellRunner` przed zapisem wstawia wynik `ResultComparer.Compare` do `Equivalence`, a `equivalence.Equivalent` do `ResultsEquivalent`. Tryb `Off` zwraca `Equivalent` i trzy liczniki jako null. W JSON `Mode` jest liczbą.

Summary compare kopiuje cały `Equivalence` i nie ma osobnego `ResultsEquivalent`. Metrics compare wstawia `report.Equivalence` do `ArtifactMetricsSection.Equivalence`. Metrics measure i measure-set zostawiają null; test `MeasureAndMeasureSet_AllSectionsRead` to sprawdza. Summary measure nie zawiera właściwości `equivalence` (test `BenchmarkSummaryTests`).

Inicjalizator `Equivalence`, gdy kod go nie nadpisze, to `Ordered`, `Equivalent` skopiowane z `ResultsEquivalent` i trzy liczniki 0. Test `Compare_report_json_projects_results_equivalent_and_equivalence_for_ordered_default` serializuje właśnie te zera. `HasMissingMembers` odrzuca null, więc brak właściwości w JSON nie jest odmową odczytu: zostaje inicjalizator. Zero licznika nie dowodzi, że `ResultComparer` policzył brak różnic, ani że właściwość była w pliku.

Status pary compare: `present`. Obiekt jest na raporcie i w obu sekcjach.

UNPROVEN dla pochodzenia zer: dowodem jest właściwość `equivalence` zapisana przez `CompareCellRunner` po `ResultComparer.Compare`. Odczyt selektywny nie odróżnia inicjalizatora od zapisanego `Ordered` z zerami. Artefaktów z dysku nie otwierałem.

Measure i measure-set nie mają `Equivalence`. Status equivalence dla tych rodzajów: `unknown`. UNPROVEN, dopóki taki raport nie zapisze `ResultEquivalenceReport`, a sekcja summary albo metrics jej nie wystawi. `ResultsStable` nie jest tym faktem.

### Stabilność

`SqlHarnessCompareReport` i `CompareBenchmarkSummary` nie mają `ResultsStable` ani innej składowej werdyktu stabilności. Status stabilności pary compare: `unknown`.

UNPROVEN, dopóki raport compare nie zapisze składowej werdyktu stabilności, a sekcja summary albo metrics jej nie wystawi. Spread jej nie zastępuje: spread też jest `unknown`.

Measure ma inny fakt, o innej nazwie i innym znaczeniu. `SqlHarnessMeasureReport.ResultsStable` jest ustawiane jako równość jednego `ResultHash` na zmierzonych przebiegach (`SqlHarnessModule`). Summary wystawia `MeasureBenchmarkSummary.ResultsStable`. Metrics tej składowej nie ma.

Measure-set: `MeasureParameterSetReport.ResultsStable` jest tą samą równością `ResultHash` wewnątrz zestawu (`MeasureParameterSetReportProjector.ProjectSet`). Gdy skróty się różnią, zapisany `ResultHash` zestawu jest null. Summary kopiuje `MeasureParameterSetSummary.ResultsStable` i nie kopiuje `ResultHash`. Metrics `ResultsStable` nie kopiuje. `runs.jsonl` ma `ResultHash` per przebieg; reader selektywny go nie zwraca.

Żadna z tych składowych measure nie jest werdyktem stabilności pary compare i nie jest spreadem czasu.

## Kolejność decyzji

Werdykt domenowy to `pass`, `fail` albo `inconclusive`. Pierwsze dopasowanie wygrywa. Ta kolejność zastępuje sekcję 4 w `docs/superpowers/specs/2026-09-26-benchmark-regression-policy.md`. Tam R1 i R2 były terminalne i zwracały `pass` dla zer, zanim R4–R7 sprawdziły niestabilność, brak pomiaru i equivalence. Sekcja 1 tej specyfikacji mówi, że pomiar niekompletny albo technicznie nierównoważny jest `inconclusive`. Ta sekcja usuwa tę sprzeczność. Mapa pól wyżej zostaje bez zmian. Progi z sekcji 3 specyfikacji nie zmieniają liczb i wchodzą dopiero po czterech bramach. Sekcja „Niezmienniki” powtarza to, czego ta kolejność nie zmienia.

Wejście to jeden już zapisany artefakt, czytany offline polami z mapy. `runs.jsonl` nie jest wejściem tej kolejności.

### Brama 1. Kompletność

Jedna para to artefakt, który ma manifest, sekcję `summary` i sekcję `metrics`, a `ArtifactManifest.ArtifactKind` jest `compare` (`ArtifactReader.CompareKind`). Sekcja `operators` nie jest wymagana: nie niesie żadnego z siedmiu faktów. Komórka macierzy zapisana jako osobny `SqlHarnessCompareReport` jest jedną parą. Rollup macierzy nią nie jest. `SqlHarnessCompareMatrixReport` nie jest rodzajem artefaktu.

Measure (`ArtifactReader.MeasureKind`, `measure`), measure-set (`ArtifactReader.MeasureSetKind`, `measure-set`) i rollup macierzy nie są jedną parą compare. To dopasowanie jest pierwsze, przed brakiem sekcji i przed liczbą przebiegów. Czytelny artefakt każdego z tych trzech rodzajów ma werdykt domenowy `inconclusive` i wyjście procesu 0. Ten sam wynik jest na każdym takim wierszu, także gdy obie mediany czasu są 0. Nie jest to `pass` ani `fail`. Udany odczyt nie robi z niego pary compare. Sekcja 6 specyfikacji nie nadaje werdyktu zestawom parametrów ani przekrojowi komórek macierzy; każda ukończona komórka zostaje własną parą.

Nieznany identyfikator artefaktu i nieczytelny manifest zostają przy istniejących wyjściach `ArtifactReader` i nie niosą werdyktu domenowego.

Para compare bez sekcji `summary` lub `metrics` daje `inconclusive`. To samo, gdy czytelny manifest tej sekcji nie wymienia.

Liczba przebiegów zmierzonych jednego wariantu jest `unknown`. Mapa T1 nie ma na nią składowej w sekcji `summary` ani `metrics`. Nie wolno wyprowadzać jej z `SqlHarnessCompareReport.MeasuredRunCount / 2` ani podstawiać `Repetitions`. Liczba `unknown` daje `inconclusive`. Podana liczba mniejsza niż 5 dla któregokolwiek wariantu też daje `inconclusive`. Minimum 5 zostaje. Chodzi o przebiegi zmierzone, nie o rozgrzewkę. Gdy obie liczby są podane i każda wynosi co najmniej 5, ta część bramy przechodzi. Dziś selektywny odczyt tej liczby nie wystawia, więc dzisiejsza para compare staje na tej bramie.

### Brama 2. Dostępność

`BenchmarkMetricReport` baseline albo candidate jest null: `inconclusive`. Jawne zapisane tokeny to tylko `BenchmarkMetricReport.Measured` (`"measured"`) i `BenchmarkMetricReport.Unavailable` (`"unavailable"`). Brak tokenu, pusty napis albo dowolna inna wartość na `ElapsedTimeAvailability` lub `LogicalReadsAvailability` daje `inconclusive`.

Czas i logical reads są wymagane u obu wariantów. Brama przechodzi na nich tylko przy tokenie `measured`. Token `unavailable` jest zapisany i jawny, ale znaczy, że wymiaru nie zmierzono, więc daje `inconclusive`. Nie wolno iść z takim tokenem w przypadki zera. Zero w `CompareDistribution` nie jest tokenem i nie oznacza, że metrykę zmierzono. Mapa T1 opisuje, skąd kod bierze te napisy. Sąsiednie składowe (`ElapsedTimeMillisecondsExact`, `ElapsedWholeMillisecondsAreExact`, `LogicalReadsSource`) nie są dostępnością.

CPU nie jest wymagane w tej bramie. Gdy `SqlHarnessTargetIdentityReport.Engine` jest `postgres`, a `CpuTimeAvailability` jest `unavailable`, wymiar CPU odpada i ocena idzie dalej. To dawny modyfikator R3, nie werdykt. Wejście polityki, które na PostgreSQL wskazuje CPU, nie przywraca wymiaru. CPU odpada także wtedy, gdy jego token nie jest `measured`, na dowolnym silniku: brama go nie wymaga, a `CpuTimeMilliseconds` równe 0 nie jest tokenem `measured`. Token `measured` zostawia CPU do korroboracji przy progach. Brak `Engine` nie rozstrzyga tokenu CPU.

### Brama 3. Equivalence

Brama czyta `ResultEquivalenceReport.Mode` i `Equivalent`. Liczniki `DifferingPositions`, `BaselineOnlyCount` i `CandidateOnlyCount` nie są tym faktem. Pochodzenie ich zer zostaje UNPROVEN, tak jak w mapie. Tej niepewności tu się nie zamyka.

`inconclusive`, gdy zachodzi którekolwiek:

- `Mode` jest `Off`. To dawne R6. Null w `Equivalent` przy `Off` nie jest ani zgodą, ani mismatch.
- Brak wyniku. Sekcja compare nie ma `Equivalence`, albo `Mode` jest `Ordered`, `Multiset` lub `Set`, a `Equivalent` jest null. To też dawne R6.
- Mismatch. `Equivalent` jest false przy `Ordered`, `Multiset` albo `Set`. To dawne R7. Werdykt nie jest `fail`: inny wynik nie jest wolniejszym tym samym wynikiem.

Dalej tylko wtedy, gdy `Mode` jest `Ordered`, `Multiset` albo `Set` i `Equivalent` jest true. To jest zgodność w wybranym trybie. Nazwy ze specyfikacji (`ordered`, `multiset`, `set`, `off`) są tymi wartościami `ResultComparisonMode`. Decyzja czyta enum, nie cyfrę, którą JSON zapisuje bez konwertera.

Inicjalizator z mapy T1 jest obiektem obecnym. Brama nie nazywa go brakiem tylko dlatego, że liczniki są zerami. Brakiem jest null obiektu albo null `Equivalent` poza `Off`. Odczyt, który nie odróżnia inicjalizatora od zapisanego `Ordered`, zostaje ograniczeniem mapy. Ta brama go nie zamyka.

### Brama 4. Stabilność

Para compare nie ma zapisanej składowej werdyktu stabilności. Nie wolno wstawiać `stable`. `SqlHarnessMeasureReport.ResultsStable` i `MeasureParameterSetReport.ResultsStable` nie są tą bramą i nie są spreadem czasu.

Formuła specyfikacji `(Max - Min) / Median` stosuje się do czasu: `ElapsedTimeMilliseconds` typu `CompareDistribution` na baseline i na candidate. Używa zapisanych `Min`, `Median` i `Max`. Te liczby są w summary (`BenchmarkVariantSummary`) i w metrics (`ArtifactNamedMetrics`). Status zapisanego spreadu zostaje `unknown`. Formuła nie dodaje składowej.

Obcięcie mediany do `long` przy parzystej liczbie próbek zostaje UNPROVEN wobec surowych próbek. Nie czytać `runs.jsonl`. Iloraz liczy się z zapisanych liczb jako porównanie z progiem 25%, nie jako dzielenie całkowite, które ten próg by skasowało.

Dla każdego wariantu, pierwsze dopasowanie:

- `Median` > 0 i iloraz > 25%: `inconclusive`. Wystarczy jeden wariant. To dawne R4. Próg 25% się nie zmienia.
- `Median` > 0 i iloraz ≤ 25%: ten wariant formułę przechodzi. Iloraz równy 25% przechodzi, bo warunek stopu jest „większy niż”.
- `Median` jest 0 oraz `Max` i `Min` są 0: brak obserwowanego spreadu. To nie jest zapisane `stable`.
- `Median` jest 0 i `Max` > 0: `inconclusive`. Iloraz jest niezdefiniowany. To nie jest czyste zero z R1.
- Każde inne `Median` równe 0: `inconclusive`. Jedyna mediana 0, która przechodzi dalej, to trójka 0, 0, 0.

Brama przechodzi tylko wtedy, gdy żaden wariant nie dał `inconclusive`. Oba mogą przejść formułę, oba mogą nie mieć obserwowanego spreadu, albo jeden nie ma obserwowanego spreadu, a drugi przeszedł formułę. Spread `CpuTimeMilliseconds` i `LogicalReads` nie wchodzi do tej bramy. Sekcja 4 specyfikacji nie nazywała metryki formuły; tu formuła jest jawnie na czasie, bo te `Min`, `Median` i `Max` są obecne, a próbek przebiegów się nie czyta.

### Po bramach: zera, potem progi

Gdy bramy 1–4 nie rozstrzygnęły, wchodzą przypadki zera, a dopiero po nich progi sekcji 3. Przypadki zera są terminalne. Nie wracają do bram.

Mediana czasu to `CompareDistribution.Median` na `ElapsedTimeMilliseconds`. Nie jest nią `ElapsedTimeMillisecondsExact`. Brama 2 już wymaga, żeby ten czas był `measured`, więc zero poniżej jest zmierzonym zerem całych milisekund, nie placeholderem przy tokenie `unavailable`.

- Obie mediany czasu są 0: `pass`. To dawne R1. Mediany reads i CPU tego werdyktu nie zmieniają.
- Mediana baseline jest 0, a mediana candidate jest większa od 0: `pass` tylko gdy candidate ma co najwyżej 5 ms. Inaczej `inconclusive`. Nigdy `fail`. To dawne R2. 5 ms jest podłogą absolutną czasu z sekcji 3 i się nie zmienia. Równe 5 ms daje `pass`.
- Mediana baseline jest większa od 0: to nie jest przypadek zera, także gdy mediana candidate jest 0. Obowiązują progi sekcji 3.

Ujemna mediana czasu nie jest przypadkiem zera ani wejściem progów. Werdykt jest `inconclusive`.

Liczby sekcji 3 zostają: czas +10% oraz 5 ms, logical reads +10% oraz 100, CPU +15% oraz 5 ms. Minimum 5 przebiegów jest bramą 1. Spread 25% jest bramą 4. Sekcja „Niezmienniki” te liczby powtarza. Ta kolejność ich nie zmienia.

`fail`, dawne R8, tylko wtedy, gdy czas spełnia oba swoje progi, względny i absolutny, i co najmniej jeden sygnał kosztów też spełnia oba swoje. Sygnałem są logical reads albo CPU, to drugie tylko gdy wymiar CPU nie odpadł w bramie 2. Sam czas nie wystarcza. Spełnienie tylko jednego progu metryki jest szumem, nie sygnałem. Gdy CPU odpadło, korroboracja może przyjść tylko z reads. W pozostałych przypadkach `pass`, dawne R9. `missingIndexes` i operatory godne uwagi nie zmieniają werdyktu.

Zero razem z mismatch, z brakiem albo nieznanym faktem wymaganym, albo z niestabilnym lub niezdefiniowanym spreadem, jest `inconclusive`, także gdy obie mediany czasu są 0. Wiersze Z1, Z2 i Z3 zostają tylko wtedy, gdy bramy 1–4 przeszły.

### Zmiana względem sekcji 4

Sekcja 4 sprawdzała po kolei R1, R2, modyfikator R3, potem R4, R5, R6, R7, R8 i R9. R1 i R2 kończyły ocenę, zanim R4–R7 zobaczyły brak danych, niestabilność i equivalence. Nowa kolejność stawia te sprawdzenia wcześniej: kompletność (dawne R5), dostępność (nowa brama terminalna; R3 zostaje modyfikatorem), equivalence (R6, potem R7), stabilność (R4), i dopiero potem zera (R1, R2) oraz progi (R8, R9).

Werdykt zmienia się tam, gdzie stare pierwsze dopasowanie kończyło się w R1 albo R2, a wada z R4–R7 albo brak tokenu dostępności nie była jeszcze sprawdzona. S1, M1, E1 i E2 przy medianie baseline większej od zera i tak były `inconclusive`. Przy zerze ta sama wada już nie przegrywa z R1 ani z R2.

Późniejszy plan testów może śledzić dawne id w tabeli. Ten plik nie ustala pola `rule` w JSON.

| Krok | Dawne id | Co zostaje, co się przesuwa |
|---|---|---|
| 1. Kompletność | R5 | Nadal `inconclusive` dla braku `summary` albo `metrics` na czytelnej parze compare i dla liczby przebiegów wariantu mniejszej niż 5. Liczba `unknown` też jest `inconclusive`; nie wolno jej doliczać. Czytelny measure, measure-set i rollup macierzy są tym samym `inconclusive`, z wyjściem procesu 0. |
| 2. Dostępność | R3 oraz nowa brama | Null `MetricReport` oraz czas lub reads inaczej niż tokenem `measured` są `inconclusive`, w tym przy jawnym `unavailable`. R3 nie jest werdyktem: CPU `unavailable` przy `Engine` równym `postgres` usuwa wymiar i ocena idzie dalej. CPU nie jest wymagane. |
| 3. Equivalence | R6, R7 | `Off` albo brak wyniku: `inconclusive` (R6). Mismatch: `inconclusive`, nigdy `fail` (R7). Zgodność w wybranym trybie idzie dalej. |
| 4. Stabilność | R4 | Nadal `inconclusive`, gdy iloraz czasu jest większy niż 25%. Brak zapisanej składowej nie jest `stable` i sam nie jest powodem stopu. Mediana 0 przy `Max` > 0 jest `inconclusive`. Trójka 0, 0, 0 przechodzi dalej. |
| 5. Zera | R1, R2 | Te same werdykty i ten sam próg 5 ms, ale tylko po bramach 1–4. R1 i R2 nie skracają już R4–R7. |
| 6. Progi | R8, R9 | Liczby sekcji 3 bez zmian. `fail` tylko przy regresji czasu (oba progi) i korroboracji. Inaczej `pass`. |

### Kombinacje zera

Izolowane wiersze Z1, Z2 i Z3 nie wystarczają. Tabela ma też zero z mismatch, zero z brakiem i zero z niestabilnym albo niezdefiniowanym spreadem. „Bramy 1–4 przeszły” znaczy: jedna para `compare` z manifestem, `summary` i `metrics`; liczba przebiegów wariantu jest podana i wynosi co najmniej 5 dla obu; czas i logical reads mają token `measured`; `Equivalent` jest true przy `Ordered`, `Multiset` albo `Set`; spread czasu albo ma iloraz nie większy niż 25%, albo jest trójką 0, 0, 0.

Dzisiejszy artefakt compare liczby przebiegów wariantu nie podaje. Z samego tego faktu jest `inconclusive` na bramie 1 i nie dochodzi do Z1. To nie jest polecenie, żeby policzyć tę liczbę z `MeasuredRunCount` albo z `Repetitions`.

| Id | Mediany czasu, ms (baseline / candidate) | Dodatkowy fakt | Sekcja 4 | Ta kolejność |
|---|---|---|---|---|
| Z1 | 0 / 0 | bramy 1–4 przeszły | R1 `pass` | `pass` |
| Z2 | 0 / 30 | bramy 1–4 przeszły | R2 `inconclusive`, nigdy `fail` | `inconclusive`, nigdy `fail` |
| Z3 | 0 / 3 | bramy 1–4 przeszły | R2 `pass`, bo candidate ≤ 5 ms | `pass` |
| C-mismatch-00 | 0 / 0 | `Equivalent` false w wybranym trybie; bramy 1, 2 i 4 by przeszły | R1 `pass`; R7 nie dochodzi | `inconclusive` (brama 3, R7) |
| C-mismatch-03 | 0 / 3 | to samo; candidate ≤ 5 ms | R2 `pass` | `inconclusive` (brama 3, R7) |
| C-mismatch-30 | 0 / 30 | to samo; candidate > 5 ms | R2 `inconclusive`, nigdy `fail` | `inconclusive` (brama 3, R7, nigdy `fail`) |
| C-off-00 | 0 / 0 | `Mode` = `Off` | R1 `pass`; R6 nie dochodzi | `inconclusive` (brama 3, R6) |
| C-missing-shape | 0 / 0 | brak `summary` albo `metrics` na czytelnej parze compare, liczba wariantu `unknown`, albo liczba < 5 | R1 `pass`; R5 nie dochodzi | `inconclusive` (brama 1, R5) |
| C-missing-availability | 0 / 0 | `MetricReport` null, albo czas lub reads bez tokenu `measured` (w tym `unavailable`) | R1 `pass`; dostępność nie była regułą terminalną | `inconclusive` (brama 2) |
| C-missing-equivalence | 0 / 0 | brak `Equivalence`, albo `Equivalent` null przy trybie innym niż `Off` | R1 `pass`; R6 nie dochodzi | `inconclusive` (brama 3, R6) |
| C-unstable-03 | 0 / 3 | baseline ma trójkę 0, 0, 0; iloraz czasu candidate > 25%; bramy 1–3 przeszły | R2 `pass`; R4 nie dochodzi | `inconclusive` (brama 4, R4) |
| C-undefined-00 | 0 / 0 | któryś wariant ma medianę 0 i `Max` > 0 | R1 `pass` | `inconclusive` (brama 4; iloraz niezdefiniowany) |
| C-first-match | 0 / 0 | liczba wariantu `unknown` i równocześnie mismatch | R1 `pass` | `inconclusive` (brama 1, nie brama 3) |
| C-measure | 0 / 0 | czytelny rodzaj `measure` | nie ma werdyktu pary compare | `inconclusive`, wyjście 0 |
| C-measure-set | 0 / 0 | czytelny rodzaj `measure-set` | nie ma werdyktu pary compare | `inconclusive`, wyjście 0 |
| C-matrix-rollup | 0 / 0 | czytelny rollup macierzy, nie jedna komórka rodzaju `compare` | nie ma werdyktu jednej pary | `inconclusive`, wyjście 0 |

Trójka 0, 0, 0 na obu wariantach nie jest wierszem niestabilności. Gdy bramy 1–3 przeszły, jest to Z1 i `pass`. Czytelny `measure`, `measure-set` i rollup macierzy mają ten sam werdykt `inconclusive` i wyjście procesu 0 na każdym z tych wierszy, także przy zerach.

## Niezmienniki

Ta sekcja niczego nie zmienia. Mapa pól zostaje. Kolejność decyzji zostaje. Nie ma tu uzasadnienia, żeby zmieniać progi, minimum przebiegów albo spread.

Liczby sekcji 3 w `docs/superpowers/specs/2026-09-26-benchmark-regression-policy.md` zostają: czas +10% oraz 5 ms, logical reads +10% oraz 100, CPU +15% oraz 5 ms. To zadanie nie uzasadnia ich zmiany. W specyfikacji próg względny to kandydat ≥ baseline × 1.10 (czas i logical reads) albo × 1.15 (CPU), a próg absolutny to różnica ≥ 5 ms (czas i CPU) albo ≥ 100 (logical reads; jednostka w specyfikacji: pages/buffers). Kolejność już tych liczb używa w „Po bramach: zera, potem progi” i zostawia podłogę 5 ms przy dawnym R2. Ta sekcja ich nie rusza.

### Odczyt offline

Nadal obowiązuje. Ocena czyta już zapisany artefakt. Nie otwiera połączenia i nie uruchamia benchmarku ponownie. Ścieżką odczytu zostaje `ArtifactReader`.

Specyfikacja, sekcja 1: źródłem metryki jest już zapisany artefakt, czytany offline; ocena regresji nie uruchamia benchmarku ponownie. Sekcja 7: odczyt jest offline, bez celu, bez połączenia i bez rebenchmarku, z sekcji `summary` i `metrics`.

Kontrakt, sekcja „Odczyt offline”: `ArtifactReader.ReadSection` czyta wyłącznie `manifest.json` i `report.json`. Nie otwiera `runs.jsonl`, planów ani SQL. Sekcja „Kolejność decyzji”: wejście to jeden już zapisany artefakt, czytany offline polami z mapy; `runs.jsonl` nie jest wejściem. Brama 4: nie czytać `runs.jsonl`.

### Werdykt i wyjście 0

Nadal obowiązuje. Werdykt domenowy to tylko `pass`, `fail` albo `inconclusive`. Udana ocena kończy się wyjściem procesu 0, także gdy werdykt jest `fail`. CI rozstrzyga po polu werdyktu, nie po kodzie wyjścia.

Specyfikacja, sekcja 1: werdykt jest polem ładunku i nie zależy od sukcesu procesu; wyjście `0` może nieść `fail` albo `inconclusive`. Sekcja 7: udana ocena ma wyjście procesu `0`, domenowe `fail` tego kodu nie zmienia, a CI bramkuje pole `verdict`, nie wyjście procesu. Sekcja 8: przy wyjściu `0` werdykt może być każdym z `pass`, `fail`, `inconclusive`. Przy wyjściach `2/3/4/5/6` pola werdyktu nie ma.

Kontrakt, otwarcie „Kolejność decyzji”: werdykt domenowy to `pass`, `fail` albo `inconclusive`. Brama 1: czytelny `measure`, `measure-set` i rollup macierzy mają `inconclusive` i wyjście procesu 0. Kolejność nie nadaje werdyktowi `fail` innego wyjścia. Nieznany identyfikator artefaktu i nieczytelny manifest zostają przy istniejących wyjściach `ArtifactReader` i nie niosą werdyktu domenowego.

### Wyjście 8

Nadal obowiązuje. Wyjście `8` zostaje różnicami `snapshot --diff`. Ta polityka go nie używa.

Specyfikacja, sekcja 1: kody `0/2/3/4/5/6/7/8` zostają przy obecnym kontrakcie; wyjście `8` jest wyłącznie `snapshot --diff found differences` i ta polityka go nie przedefiniowuje. Sekcja 7: wyjście `8` zostaje nietknięte (`snapshot --diff` only). Sekcja 8: wyjście `8` nie dotyczy regresji i nigdy nie jest werdyktem regresji.

Kontrakt, „Kolejność decyzji”: jedyne wyjście procesu, które ta kolejność nadaje przy werdykcie domenowym, to 0 (czytelny `measure`, `measure-set` i rollup macierzy). Błąd odczytu zostaje przy istniejących wyjściach `ArtifactReader` i bez werdyktu. Żadna brama i żaden wiersz nie nadaje wyjścia `8`.

### CPU PostgreSQL `unavailable`

Nadal obowiązuje. Gdy `CpuTimeAvailability` jest `unavailable`, a `SqlHarnessTargetIdentityReport.Engine` jest `postgres`, wymiar CPU odpada. Nie jest to zmierzone zero i samo nie rozstrzyga werdyktu. Korroboracja zostaje wtedy tylko z logical reads.

Specyfikacja, sekcja 3: na PostgreSQL wymiaru CPU nie ma, więc korroboracja może przyjść tylko z reads; bez regresji czasu werdykt nie jest `fail`, z CPU albo bez. Sekcja 4, R3: to modyfikator, nie werdykt; na `postgres` wymiar odpada, stałe `CpuTimeMs` równe 0 nie jest wejściem decyzji, ocena idzie dalej, a korroboracja jest tylko z reads.

Kontrakt, mapa „Dostępność czasu, CPU i logical reads”: `PostgresBenchmark.ParseStats` zapisuje `CpuTimeAvailability` zawsze jako `unavailable`. `CpuTimeMs` równe 0 nie jest zmierzonym zerem; mówi to `BenchmarkMetricText.PostgresCpuUnavailable`. Brama 2: gdy `Engine` jest `postgres`, a `CpuTimeAvailability` jest `unavailable`, wymiar CPU odpada i ocena idzie dalej. To dawny R3, nie werdykt. Wejście polityki, które na PostgreSQL wskazuje CPU, nie przywraca wymiaru. `CpuTimeMilliseconds` równe 0 nie jest tokenem `measured`. Akapit progów: gdy CPU odpadło, korroboracja może przyjść tylko z reads.

Brama 2 zostaje dosłownie, także zdanie „Token `measured` zostawia CPU do korroboracji przy progach”. To zdanie nie nazywa silnika. Ta sekcja go nie skreśla. Różnica wobec sekcji 3 specyfikacji zostaje otwarta: tam na PostgreSQL wymiaru CPU nie ma, więc token `measured` też nie jest korroboracją. Tej różnicy tu się nie zamyka.

### Oba progi naraz

Nadal obowiązuje. Metryka jest sygnałem regresji tylko wtedy, gdy spełnia oba progi: względny oraz absolutny. Spełnienie tylko jednego jest szumem, nie sygnałem.

Specyfikacja, sekcja 3: metryka regresuje tylko gdy oba progi są spełnione (względny AND absolutny). Jeden próg nigdy nie jest sygnałem regresji.

Kontrakt, „Po bramach: zera, potem progi”: „Spełnienie tylko jednego progu metryki jest szumem, nie sygnałem.” Wiersz progów w „Zmiana względem sekcji 4” zostawia liczby sekcji 3 bez zmian.

### `fail` to czas plus korroboracja

Nadal obowiązuje. `fail` wymaga regresji czasu (oba progi) oraz co najmniej jednego sygnału korroboracji: logical reads, oba progi, albo CPU, oba progi, na SQL Server. Sam czas nigdy nie daje `fail`.

Specyfikacja, sekcja 3: `fail` wymaga regresji czasu (oba progi) plus korroboracji z logical reads (oba progi) albo — tylko SQL Server — z CPU (oba progi). CPU jest korroboracją i samo nie wystarcza.

Kontrakt, ten sam akapit: `fail`, dawne R8, tylko gdy czas spełnia oba progi, względny i absolutny, i co najmniej jeden sygnał kosztów też spełnia oba swoje. Sygnałem są logical reads albo CPU, to drugie tylko gdy wymiar CPU nie odpadł w bramie 2. „Sam czas nie wystarcza.” W pozostałych przypadkach `pass`, dawne R9. `missingIndexes` i operatory godne uwagi nie zmieniają werdyktu.

### Minimum 5

Nadal obowiązuje. Minimum to 5 zmierzonych przebiegów na wariant. Brama 1 już mówi, że liczba `unknown` albo mniejsza niż 5 jest `inconclusive`. `Repetitions` tego nie zastępuje.

Specyfikacja, sekcja 4, R5: mniej niż 5 zmierzonych przebiegów na wariant jest `inconclusive`. Sekcja 6: minimum 5 zmierzonych przebiegów na wariant; rozgrzewka nie wchodzi do statystyki.

Kontrakt, brama 1: nie wolno wyprowadzać liczby z `SqlHarnessCompareReport.MeasuredRunCount / 2` ani podstawiać `Repetitions`. Liczba `unknown` daje `inconclusive`. Podana liczba mniejsza niż 5 dla któregokolwiek wariantu też daje `inconclusive`. Minimum 5 zostaje. Chodzi o przebiegi zmierzone, nie o rozgrzewkę. Mapa „Liczba przebiegów”: `Repetitions` nie jest zamiennikiem liczby przebiegów wariantu, a jej status zostaje `unknown`. Ta sekcja nie zastępuje tej bramy innym licznikiem.

### Spread 25%

Nadal obowiązuje. Próg zostaje 25% na formule rozkładu czasu, którą brama 4 już zapisała. Ta sekcja formuły nie zmienia.

Specyfikacja, sekcja 4, R4: iloraz `(max − min) / median` większy niż 25% na zmierzonych przebiegach wariantu jest `inconclusive`.

Kontrakt, brama 4: formuła `(Max - Min) / Median` stosuje się do `ElapsedTimeMilliseconds` typu `CompareDistribution` na baseline i na candidate, z zapisanych `Min`, `Median` i `Max`. Próg 25% się nie zmienia. Iloraz > 25% jest `inconclusive`. Iloraz równy 25% przechodzi, bo warunek stopu jest „większy niż”. Spread `CpuTimeMilliseconds` i `LogicalReads` nie wchodzi do tej bramy. Wiersz stabilności w „Zmiana względem sekcji 4” mówi to samo.
