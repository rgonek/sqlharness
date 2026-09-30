# Plan 004 — dowód: inspect pod wspólną blokadą operacji DB (R1)

Branch: `fix/plan-004-mcp-inspect-gate` (worktree `.worktrees/plan-004-mcp-inspect-gate`,
start `8349fa7`). Bez merge/push — czeka na decyzję, jak 001/002.

Commity: `ff5d0b6` T1 testy RED, `32f0cc2` T2 fix gate GREEN,
`9735b1a` T3 testy release, `bc5758c` T3 docs.

## Zmiana (5 plików, +249/−11)

- `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs` — `InspectAsync` przez
  `RunDbAsync("sqlharness_inspect", ctx, null, null, …)`; mapowanie
  (`MapInspect` + `ThrowIfUnknown`) i timeouty per kind (ping 5, inne 30)
  nietknięte; sygnatura bez zmian, liczba tools 11.
- `src/SqlHarness.Mcp/McpExecutionGate.cs` — `DbTools += "sqlharness_inspect"`;
  `IsDbTool` obejmuje dokładnie sześć narzędzi DB.
- `tests/.../McpLifecycleTests.cs` — blokady: query↔inspect, inspect↔query+inspect,
  capabilities/validate dostępne, klasyfikacja six, release po odmowie
  mapowania / błędzie Core / host-shutdown.
- `tests/.../McpCancellationTests.cs` — rename midflight-cancel na inspect
  (superset: `CancelObserved` + `TryEnterDb` + next-inspect), deadline 1 s.
- `docs/mcp.md` — inspect w gate-liście, safe-local bez inspect (3 zdania).

## Dowody (kontroler, worktree, 2026-09-29)

- Fokus `McpLifecycleTests|McpCancellationTests`: 28 passed, 0 failed.
- `dotnet build --no-restore -warnaserror`: 0 warn, 0 error.
- Pełny `--filter 'FullyQualifiedName!~Integration'`: Core 1915/0/0,
  MCP 154 passed, 0 failed, 4 skipped (2 foreign-RID + 2 live opt-in).
- `git diff --check`: 0. Dyskryminujący dowód regresji: T1 RED 3/24 na starym
  kodzie (inspect odpowiadał success zamiast BUSY) → T2 GREEN 24/24 bez edycji testów.
- Final review całej gałęzi: Ready to merge, 0 Critical/Important.

## Rulingi i odroczenia (ledger `.superpowers/sdd/004-mcp-inspect-gate/`, usunięty po review)

1. Preflight czysty — start T1 (brak konfliktów z planem ani driftu 003).
2. T3 Important (brak RED-phase dla testów release) zaparkowany bez rundy fix:
   asercje release przechodzą na starym kodzie pusto (bez gate nie ma slotu
   do wycieku), więc RED-run nie dyskryminuje; final review potwierdził.
3. Odroczone minory (żaden nie blokuje merge): holder-assertion bez `Text()`,
   prefiksy `local*`, fraza `discovery` w `McpExecutionGate.cs:71`,
   EOF tylko via hostShutdown, wording raportu budget-vs-timeout.

## Braki

Brak live DB (jawny; live testy skip). Flake
`Progress_fires_only_for_a_client_token…` pre-existing — w tych runach zielony
(fokus 28/28, pełny 154/0/4); baseline diagnozuje plan 005.
