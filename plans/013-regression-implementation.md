# Plan implementacji offline decidera regresji (013/T4)

> **2026-10-11:** the decision thresholds here are reused by [051](051-compare-regression-verdict.md). Everything about reading saved artifacts offline (run-count inference, legacy producer proof, `regress` command) is superseded.

Status: **PLANNED**. Rewizja 2026-10-11 na HEAD `729d88f`. Nie wdraża komend, stubów ani capabilities. Autorytet zachowania: `plans/013-regression-contract.md`; ten kontrakt zastępuje sprzeczne reguły starszej `docs/superpowers/specs/2026-09-26-benchmark-regression-policy.md`, zwłaszcza obowiązkową korroborację.

## Cel i aktualny stan

Stabilny pomiar 100 → 1000 ms jest regresją czasu przy stałych lub niedostępnych reads/CPU. `fail` dotyczy czasu po bramach kompletności, dostępności, equivalence i stabilności. `pass` nie jest zapewnieniem poprawy wszystkich kosztów.

`CompareCellRunner` zapisuje łączny MeasuredRunCount obu wariantów i Repetitions, lecz nie licznik per wariant. ArtifactReader czyta manifest/report, nie runs.jsonl. Manifest v1 nie identyfikuje wersji producenta. CPU PostgreSQL jest unavailable. Rozkłady zapisują long Min/Median/Max; mediana parzystej liczby może być obcięta. Zachowaj te granice.

## Zakres przyszłej implementacji

Nowe pliki:

- `src/SqlHarness.Core/RegressionPolicy.cs`: tylko v1, progi czasu +10% AND 5ms, reads +10% AND100, CPU +15% AND5ms, minimum5, spread25%.
- `src/SqlHarness.Core/RegressionDecider.cs`: czysta funkcja, bez I/O, z kolejnością i przypadkami zera z kontraktu.
- `src/SqlHarness.Cli/Commands/RegressCommand.cs`: offline `regress <artifact-id> --policy v1 --json`.
- `tests/SqlHarness.Tests/RegressionDeciderTests.cs` i `tests/SqlHarness.Tests/Cli/RegressCommandTests.cs`.

Istniejące pliki:

- `src/SqlHarness.Core/Artifacts.cs`: opcjonalne `int? MeasuredRunCount` init na CompareVariantReport; JsonIgnore przy null.
- `src/SqlHarness.Core/BenchmarkSummary.cs`: to samo na BenchmarkVariantSummary; ProjectVariant kopiuje.
- `src/SqlHarness.Core/ArtifactReader.cs`: to samo na ArtifactNamedMetrics; Of kopiuje. Dodaj publiczny odczyt sprawdzonego manifestu przez istniejące sprawdzenia id/link/rozmiaru. HasMissingMembers nie wymaga nowej liczby.
- `src/SqlHarness.Core/BenchmarkRunner.cs`: CreateVariantReport ustawia liczbę na runs.Count otrzymanej listy; rozgrzewka nie wchodzi.
- `src/SqlHarness.Cli/SqlHarnessCli.cs`: rejestracja obok ArtifactCommand.
- Istniejące testy ArtifactReader, BenchmarkSummary, benchmarków i CLI capabilities oraz `Capabilities.cs`, README.md, AGENTS.md i skill dokumentacji tylko w końcowej fazie reklamy istniejącej już komendy.

Poza zakresem: MCP, DB, progi z pliku, plan XML/SQL, zmiany istniejącego porównywania danych i czytanie runs.jsonl. Status funkcji pozostaje PLANNED do implementacji planu035.

## Liczniki i kompatybilność legacy

Zapisuj opcjonalną liczbę per wariant. Brak klucza deserializuje się do null; obecne 0 pozostaje0. Nie zmieniaj typów istniejących pól ani nie zapisuj spreadu. Null nie czyni artefaktu nieczytelnym.

Źródło liczby w wyniku jest `recorded`, `inferred-known-producer` albo `unknown`. Inferencja wymaga rozpoznanego producenta/schematu oraz przetestowanego niezmiennika sukcesu: równy licznik wariantów = Repetitions, łączny licznik =2*Repetitions, rozgrzewka wykluczona. Sprawdź zgodność obu pól; samo `/2` lub manifestVersion1 nie wystarcza. Nie dodawaj fikcyjnego producenta do starszych artefaktów. Obecne legacy bez identyfikacji daje inconclusive. Jeżeli wykonawca znajdzie rzeczywistą metadaną producenta, udokumentuje konkretną wersję i dowód oraz doda odpowiedni fixture; bez niego nie uruchamia inferencji.

## Decider

Implementuj kontrakt: rodzaj/sekcje/liczniki → dostępność czasu → equivalence → rozkłady i spread czasu → zera/progi. Koszty są opcjonalne i nie wpływają na werdykt. Brama niestabilności używa decimal i `(Max-Min)/Median >0.25`; wymaga `0 <= Min <= Median <= Max`. 0,0,0 przechodzi, mediana0/Max>0 nie przechodzi.

Dla dodatniego baseline elapsed: fail przy candidate >=baseline*1.10 AND różnica>=5ms. Dla baseline0: 0/0 pass, 0/candidate<=5 pass, 0/candidate>5 fail z null procentem. Właściwości exact nie podmieniają median long. Diagnozuj reads/CPU według kontraktu, zachowując unavailable PG CPU i null procent przy zerze. Niepoprawny koszt ma tylko diagnostykę inconclusive.

## Komenda i wynik

Argument id to nazwa katalogu jak w ArtifactCommand, nie dowolna ścieżka. Obowiązkowe `--policy v1 --json`, bez profilu/var/unsafe-direct. Zła polityka (także V1/latest/v2), brak id lub json: exit2 bez verdict. Regresja nie bierze ISqlHarnessModule.

Użyj publicznego odczytu sprawdzonego manifestu: measure/measure-set daje inconclusive przed odczytem sekcji; compare bez summary/metrics daje inconclusive. Gdy sekcje są, czytaj przez ReadSection. `artifact --section` nadal odmawia brakującej sekcji. Nie dodawaj rodzaju rollupu; test tego kształtu dotyczy tylko czystego decidera.

Udany JSON zawiera `verdict`, `regressionPolicyVersion:1`, `metricOutcomes` i `runCountEvidence`. metricOutcomes ma elapsed/logicalReads/cpu z `outcome`, medianami, `absoluteDelta`, `relativeDelta` (null przy baseline0). Brak metryki nie ma fikcyjnych median/różnic: null. Przy gate inconclusive nie oceniaj kosztów na niesprawdzonym wejściu: zwróć ich inconclusive lub unavailable zgodnie z dostępnością. runCountEvidence ma baseline/candidate: count nullable, source, a tylko dla inferencji producer i invariant. Dodaj stabilne `reason` dla nieprzejścia bram; nie używaj historycznych R1–R9 jako kolejności.

Udana ocena ma exit0, także przy fail/inconclusive. Nie ma exit8 ani zmiany artefaktu. Przekazuj ArtifactReadException.ExitCode. IOException/UnauthorizedAccessException poza readerem: exit6. Brak manifestu, błędny raport, traversal/link, zła wersja/rodzaj pozostają istniejącymi błędami readera (exit2 bez verdict); IO storage exit6. Nieznany id: `Unknown artifact id.`

## Przykład CI

```powershell
$raw = sqlharness regress $artifactId --policy v1 --json
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
$verdict = (ConvertFrom-Json -InputObject $raw).verdict
if ($verdict -ne 'pass') { Write-Error "regression verdict is $verdict"; exit 1 }
```

## Testy

Testy są przyszłe; dokumentacyjna rewizja ich nie uruchamia. Wzorce: ArtifactCommandTests (syntetyczny SQLHARNESS_HOME), ArtifactReaderTests, BenchmarkSummaryTests. CLI fake module rzuca przy ExecuteAsync, by wykryć próbę DB. Kompletna para: compare z obiema sekcjami, zapisane count5 na wariant, czas measured, Ordered Equivalent true, Min=Median=Max, reads/CPU measured i stałe o ile wiersz nie zmienia.

| Id | Wejście | Oczekiwanie |
|---|---|---|
| Z1 | 0/0, trójki0 | pass |
| Z2 | 0/30, stabilne | fail, procent null |
| Z3 | 0/3 | pass |
| Z-boundary | 0/5 oraz0/6 | pass oraz fail |
| negative | Ujemna mediana któregokolwiek wariantu | inconclusive |
| invalid-distribution | Ujemny Min, Min>Median albo Median>Max | inconclusive |
| C-mismatch-zero | 0/0,0/3,0/30 z Equivalent false | wszystkie inconclusive |
| C-off-zero | 0/0 i Off | inconclusive |
| C-equivalence-null | Null Equivalent przy Ordered; decider też null obiekt | inconclusive; null obiekt w pliku = reader error2 |
| undefined-mode | Nieznany enum z0/0 lub100/120 i Equivalent true | inconclusive |
| C-shape | Brak summary albo metrics | inconclusive exit0 |
| C-count | Brak liczby bez znanego producenta lub count4 | inconclusive |
| C-elapsed | MetricReport null lub token czasu unavailable/pusty/obcy | inconclusive; elapsed unavailable |
| C-cost-availability | 100/1000 z reads/CPU unavailable, pustym albo obcym tokenem | fail; odpowiednie koszty unavailable |
| C-unstable-zero | 0/3, candidate spread>25% | inconclusive |
| C-undefined-zero | Mediana0 i Max1 | inconclusive |
| spread-boundary | Min90 Median100 Max115 | przechodzi (25%) |
| C-first-match | Count unknown i mismatch | reason pierwszej bramy |
| C-kind | measure/measure-set/rollup w deciderze | inconclusive; tylko pierwsze dwa przez CLI |
| P1 | Czas100/102 | pass |
| P2 | Czas100/105 | pass; sam próg absolutny |
| P3 | Czas500/510 | pass |
| F1 | Czas100/120, reads1000/1150 | fail; oba wyniki regressed |
| F2 | Czas200/230, reads spadają, CPU50/70 | fail; CPU regressed |
| N1 | Czas100/112, koszty stałe | fail; koszty not-regressed |
| latency-only | Czas100/1000, reads/CPU stałe | fail |
| elapsed-thresholds | Czas50/55 oraz4/8 | fail (dokładnie oba progi) oraz pass (delta4ms) |
| cost-only | Czas100/102, reads1000/1500 i CPU20/40 | pass, koszty regressed |
| G1 | PG100/118, CPU unavailable | fail |
| G2 | PG100/102, CPU unavailable | pass |
| pg-cpu-measured | PG200/230, CPU token measured i50/70 | fail; CPU unavailable mimo tokenu |
| engine-absent | Engine null, czas200/230, CPU measured | fail; CPU diagnostyczne według tokenu |
| S1 | Candidate Min100 Median130 Max152 | inconclusive (>25%) |
| M1 | Count3 na obu | inconclusive |
| E1 | Off przy100/120 | inconclusive |
| E2 | Equivalent false przy100/120 | inconclusive |
| E3 | Multiset Equivalent true przy100/120 | fail |
| cost-zero | Reads0/99 i0/100; CPU0/4 i0/5 | not-regressed/regressed; procent null |
| cost-invalid | Czas100/120, koszt ujemny lub niespójny | fail, tylko koszt inconclusive |
| legacy-missing-count | Count null, Repetitions5, total10, manifestv1 bez wersji producenta | inconclusive; brak automatycznego /2 |
| legacy-proven | Fixture rozpoznanego producenta z dowodem niezmiennika; zgodne5/10 | count5 inferred-known-producer; normalny werdykt czasu |
| legacy-inconsistent | Rozpoznany producent, Repetitions5,total9 lub12 | inconclusive |
| count-zero | Zapisana liczba0 | source recorded, inconclusive; nie null |
| count-json | Klucz absent/0/new writer | null/0/count kopiowane do obu sekcji |
| unknown-id | Nieznany id/traversal/link | error2 bez verdict |
| missing-manifest | Katalog bez manifestu | error2 bez verdict |
| unreadable | Nieczytelny report lub null Equivalence | error2 bez verdict |
| storage | IOException | error6 bez verdict |
| CLI-options | Brak id/json, policy inna niżv1 | error2 bez verdict |

legacy-proven to test warunkowej gałęzi decidera z jawnym opisem dowodu, nie uprawnienie CLI do uznawania dowolnego v1. Jeżeli nie istnieje rozpoznawalna wersja rzeczywistego legacy, CLI musi zwracać unknown; nie fabrykuj wersji.

Test CLI dodatkowo sprawdza metricOutcomes, runCountEvidence, reason, fail z exit0, brak I/O SQL, oraz manifest tylko operators: regress inconclusive exit0, artifact summary error2.

## Weryfikacja przyszłej implementacji

- `dotnet build SqlHarness.sln -warnaserror` → exit0, brak ostrzeżeń.
- `dotnet test tests/SqlHarness.Tests --no-build --filter "FullyQualifiedName~RegressionDeciderTests|FullyQualifiedName~RegressCommandTests|FullyQualifiedName~ArtifactReader|FullyQualifiedName~BenchmarkSummary"` → wszystkie pass.
- `pwsh -NoProfile -File scripts/verify.ps1` i `pwsh -NoProfile -File scripts/verify-linux.ps1` → obie bramy exit0; Windows kończy `verify: OK`, Linux wrapper kończy `verify-linux: OK`; sprawdź pełny wynik oraz brak FAILED. Te skrypty nie wypisują VERIFICATION PASS.

## Granice dowodu i utrzymanie

Nie ma nowego dowodu runtime/live/platform; to plan. Nie odtwarzamy mediany z runs.jsonl. Rollup nie ma obecnie czytelnego manifestu. Bez wersji producenta nie da się dowieść liczników wszystkich legacy. Syntetyczny PG+CPU measured sprawdza odporność decidera.

Drift wymaga przeczytania aktualnych symboli i adaptacji mechaniki w granicach tego kontraktu. Zatrzymaj tylko krok wymagający niezgodnej decyzji produktu, niezapewnionego dowodu producenta albo wyjścia poza autoryzację. Zmiana pliku sama nie jest powodem STOP.

## Integracja z polityką equivalence (plan 046)

Przed implementacją decidera wdroż plan046. Default Equivalent ocenia dane i nie uznaje samej zmiany allowNull za mismatch. Jawne --compare-nullability / compareNullability przełącza rygorystyczną politykę; jej wynik Equivalent nadal jest bramą3. Decider używa zapisanego wyniku wybranej polityki, nie przelicza danych ani nie omija rzeczywistego mismatch. Test integracji: identyczne dane i inne allowNull przy domyślnej polityce przechodzą bramę3; przy strict dają inconclusive. Starszy zapisany mismatch pozostaje inconclusive, bez reinterpretacji nieznanych danych.
