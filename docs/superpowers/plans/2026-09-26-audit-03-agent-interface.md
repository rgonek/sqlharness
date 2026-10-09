# Agent interface and token economy Implementation Plan

**Status (2026-10-08):** DONE — T1 8f83448/bd1d72d, T2 c798c1a/0d67946, T3 eb72d97/6a9eb8b/91fa861, T4 ca51814/b5daea0/4c3a1d8/329df03. Checkboxy poniżej nie były odhaczane w trakcie wykonania.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Zapewnić agentowi przewidywalny format, krótkie odpowiedzi i możliwość rozpoznania błędu bez kolejnych prób na bazie.

**Architecture:** Stabilny kontrakt błędu, jawnie wersjonowany tryb odpowiedzi agenta i selektywne projekcje. Walidacja offline korzysta z tej samej logiki Core co wykonanie.

**Tech Stack:** .NET 8, Spectre.Console.Cli, System.Text.Json, xUnit.

**Spec:** [audyt A1–A4 i propozycje 1–4](../specs/2026-09-26-project-audit.md), [roadmapa](2026-09-26-audit-roadmap.md).

## Global Constraints

- Najpierw redakcja 01/T2; validate wymaga 01 i 02/T1.
- Nie zmieniać bez zapowiedzi kształtu sukcesów istniejącego --json. Nowa koperta sukcesów jest opt-in przez `--output agent`; obecne --json/--json-summary otrzymują strukturalne błędy.
- `--output agent`, `--json` i `--json-summary` są wzajemnie wykluczające się. Kody procesu pozostają zgodne.
- Błąd nie zawiera SQL, wartości parametrów, tokenów ani connection stringu. Brak automatycznego retry mutacji.
- Ograniczenie prezentacji nie wpływa na dane do equivalence ani deklarację kompletności snapshotu.

## Review Focus

- Błąd parsera CLI przed Dispatch oraz nieznana opcja: T1.
- Jednoczesne report i error przy częściowo zakończonym matrix: T1.
- Nieaktualna binarka, brak DB i brak skonfigurowanych profili: T2.
- Wielobajtowe Unicode i pojedyncza ogromna komórka: T3.
- Ujemna oszczędność oraz stary plik gain: T4.

## T1 — wersjonowany wynik i bezpieczne błędy

**Files:** `src/SqlHarness.Core/Contracts.cs`, `SqlHarnessModule.cs`; nowy `src/SqlHarness.Core/SqlHarnessError.cs`; `src/SqlHarness.Cli/SqlHarnessCli.cs`, `Commands/SqlHarnessCommands.cs`, `Commands/Renderer.cs`, `Infrastructure/OutputContext.cs`; nowe `tests/SqlHarness.Tests/Cli/AgentOutputTests.cs`; `tests/SqlHarness.Tests/Cli/CommandTests.cs`.

**Interfaces:** zapisać publiczny format w nowym `docs/superpowers/specs/2026-09-26-agent-output-contract.md`: koperta `schemaVersion`, `command`, `status`, `exitCode`, `result`, `error`, `truncation`. Error: `code`, `phase`, `message`, opcjonalne bezpieczne `hint` i `location`. Kod przyczyny jest stabilnym stringiem, nie nazwą wyjątku.

- [ ] Dodać testy parsujące stdout przez JsonDocument dla odmowy safety, błędnego argumentu, braku pliku, auth, target mismatch, SQL error i local storage. Stdout w trybie maszynowym ma dokładnie jeden dokument JSON.
- [ ] Dodać test częściowego matrix: zachowane zakończone komórki i informacja o błędzie; renderer nie może zgubić SafeError dlatego, że Report != null.
- [ ] Zaimplementować mapowanie kodów przed renderowaniem oraz obsługę błędów Spectre przed Dispatch. Kody nie wynikają z parsowania tekstu exception.Message. Zachować surowy help/version jako specjalne jawnie opisane operacje.
- [ ] Stare sukcesy --json pozostawić kompatybilne; --output agent daje nową kopertę, kompaktowy zapis i metadane dostępności metryk z 02/T3. Dla błędów --json i --json-summary zwracać JSON z tym samym obiektem error.
- [ ] Uruchomić `dotnet test --filter 'FullyQualifiedName~Cli'`; sprawdzić konflikt flag, stderr/stdout, brak wartości poufnych i numeryczne exit codes. Lokalny commit `feat: add consistent machine-readable outcomes`.

## T2 — odkrywanie możliwości i walidacja offline

**Files:** nowe `src/SqlHarness.Cli/Commands/CapabilitiesCommand.cs`, `DoctorCommand.cs`, `ValidateCommand.cs`; `SqlHarnessCli.cs`, `Commands/HelpProvider.cs`; nowe `src/SqlHarness.Core/Capabilities.cs`, `SqlValidation.cs`; nowe `tests/SqlHarness.Tests/Cli/CapabilitiesCommandTests.cs`, `DoctorCommandTests.cs`, `ValidateCommandTests.cs`; `README.md`, `AGENTS.md`.

**Interfaces:** `capabilities --json` opisuje build/version/contractVersion/commands/engines/limits; `doctor --json` jest lokalny; `validate [profile] --file ... --param ... --json` używa zamkniętego profilu i nie otwiera połączenia. W tej wersji validate nie dodaje nowej ścieżki --engine poza istniejącym unsafe-direct.

- [ ] Ustalić testami, że wszystkie trzy komendy nie wykonują ConnectAsync; doctor nie pobiera tokenu, nie wyświetla env i nie aktualizuje instalacji.
- [ ] W capabilities podać ograniczenia qstop/indexes per engine, typy parametrów, limity i tryby wyników. Nie listować nazw baz, kont ani wartości zmiennych profili. Build id pozwala porównać binarkę z repo.
- [ ] Validate zwraca klasyfikację, wymagane parametry, przyczynę odmowy, dostępne lokalizacje AST i fakt braku wykonania. Bez kopiowania SQL/literalów. Wymagania uprawnień lub istnienie obiektów oznacza unknown, zamiast zgadywania.
- [ ] Sprawdzić zgodność validate z preflight wykonania na tych samych fixtures SQL Server/PG; wynik validate nie jest biletem autoryzacji do późniejszej mutacji.
- [ ] Dodać krótkie opisy komend do głównego helpa i zwięzłą ścieżkę startową do AGENTS.md z odsyłaczami do szczegółów. Usunąć pusty HelpProvider tylko jeśli nadal nieużywany.
- [ ] Uruchomić trzy nowe klasy testów i Cli.CommandTests; zapisać lokalny commit.

## T3 — budżet odpowiedzi

**Files:** nowy `src/SqlHarness.Core/AgentOutputProjection.cs`; `BenchmarkSummary.cs`; `src/SqlHarness.Cli/Commands/Renderer.cs`, `SqlHarnessCommands.cs`, `Infrastructure/OutputCaptureWriter.cs`; `tests/SqlHarness.Tests/Cli/AgentOutputTests.cs`, `BenchmarkSummaryTests.cs`.

**Interfaces:** --output agent: domyślnie 16 KiB UTF-8 całej koperty; `--max-output-bytes` 4096..1048576, `--max-cell-chars` domyślnie 512, zakres 0..4096. Te opcje obowiązują tylko w trybie agent i mają wpis w capabilities. To nowe proponowane wartości, nie limity historycznego --json.

- [ ] Zdefiniować w spec kolejność zachowania danych: status/error, poprawność/equivalence, dostępność metryk, główne metryki, artefakt i liczniki pominięć, następnie detale.
- [ ] Test 1000 tabel, wiele setów/matrix cells, długa warning, ogromna komórka, Unicode, długa ścieżka artefaktu: prawidłowy JSON mieści się w budżecie wraz z newline; truncation wyjaśnia co pominięto. Jeśli minimalna koperta nie mieści się, zwrócić mały strukturalny błąd, nie ucięte bajty JSON.
- [ ] Projektować ograniczoną strukturę przed serializacją; nie tworzyć pełnego wielomegabajtowego JSON tylko po to, by go obciąć. Dla operatorów zachować limit 10 i priorytet ostrzeżeń.
- [ ] Nie tworzyć automatycznie pełnych artefaktów query zawierających dane tylko dlatego, że odpowiedź jest krótka. Wskazywać wyłącznie rzeczywiście zapisane artefakty; pominięte wyniki query mają jawną informację o braku pełnego pliku.
- [ ] Sprawdzić, że identyczny surowy wynik ma ten sam hash/equivalence niezależnie od budżetu prezentacji. Licznik output liczy bajty bez przechowywania całej kopii tekstu (koordynacja 04/T3).
- [ ] Uruchomić AgentOutputTests i BenchmarkSummaryTests; zapisać pomiary bajtów, bez deklarowania rzeczywistych tokenów; lokalny commit.

## T4 — uczciwy gain i scenariusze agentowe

**Files:** `src/SqlHarness.Core/GainStore.cs`, `CanonicalResults.cs`, `SqlHarnessModule.cs`; renderer; `tests/SqlHarness.Tests/GainStoreTests.cs`, `CanonicalResultsTests.cs`; nowe `tests/SqlHarness.Tests/AgentWorkflowTests.cs`.

**Interfaces:** zachować historyczne SavedEstimatedTokens; dodać signed `NetEstimatedTokens = rawEstimatedTokens - emittedEstimatedTokens` oraz `EstimationMethod = utf8-bytes-div-4`. Stare rekordy gain odczytywać z wyliczonym net, bez przepisywania danych użytkownika.

- [ ] Test raw=100, emitted=200 daje net=-100; agregat uwzględnia stratę, a stare pole gross saved nadal pozostaje nieujemne. Opisać baseline jako canonical raw, nie rzeczywisty koszt alternatywnego narzędzia.
- [ ] Dodać kontrolowane scenariusze bez DB: odkrycie możliwości→schema, odmowa→validate→poprawione wejście, compare→krótki wynik→detal artefaktu po 06/T2. Liczyć wywołania i bajty.
- [ ] Zarchiwizować fixtures i budżety bajtów jako regresje; nie porównywać marketingowo kosztu LLM bez tokenizera/modelu i rzeczywistego baseline.
- [ ] Uruchomić GainStoreTests i AgentWorkflowTests, zapisać lokalny commit oraz krótki raport porównania przed/po.
