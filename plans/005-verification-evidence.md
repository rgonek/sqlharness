# 005-verification-evidence — pełny zestaw weryfikacyjny (T4, FINAL)

HEAD: `39519d5` (`test: assert token-less progress silence over a settle window (005/T2)`),
branch `fix/plan-005-mcp-verification`, worktree `.worktrees/plan-005-mcp-verification`.
Data runów: 2026-09-29. Kod prod/test nietknięty względem T2/T3 (T4 wyłącznie dokumentacyjny).

## 1. Filtr MCP z briefu (a)

Komenda:

```powershell
dotnet test SqlHarness.sln --filter 'FullyQualifiedName~McpLifecycleTests|FullyQualifiedName~McpStdioProcessTests' --verbosity minimal
```

| Pozycja | Wynik |
|---|---|
| Exit | 0 |
| `SqlHarness.Mcp.Tests` | Passed 24 / Failed 0 / Skipped 2 / Total 26, Duration 26 s |
| `SqlHarness.Tests` | brak dopasowań do filtra (exit całości 0) |
| Wall | niemierzony osobno (restore + build w tym samym wywołaniu) |

Skipi: wyłącznie nie-natywne RID-y (`..._on_linux_x64`, `..._on_osx_arm64`).
Bramki 004 inspect↔query w zakresie filtra: zielone (brak FAIL).

## 2. Build z `-warnaserror` (b)

Komenda:

```powershell
dotnet build SqlHarness.sln --no-restore -warnaserror --verbosity minimal
```

| Pozycja | Wynik |
|---|---|
| Exit | 0 |
| Ostrzeżenia / błędy | 0 Warning(s), 0 Error(s) |
| Czas | Time Elapsed 00:00:03.97, wall 4 s |

## 3. Pełna suita bez integracji (c)

Komenda:

```powershell
dotnet test SqlHarness.sln --filter 'FullyQualifiedName!~Integration' --verbosity minimal
```

| Pozycja | Wynik |
|---|---|
| Exit | 0 |
| `SqlHarness.Tests` | Passed 1915 / Failed 0 / Skipped 0 / Total 1915, Duration 10 s |
| `SqlHarness.Mcp.Tests` | Passed 154 / Failed 0 / Skipped 4 / Total 158, Duration 36 s |
| Razem | Passed 2069 / Failed 0 / Skipped 4 |
| Wall | 49 s |

Skipi (4): 2× nie-natywne RID-y (jak w §1) + 2× live stdio
(`McpStdioLiveTests.Live_postgres_stdio_drive`, `Live_sqlserver_stdio_drive` —
testy wymagające żywej bazy; brak dowodu live — patrz §5).

## 4. Co ustalono

Progress-timeout (`Progress_fires_only_for_a_client_token_without_sql_payload`,
limit 15 s w `WaitForProgressCountAsync`): przyczyna USTALONA i NAPRAWIONA test-only.
Odtworzenie 3/3 pod dual-load (2× pełna suita MCP współbieżnie, 6 FAIL-eventów,
`TaskCanceledException` w pierwszym czekaniu). Przyczyna: klient SDK 2.2.0
(`ProcessMessagesCoreAsync` — "fire and forget" + `ForceYielding`) obsługiwał
komunikaty współbieżnie, a odbiornik progress (`SendRequestWithProgressAsync`,
per-call, usuwany `await using` z chwilą powrotu wyniku) znikał, gdy odpowiedź
wygrywała wyścig z notyfikacją — serwer wysyłał poprawnie (`Started` przed `run()`).
Fix (commity `6c41f91`, `39519d5`, wyłącznie `McpLifecycleTests.cs`):
obserwacja progress przez sesyjny `RegisterNotificationHandler` (żyje dłużej niż
call) + asercja ciszy bez tokena przez okno settle (poll 25 ms / 300 ms,
`AssertNoProgressGrowthAsync`). Weryfikacja fixu tym samym dual-load: 0/0,
154/0/4 w obu procesach. Prod-code nietknięty.

## 5. Czego NIE ustalono

- Publish-stall (`PublishAsync`, budżet 10 min): NIGDY nie odtworzony.
  Linia bazowa: Duration testu `..._on_this_rid` 16–17 s solo (7/7 runów solo
  w T1–T3; Duration 26 s w §1 obejmuje restore+build+testy całego wywołania,
  nie sam publish). Reguła: Duration >2× ponad 16–17 s (próg ~32–34 s)
  w przyszłości to sygnał do reinstrumentacji (`PublishAsync`: rozróżnienie
  restore/publish/launch, logi), nie do zgadywania dziś. Zero zmian kodu —
  (a) stdout/stderr już opróżniane współbieżnie przed `WaitForExitAsync`
  (pomiar M1: stdout 570 B / stderr 0 B), (b) brak awarii do rozróżnienia
  (zawsze exit 0), (c) `Kill(entireProcessTree: true)` już w kodzie.
- Dowód live/platformowy: BRAK. Testy live (`Live_*_stdio_drive`) skipowane
  bez żywej bazy; runy wyłącznie Windows x64 (RID-y linux/osx skipowane).
  Poza zakresem T4 (live DB, deploy — zabronione briefem).

## 6. Statusy T1–T3

- T1 (`task-1-report.md`): DONE dokumentacyjnie, 0 commitów, timeoutów nie
  odtworzono (przyczyny nieustalone na tamtym etapie).
- T2 (`task-2-report.md`): DONE — fix #1 (`6c41f91`) + runda review I1
  (`39519d5`); limit 3 fixów niewyczerpany; publish bez zmian (brak pomiaru).
- T3 (`task-3-report.md`): DONE — 0 fixów (faza B nieuzasadniona pomiarem);
  3 punkty hardeningu planu potwierdzone jako już obecne w kodzie;
  jednorazowe flaki T2 zamknięte (artefakt metodologii / overload), sekwencyjnie
  nieodtworzone (publish 16–17 s ×3, precancelled-token 3/3 PASS, filtr 24/0/2).
- T4 (niniejszy): wyłącznie ten plik evidence; limitów nie podnoszono;
  timeoutów środowiska nie było (wszystkie komendy exit 0 w pierwszym podejściu).

## 7. Kontrole

- `git diff --check`: exit 0.
- `git status --short`: tylko `plans/005-verification-evidence.md` (nowy plik).
- `plans/README.md`: nietknięty (indeks aktualizuje kontroler).
