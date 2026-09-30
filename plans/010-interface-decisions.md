# 010/T4 — Decyzje zachowania granic interfejsów (nie scalać)

**Status:** DECYZJE (dokument, nie wdrożenie — utrwala, czego T1–T3 świadomie nie scaliły)
**Data:** 2026-09-30
**Baza:** T1–T3 planu 010 w tym worktree (`2ef86ce` testy parytetu watch,
`edb8606` jeden silnik polling z odbiornikami `ReportWatchSink` /
`NdjsonWatchSink`, `8cf5b36` + `7f3687e` współdzielone `Core/OperationLimits.cs`).
**Dowód live:** brak — krok wyłącznie dokumentacyjny, bez live DB.

## Cel

T1–T3 usunęły dwa realne powielenia: podwójną pętlę polling watch
(`WatchRunner.cs`) oraz podwójne czyste reguły bounds/duration
(`OperationLimits.cs`). Pozostałe podobieństwa wymienione niżej to granice
świadome — podobna metoda nie jest tu powodem do scalenia. Każda sekcja to
jedna pozycja, której brief T4 zabrania scalać.

## 1. Adaptery CLI/MCP pozostają odrębne

`Cli/Commands/WatchCommand.cs` (`WatchCommand.Settings` z atrybutami
`CommandOption`/`DefaultValue`) i `Mcp/McpOperationMapper.cs` (metody
`Parse*`, stałe `Default*`) to dwa transporty o odmiennych kontraktach:
formaty wejścia, zestawy sufiksów jednostek, polityka wielkości liter
i białych znaków, wartości domyślne oraz teksty błędów. Współdzielą
wyłącznie czyste jądro konwersji i zakresów (`OperationLimits.cs` —
bez komunikatów, nigdy nie rzuca błędów transportowych), a `Core`
niezależnie re-waliduje bounds w czasie wykonania.

Scalenie adapterów zunifikowałoby jednostki i defaulty, które mają
świadomie pozostać odmienne (warunek zatrzymania planu), oraz
przeniosłoby teksty błędów jednego transportu do drugiego.

## 2. Operacje query/measure/compare (i pozostałe) pozostają odrębne

Rodzina `SqlHarnessOperation` (`Core/Contracts.cs`) ma 14 typów operacji,
każdy z własnym typem raportu (m.in. `SqlHarnessQueryReport`,
`SqlHarnessMeasureReport`, `SqlHarnessCompareReport`,
`SqlHarnessCompareMatrixReport`). Wspólna metoda wykonania (dispatch
fasady) nie czyni ich jedną operacją: różnią się semantyką (pojedyncze
wykonanie vs. powtórzenia vs. porównanie wariantów), kształtem raportu
oraz projekcjami (`BenchmarkSummary.cs`, `AgentOutputProjection.cs`).

## 3. Zestawy parametrów measure i macierz compare pozostają odrębne

`MeasureParameterSets.cs` mierzy jedno zapytanie na jednej sesji:
setup raz, rotacja zestawów w kolejności użytkownika, stabilność
wyłącznie wewnątrz zestawu, współdzielony stan plan-cache. `CompareMatrixRunner.cs`
porównuje dwa warianty zapytania z nowym połączeniem i jednym setupem
na wartość, sekwencyjnie w kolejności użytkownika; pierwsza awaria
komórki zatrzymuje przebieg (`CompareMatrixCellFailedException`
z raportem cząstkowym). To różne kontrakty wykonania, nie dwa
smaki tej samej pętli — scalenie zatarłoby reguły rotacji vs. stop-on-first-failure.

## 4. Writery domenowe pozostają odrębne

`ArtifactDirectoryPublisher.cs` współdzieli wyłącznie mechanikę cyklu
życia katalogu (nazewnictwo pod stałym rootem, staging, publikacja
przez move, rollback niemaskujący pierwotnej awarii). Polityka treści —
co jest zapisywane i które wrażliwe payloady mogą towarzyszyć raportowi —
pozostaje we writerze domenowym (`CompareArtifactWriter`,
`QueryStoreArtifacts`, `IndexAnalysisArtifacts`, `WatchNdjsonWriter`).
`SnapshotStore`/`GainStore` w ogóle nie używają tego kontraktu.
Scalenie writerów przeniosłoby decyzje o wrażliwych payloadach do
wspólnego kodu.

Zachowane: brak retencji historii w NDJSON (`Capabilities.cs` wpis
`watchNdjson`: każde `changed` emitowane natychmiast; odbiornik
`NdjsonWatchSink` nie gromadzi historii, w przeciwieństwie do
`ReportWatchSink` z `HistoryLimit`).

## 5. Zegary pozostają wstrzykiwane, nie współdzielone jako statyk

Silnik watch pracuje na wstrzykiwanym `IWatchClock`: produkcja używa
zegara systemowego (`DateTimeOffset.UtcNow`), a testy parytetu T1
fałszywego zegara. Jeden monotoniczny budżet (`IWatchBudget` /
`SystemWatchBudget` na `Stopwatch`, `CancelAfter`) obejmuje connect,
wszystkie polle i wszystkie opóźnienia. Unifikacja zegarów we wspólny
statyczny czas systemowy złamałaby determinizm testów; unifikacja
budżetu z limitem pojedynczego polla złamałaby regułę „jeden budżet
na całą operację" z T2.

## 6. ADR: publiczne `object? Report` i `OperationReportContract` zachowane

Zgodnie z [ADR T2](../docs/superpowers/plans/2026-09-26-audit-05-architecture-adr-t2-typed-result.md):
publiczne `SqlHarnessOutcome.Report` pozostaje `object?` (kompatybilny
adapter, bez migracji `ISqlHarnessModule`, rendererów CLI,
`AgentOutputProjection`, writerów artefaktów i fake'ów testowych),
a typowany kontrakt jest egzekwowany wewnętrznie:
`Core/OperationReportContract.cs` mapuje 14 operacji na dozwolone
raporty (`Measure` dopuszcza `SqlHarnessMeasureReport` i
`SqlHarnessMeasureSetReport`; awarie niosą `null`), dispatch fasady
owija zwroty w `Checked(...)`, a `ContractsTests` pokrywa pary
14 × 15 i domkniętość rodziny operacji. Zamiana na w pełni typowany
publiczny wynik byłaby zmianą publicznego API bez zysku behawioralnego.

## Co ten dokument świadomie nie robi

- Nie zmienia kodu ani testów T1–T3 ani żadnego innego pliku.
- Nie scala niczego z sekcji 1–6; przyszła zmiana dowolnej granicy
  wymaga osobnej decyzji i własnego planu.
- Nie ujednolica jednostek ani defaultów o świadomie odmiennych
  wartościach (warunek zatrzymania planu 010).
