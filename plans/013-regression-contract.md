# Kontrakt regresji — mapa pól

Status: draft, T1 only

HEAD mapowania: `39451fcabc7c3eac184e0370bdef7196c7e1e683`. To nie jest baza audytu planu (`8aa01f8`).

Kolejność decyzji nie jest w tym pliku rozstrzygnięta.

Ten plik mapuje siedem faktów na typy z HEAD. Nie opisuje kolejności reguł, nie spisuje sekcji niezmienników i nie jest planem implementacji. Nie dodaje komend, stubów ani capabilities. Sekcja 2 w `docs/superpowers/specs/2026-09-26-benchmark-regression-policy.md` tylko nazywa wejścia, których polityka chce użyć. Kolejność reguł z tej specyfikacji nie jest tu przenoszona.

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
