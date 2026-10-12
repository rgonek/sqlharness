# Kontrakt regresji — mapa pól, kolejność decyzji i niezmienniki

> **2026-10-11:** the decision thresholds here are reused by [051](051-compare-regression-verdict.md). Everything about reading saved artifacts offline (run-count inference, legacy producer proof, `regress` command) is superseded.

Status: PLANNED; kontrakt zrewidowany 2026-10-11 na HEAD 729d88f. Funkcja nie jest wdrożona.

Historyczny HEAD mapowania (stan przed wdrożeniem, nie bieżący HEAD): `39451fcabc7c3eac184e0370bdef7196c7e1e683`. To nie jest baza audytu planu (`8aa01f8`).

Kolejność decyzji jest w sekcji „Kolejność decyzji”. Zastępuje pierwsze dopasowanie z sekcji 4 specyfikacji. Mapy pól poniżej nie zmienia.

Sekcja „Niezmienniki” zachowuje granice offline i dowodu. Kolejność decyzji zawiera poprawkę niewydanej polityki czasu i kosztów.

Ten plik zachowuje historyczną mapę siedmiu faktów z podanego wyżej SHA, zapisuje kolejność reguł i spisuje niezmienniki, których ta kolejność nie zmienia. Nie jest planem implementacji. Nie dodaje komend, stubów ani capabilities. Sekcja 2 w `docs/superpowers/specs/2026-09-26-benchmark-regression-policy.md` tylko nazywa wejścia, których polityka chce użyć. Kolejność reguł z sekcji 4 tej specyfikacji nie jest tu przenoszona: jest zastąpiona.

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

Osobnej składowej „liczba przebiegów wariantu” nie ma. Status tej liczby: `unknown`. `Repetitions` bez rozpoznanej wersji producenta i dowodu niezmiennika nie jest jej zamiennikiem. Warunkową inferencję legacy definiuje brama 1; obecny manifest nie identyfikuje wersji producenta.

Measure: `SqlHarnessMeasureReport.MeasuredRunCount` jest `runs.Count` po pętli 1..`Repeat` (`src/SqlHarness.Core/SqlHarnessModule.cs`). Summary kopiuje tylko `Repetitions`. Measure-set: `SqlHarnessMeasureSetReport.MeasuredRunCount` jest `repeat * sets.Count`, a `MeasureParameterSetReport.Repetitions` jest `repeat` (`src/SqlHarness.Core/MeasureParameterSets.cs`). `MeasureSetBenchmarkSummary` nie ma `Repeat` ani `MeasuredRunCount`. Potwierdzenie projekcji: test `Measure_set_json_summary_projects_medians_labels_and_capped_operators_without_values_or_plans` oczekuje braku właściwości `measuredRunCount` i `repeat` w JSON summary.

### Spread

W `src/SqlHarness.Core` nie ma typu ani składowej spread. Status: `unknown`.

Brak zapisanego spreadu nie blokuje oceny: brama 4 jawnie wylicza rozrzut czasu z zapisanych rozkładów; nie odtwarza surowych próbek:

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

Brak tej składowej nie blokuje bramy 4, która wylicza rozrzut czasu. Equivalence na bramie 3 sprawdza osobno zgodność wyników.

Measure ma inny fakt, o innej nazwie i innym znaczeniu. `SqlHarnessMeasureReport.ResultsStable` jest ustawiane jako równość jednego `ResultHash` na zmierzonych przebiegach (`SqlHarnessModule`). Summary wystawia `MeasureBenchmarkSummary.ResultsStable`. Metrics tej składowej nie ma.

Measure-set: `MeasureParameterSetReport.ResultsStable` jest tą samą równością `ResultHash` wewnątrz zestawu (`MeasureParameterSetReportProjector.ProjectSet`). Gdy skróty się różnią, zapisany `ResultHash` zestawu jest null. Summary kopiuje `MeasureParameterSetSummary.ResultsStable` i nie kopiuje `ResultHash`. Metrics `ResultsStable` nie kopiuje. `runs.jsonl` ma `ResultHash` per przebieg; reader selektywny go nie zwraca.

Żadna z tych składowych measure nie jest werdyktem stabilności pary compare i nie jest spreadem czasu.

## Kolejność decyzji

Ocena działa offline dla jednej pary compare. Werdykt oznacza wynik kontroli regresji czasu, nie poprawę wszystkich kosztów. Reguły tej sekcji zastępują sprzeczne reguły starszej specyfikacji, w tym obowiązkową korroborację.

Zmiana 2026-10-11 (HEAD 729d88f): stabilne 100 ms → 1000 ms przy stałych reads i CPU musi dawać `fail`. Koszty są diagnostyką; ich brak albo poprawa nie unieważnia pomiaru czasu. Progi pozostają bez zmian.

### Brama 1. Kompletność i liczba przebiegów

- Czytelny measure, measure-set albo wejście rollupu: `inconclusive`. Komórka macierzy zapisana jako compare jest zwykłą parą. Nie dodawać nowego rodzaju manifestu dla rollupu; obecny reader go odrzuca.
- Compare bez summary lub metrics: `inconclusive`.
- Preferuj zapisane `MeasuredRunCount` per wariant. Oba muszą wynosić co najmniej 5; rozgrzewka nie wchodzi. Obecne 0 to liczba, nie brak.
- Legacy: wyprowadzenie licznika wolno stosować tylko dla rozpoznanej wersji producenta i schematu, których udokumentowany i przetestowany niezmiennik udanego zapisu gwarantuje dokładnie `Repetitions` przebiegów na każdym wariancie oraz łączny `MeasuredRunCount = 2 * Repetitions`. Sprawdź dodatnią liczbę całkowitą, zgodność pól i minimum 5.
- Sam manifestVersion 1, parzysta liczba lub obecność Repetitions nie identyfikują producenta. Dzisiejszy manifest nie niesie wersji producenta; obecnie brak dowodu dla ogólnej inferencji starych artefaktów. Bez rozpoznania albo przy sprzecznych licznikach: `inconclusive`, bez automatycznego `/ 2`.
- Źródło liczby w wyniku: `recorded`, `inferred-known-producer` (z identyfikatorem producenta i niezmiennikiem) albo `unknown`. Nie nadpisuj artefaktu wywnioskowanymi danymi.

Nieznany id, brak manifestu i nieczytelny raport pozostają błędami ArtifactReader bez werdyktu.

### Brama 2. Dostępność czasu

Na obu wariantach wymagane są MetricReport i dokładny token `ElapsedTimeAvailability = measured` (porównanie porządkowe). Brak, null, unavailable, pusty lub obcy token daje `inconclusive`. Zero nie dowodzi dostępności.

Reads i CPU nie są wymagane. Diagnostyka danej metryki jest dostępna tylko gdy oba warianty mają jej token measured. Inaczej ma wynik `unavailable`. Na Engine = postgres CPU zawsze jest unavailable, nawet dla syntetycznego tokenu measured. Brak Engine nie oznacza postgres; nie zgaduj z zera.

### Brama 3. Equivalence

Off, brak Equivalence, null Equivalent lub niezdefiniowany enum trybu: `inconclusive`. Equivalent false przy Ordered, Multiset lub Set: `inconclusive`, nigdy wydajnościowy fail. Dalej przechodzi tylko true przy jednym z tych trzech trybów. Liczniki różnic nie zastępują wyniku equivalence. Reader nadal odrzuca nieczytelny obiekt bez werdyktu.

### Brama 4. Stabilność czasu

Dla obu wariantów wymagaj nieujemnych `Min <= Median <= Max`. Niepoprawny rozkład: inconclusive. Dla Median > 0 licz `((decimal)Max - Min) / Median`; wynik > 0.25 daje inconclusive, równo 0.25 przechodzi. Trójka 0,0,0 przechodzi jako brak obserwowanego rozrzutu całych milisekund. Mediana 0 z Max > 0 daje inconclusive. Nie czytaj runs.jsonl ani nie nazywaj rozrzutu czasu stabilnością wartości wyników.

### Po bramach: zera i progi

Czas jest oceniany na zapisanej medianie całych milisekund; pola exact nie zastępują jej po cichu.

| Baseline / candidate | Wynik |
|---|---|
| 0 / 0 | pass |
| 0 / dodatnie <= 5 ms | pass — zachowana podłoga dla kwantyzacji krótkich pomiarów |
| 0 / > 5 ms | fail na podstawie progu absolutnego; zmiana względna niezdefiniowana (null) |
| baseline > 0 / candidate >= 0 | fail wtedy i tylko wtedy, gdy candidate >= baseline * 1.10 ORAZ candidate - baseline >= 5 ms; inaczej pass |

0 → 5 ms jest jawnym wyjątkiem od dodatniego-baseline progu >= 5; 0 → 6 ms daje fail. Zera nie omijają bram 1–4. Ujemne lub niespójne rozkłady są odrzucone wcześniej.

### Diagnostyka kosztów

Raportuj `metricOutcomes.elapsed`, `.logicalReads`, `.cpu` jako `regressed`, `not-regressed`, `unavailable` lub `inconclusive`, z medianami, różnicą absolutną i względną (null przy baseline 0). Elapsed odzwierciedla werdykt; przy bramie niepewności jest inconclusive, a przy niedostępnym czasie unavailable. Werdykt pochodzi wyłącznie z czasu po bramach.

- Reads przy baseline > 0: oba progi candidate >= baseline * 1.10 i różnica >= 100.
- CPU przy baseline > 0: oba progi candidate >= baseline * 1.15 i różnica >= 5 ms.
- Dostępny koszt z baseline 0: 0/0 to not-regressed; dodatnia candidate to regressed przy różnicy >= podłoga metryki, inaczej not-regressed; procent null.
- Ujemny lub niespójny rozkład kosztu: tylko jego diagnostyka inconclusive; nie blokuje czasu.
- Rosnące koszty bez regresji czasu nie dają fail, lecz są widoczne jako regressed. Operatory i missingIndexes nie zmieniają werdyktu.
- Używaj decimal przed odejmowaniem i mnożeniem, bez przepełnienia long.

## Niezmienniki

- Offline, bez celu DB, połączenia, rebenchmarku, SQL, planów i runs.jsonl.
- Minimum 5 zmierzonych przebiegów na wariant, equivalence przed progami, spread czasu >25% daje inconclusive.
- Dostępność jest tokenem; CPU PostgreSQL pozostaje niedostępne.
- Dla dodatniego baseline oba progi stosuje się razem. Wyjątki baseline zero są określone powyżej.
- Udana ocena daje exit 0 także przy fail/inconclusive; CI bramkuje po verdict. Błąd argumentu/odczytu nie niesie verdict.
- Exit 8 pozostaje snapshot --diff.
- regressionPolicyVersion = 1: to korekta niewydanej funkcji PLANNED.

Pełna macierz akceptacyjna jest w plans/013-regression-implementation.md.

## Integracja z polityką equivalence (plan 046)

Przed implementacją decidera wdroż plan046. Default Equivalent ocenia dane i nie uznaje samej zmiany allowNull za mismatch. Jawne --compare-nullability / compareNullability przełącza rygorystyczną politykę; jej wynik Equivalent nadal jest bramą3. Decider używa zapisanego wyniku wybranej polityki, nie przelicza danych ani nie omija rzeczywistego mismatch. Test integracji: identyczne dane i inne allowNull przy domyślnej polityce przechodzą bramę3; przy strict dają inconclusive. Starszy zapisany mismatch pozostaje inconclusive, bez reinterpretacji nieznanych danych.
