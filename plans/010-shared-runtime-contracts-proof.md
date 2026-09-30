# 010-shared-runtime-contracts-proof — final verification gates (closure)

HEAD: `f64a3da` (`docs(010/T4): record preserved interface boundary decisions`),
branch `fix/plan-010-shared-runtime-contracts`, worktree
`.worktrees/plan-010-shared-runtime-contracts` (baza worktree: `20a2aaf` = main).
Gate runs: 2026-09-30, w worktree. Brak pusha i merge'a (plan zabrania bez zlecenia).

## Goal

Jeden silnik polling watch emitujący zdarzenia do dwóch odbiorników
(raport z ograniczoną historią / strumień NDJSON bez retencji) przy
identycznych decyzjach terminalnych; wyłącznie identyczne czyste reguły
bounds/duration we wspólnym Core `OperationLimits`; granice interfejsów
(CLI/MCP, query/measure/compare, param-set/matrix, writery, zegary)
udokumentowane jako zachowane, nie scalone.

## Commits (20a2aaf..HEAD, 5)

| Commit | Task | Subject |
|---|---|---|
| 2ef86ce | T1 | test(010/T1): add watch report/NDJSON terminal-decision parity tests |
| edb8606 | T2 | refactor(010/T2): single watch polling engine with report/NDJSON sinks |
| 8cf5b36 | T3 red | test(010/T3): pin shared operation bounds and adapter parity cases |
| 7f3687e | T3 | refactor(010/T3): share pure operation bounds and durations via Core OperationLimits |
| f64a3da | T4 | docs(010/T4): record preserved interface boundary decisions |

Każde zadanie reviewed clean (spec ✅ + Approved; zero Critical/Important).
Final whole-branch review: poniżej w „Final review".
Rulinge: nazwa `ExecuteWatchNdjsonAsync` istnieje na warstwie modułu
(Contracts.cs:21) — plan był poprawny, brak rozjazdu; reguły o różnych
jednostkach/defaultach (s/m/h z cap 24h vs m/h/d→minuty vs liczniki)
świadomie niescalone (hard-stop planu), w tym różna polityka case
(CLI watch folduje, CLI qstop odrzuca wielkie litery, MCP folduje).
Incydent: pierwszy dispatch T3 utracony przez infrastrukturę
(model service unreachable, brak rozpoczęcia pracy); re-dispatch świeżego
implementera, drzewo nienaruszone.

## Gates (2026-09-30, HEAD f64a3da)

- Filtr planu
  `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~WatchTests|FullyQualifiedName~WatchNdjsonTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~OperationLimitsTests'`:
  SqlHarness.Tests 125/125, SqlHarness.Mcp.Tests 50/50 → exit 0.
- `dotnet build SqlHarness.sln --no-restore -warnaserror`: 0 warnings, 0 errors.
- Pełny suite bez Integration: Core/CLI 2074 passed / 0 failed / 0 skipped;
  MCP 193 passed / 0 failed / 4 skipped (2 live opt-in + 2 foreign-RID smoke,
  pre-existing).
- `git diff --check`: 0. `git status --short`: czysto (10 plików, wszystkie
  w zakresie planu + dokument wynikowy).
- Red → green: T1/T3-red to piny charakteryzujące (zielone przed i po —
  uczciwie raportowane jako siatka, nie witness-fail); pętle T2 już zgodne
  (10/10 parity od pierwszego biegu); T4 krok dokumentacyjny (filtr zielony
  = brak regresji).

## Dokumenty wynikowe

- `plans/010-interface-decisions.md` — decyzje zachowania granic (T4).
- Ten dowód: `plans/010-shared-runtime-contracts-proof.md`.

## Braki i reszty (jawne)

- Brak live DB (plan zabrania; testy na fakach; live testy skip).
- Brak dowodu platformowego poza Windows/x64 (jak wyżej).
- Deferred minors (non-blocking, do triage'u przed merge): zbędna pusta
  linia w McpMappingTests.cs; mismatch etykiety DONE_WITH_CONCERNS/Brak
  w task-4-report; zakresy linii ORC 13–30/52–61 vs faktyczne 19/52–58.

## Final review

(uzupełnione po finale — patrz ledger.)
