# 009-safety-contract-proof — final verification gates (closure)

HEAD: `29b3102` (`test(009/final): pin no read-only claim in CLI watch help and capabilities entry`),
main (praca bez worktree — decyzja w ledgerze: subagenty dziedziczą workspace root;
cudze zmiany nietknięte, `git status` czysty poza zakresem).
Gate runs: 2026-09-30, na main. Brak pusha (plan zabrania bez zlecenia).

## Goal

Granica statycznego safety widoczna dla klientów: capabilities/validate niosą
wersjonowane metadane (`analysisKind: static-visible-effects`,
`analysisContractVersion: 1`, `hiddenEffectsVerified: false`,
`objectAndPermissionStatus: unknown`); opisy query/validate/inspect/watch mówią
o statycznej kontroli widocznych efektów i roli DB przygotowanej poza
harnessem; `ClassificationLabel` bez zmian znaczenia.

## Commits (0227180..HEAD, 11)

| Commit | Task | Subject |
|---|---|---|
| 13477be | T1 red | test(009/T1): red regression witness for versioned static-analysis boundary |
| a58c15c | T1 | feat(009/T1): additive versioned static-analysis boundary on capabilities and validate |
| 1cbafab | T1 | test(009/T1): update pinned byte observations for larger capabilities-validate JSON |
| 993e3c4 | T2 red | 009/T2: regression test pinning static-visible-effects query/validate descriptions |
| 84ec9f8 | T2 | 009/T2: query/validate descriptions state static-visible-effects boundary, DB role outside harness |
| 1d3052b | T2 docs | 009/T2: docs/mcp.md query/validate rows match static-visible-effects boundary |
| 7493ad9 | T3 red | test(009/T3): red witness for preflight field stability, denylist, no-connection, no-side-effects |
| 361280b | T3 | fix(009/T3): drop read-only overclaim from MCP inspect/watch descriptions |
| 914238c | T4 | docs(009/T4): strict profile assessment as design-only draft |
| 86c3b3e | final fix | fix(009/final): align CLI/capabilities watch wording with MCP static visible-effects check |
| 29b3102 | final test | test(009/final): pin no read-only claim in CLI watch help and capabilities entry |

Każde zadanie reviewed clean (spec ✅ + Approved; zero Critical/Important).
Final whole-branch review: With fixes (1 Minor: CLI watch `read-only`) →
fix wave → scoped re-review: ADDRESSED, no new breakage.
Korekty zakresu z uzasadnieniem: `byte-budgets.json` (piny bajtów za większym
JSON; budżety-górne bez zmian), pliki CLI `SqlHarnessCli.cs`/`ValidateCommand.cs`
(opisy CLI query/validate muszą tam mieszkać — plan pominął je na liście).

## Gates (2026-09-30, HEAD 29b3102)

- Filtr planu
  `dotnet test SqlHarness.sln --filter 'FullyQualifiedName~CapabilitiesCommandTests|FullyQualifiedName~ValidateCommandTests|FullyQualifiedName~McpToolSchemaTests'`:
  SqlHarness.Tests 73/73, SqlHarness.Mcp.Tests 33/33 → exit 0
  (po final fix: 107 passed w filtrze planu + AgentWorkflow/Command).
- `dotnet build SqlHarness.sln --no-restore -warnaserror`: 0 warnings, 0 errors.
- Pełny suite bez Integration: Core 1997 passed / 0 failed / 0 skipped;
  MCP 181 passed / 0 failed / 4 skipped (pre-existing: 2 foreign-RID smoke + 2 live opt-in).
- `git diff --check`: 0. `git status --short`: czysto (tylko commity z zakresu).
- Red → green: T1 5 witness-faili, T2 1 fail, T3 1 fail — każdy na niezmienionym
  kodzie przed implementacją; T4 krok dokumentacyjny (filtr zielony = brak regresji).

## Dokumenty wynikowe

- `plans/009-strict-profile-assessment.md` — projekt zaostrzonego profilu
  (TEMP, SECURITY DEFINER, operatory, efekty zewnętrzne); bez wdrożenia
  READ ONLY i przełączania ról.
- Ten dowód: `plans/009-safety-contract-proof.md`.

## Braki i reszty (jawne)

- Brak live DB (plan zabrania; testy na fake/syntetycznym HOME; live testy skip).
- Zaparkowane rulingiem (ledger): fraza `watch` / `bounded read-only query`
  w `AGENTS.md` — realna, docs-only, ten sam overclaim co fala fix; brak drugiej
  fali fix wg skill; kandydat na follow-up. Nic na niej nie buduje.
- Deferred minors (non-blocking): normalizacja końcówek linii CRLF→LF przy
  dotkniętych plikach; metodologia red-witness T3 (uczciwie raportowana).
