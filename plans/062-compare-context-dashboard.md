# Plan 062: Kontekst compare i dashboard optymalizacji SQL

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking. Wybór wykonania pozostaje do użytkownika.

**Goal:** Pokazać operatorowi historię optymalizacji SQL w sesji i dowody pojedynczej próby compare, przy minimalnym kontekście od agenta.

**Architecture:** Jedna globalna polityka steruje wymaganiem kontekstu CLI i zamrożonym kontraktem MCP. Core przenosi parę subject/scope do journala. W MCP request-scoped dotychczasowy obiekt `scope` wskazujący profil/vars zostaje przemianowany na `target` we wszystkich narzędziach zależnych od celu; tekstowe `scope` oznacza wyłącznie fragment SQL w `compare`. Dashboard buduje read model wersji i prób z zachowanej historii; UI pokazuje SQL → zakres → próby oraz porównanie obu stron bez nowego klasyfikatora regresji.

**Tech Stack:** C#/.NET, System.CommandLine, ModelContextProtocol SDK, SQLite, React, TypeScript, TanStack Query/Router, xUnit, Vitest.

**Spec:** [2026-10-12-compare-dashboard-design.md](../docs/superpowers/specs/2026-10-12-compare-dashboard-design.md).

## Status i zależności

- DONE on feature branch; P2; nakład L; zapisano 2026-10-12 na `52bdc23`; implementacja na `feat/plan-062-compare-context`; bramki Windows/Linux zielone na `3ca11c8`; review T7 Luna xhigh i całej gałęzi Sol low zatwierdzone; bez Critical/Important; dwa Minor zapisane w dowodach.
- Wymaga istniejącego werdyktu compare (051, DONE) i obecnych ustawień/journala/dashboardu.
- 055–061 nie są twardą zależnością. Szczególnie 060/061 zmieniają ten sam katalog MCP, mappery i matrix; realizować sekwencyjnie lub z integracją aktualnej bazy. Nie przywracać usuniętych narzędzi ani starych nazw argumentów.
- Cały plan jest jednostką wykonania: kontekst, zapis, API, oba ekrany i dokumentacja.
- Drift check: `git diff --stat 52bdc23..HEAD -- src/SqlHarness.Core src/SqlHarness.Mcp src/SqlHarness.Cli src/SqlHarness.Dashboard plans/README.md` oraz `git worktree list --porcelain`.

## Global Constraints

- Kontrakt i wartości normatywne są w Spec; obowiązują wszystkie zadania.
- `compare.requireContext=true` domyślnie; nie uzależniać go od `journal.enabled`.
- MCP compare off (`compare.requireContext=false`): brak properties `subject` i tekstowego `scope`, bez akceptowania ukrytych pól; w trybie request-scoped obiekt `target` pozostaje wymagany. CLI off: brak obu pól kontekstu lub poprawna pełna para.
- Breaking change MCP: w **każdym** narzędziu zależnym od bazy w trybie request-scoped publiczny obiekt `scope` (`profile`/`vars`) zmienia nazwę na `target`. Starej nazwy nie przyjmować jako aliasu. W trybie fixed nie ma argumentu `target`. W `compare` nazwa `scope` jest zarezerwowana dla tekstowego zakresu optymalizacji; inne narzędzia nie przyjmują tego pola. Nie zmieniać nazw opcji CLI służących do wyboru bazy.
- Granica wersji: `(sessionId, subject, scope)`, hash obu stron, pełna zachowana historia. Warunki pomiaru należą do próby, nie do wersji SQL.
- Tożsamość sesji nie jest zmieniana w 062: aktualny klucz procesu MCP / bieżące reguły CLI wyznaczają `sessionId`. Nie dodawać nowej identyfikacji rozmów agenta ani automatycznego scalania sesji.
- Nie traktować różnych baz, vars, setupów, parametrów lub cache jako jednego porównywalnego szeregu pomiarowego. UI nie rysuje wspólnego trendu ani rankingu wersji na takich danych.
- Brak live DB i dostępu do dashboardu operatora. Weryfikacja izolowana.
- Done wymaga obu bramek, uruchomionych kolejno, oraz aktualizacji README, skills/sqlharness/SKILL.md, Capabilities.cs i docs/mcp.md.

## Review Focus

1. Dwa serwery MCP z różną konfiguracją w jednym procesie testowym: schema i walidacja nie mogą przeciekać między instancjami (T2).
2. Ponowienie identycznego SQL po obu stronach i brak hasha: stabilna wersja lub jawne unknown, bez sztucznej nowej wersji (T4).
3. Ponad 500 operacji i retencja: jawne stronicowanie, kompletność i brak numeracji wyliczanej z pojedynczej strony (T4/T5).
4. Matrix: jedna regresja lub różne wyniki wśród popraw nie giną w zielonym agregacie (T3/T6).
5. Najnowszy whole zakończony błędem oraz starszy zielony compare: nagłówek pokazuje błąd i właściwą parę, bez deklaracji sukcesu (T5).
6. W request-scoped MCP argument `target` (obiekt połączenia) nie koliduje z `scope` (tekst zakresu SQL w compare); wszystkie narzędzia DB odrzucają stary obiekt `scope` (T2).

## Mapa plików

Istniejące punkty wejścia: `SqlHarnessConfig.cs`, `SqlHarnessConfigWriter.cs`, `Contracts.cs`, `Capabilities.cs`; CLI `Commands/SqlHarnessCommands.cs`; MCP `Tools/McpToolCatalog.cs`, `Tools/McpRequestToolHandlers.cs`, `McpOperationMapper.cs` i host rejestrujący narzędzia; journal `JournalModels.cs`, `JournalSchema.cs`, `ActivityJournal.cs`, `OperationJournalDescriber.cs`, `JournalSummary.cs`, `JournalBenchmarkBuilder.cs`; dashboard `DashboardModels.cs`, `JournalReader.cs`, `DashboardServer.cs`; UI `api/types.ts`, `api/queries.ts`, `pages/SessionPage.tsx`, `pages/OperationPage.tsx`, `pages/SettingsPage.tsx`, `lib/compare.ts`, `components/VariantPanel.tsx`.

Nowe małe moduły: `Core/CompareContext.cs`, `Dashboard/CompareHistoryBuilder.cs`, UI `components/CompareSessionOverview.tsx`, `components/CompareDetail.tsx`, `components/SqlDiff.tsx`. Nie refaktoryzować niezwiązanych ścieżek query/measure.

### T1: Globalna polityka i kontekst Core/CLI

**Files:** mapa Core/config/CLI powyżej; testy `Journal/SqlHarnessConfigTests.cs`, nowe `CompareContextTests.cs` i `Cli/CompareContextCommandTests.cs`.

**Interfaces:** `CompareConfig.RequireContext` (default true), `SqlHarnessConfig.Compare`; `CompareContext(string Subject, string Scope)`; nullable `Context` jako init property na obu operacjach compare. Wspólny `CompareContext.Parse(string? subject, string? scope, bool required)` zwraca poprawną parę lub null, a błędne wejście odrzuca istniejącym mechanizmem błędów walidacji.

- [x] RED: missing config → true; round trip true/false; zły typ i nieznane pole są odrzucane; pola puste, z whitespace, slash, uppercase i 65 znakami odrzucone; 64 znaki zaakceptowane; off dopuszcza zero pól, lecz odrzuca jedno. `whole` zachowane dokładnie.
- [x] Uruchom `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~SqlHarnessConfigTests|FullyQualifiedName~CompareContext"`; potwierdź oczekiwany RED nowych testów.
- [x] Dodaj opcje CLI i walidację przed DB/file execution według istniejącej kolejności preflight; Core otrzymuje kontekst także dla matrix. Nie zmieniaj wyniku SQL ani samej oceny regresji.
- [x] GREEN tego samego filtra; fixture z licznikiem otwarć dowodzi zero połączeń przy brakującej parze. Uaktualnij istniejące fixture compare wymagające pary, bez globalnego wyłączania feature w testach.
- [x] Commit zadania po review.

### T2: Zamrożony, dynamiczny kontrakt MCP

**Files:** MCP katalog/handlers/mapper/host z mapy; testy `McpToolSchemaTests.cs`, `McpRequestScopeTests.cs`, `McpMappingTests.cs`, `McpStartupTests.cs`.

**Interfaces:** host przekazuje niemutowalny snapshot `RequireContext` do rejestracji katalogu i handlerów. W każdym narzędziu target-dependent request-scoped zmień publiczną nazwę obiektu wyboru celu `scope` na `target` (jego struktura `profile`/`vars` zostaje bez zmian). Mapper przypisuje `Context` z T1 dla `compare`; wewnętrzne typy `McpScope`/`McpRequestScopeArgument` mogą zachować nazwy. Schema i runtime używają tej samej polityki; nie stosować mutowanego globalnego cache narzędzi SDK.

- [x] RED: `compare` w fixed MCP: true → tekstowe `subject`/`scope` są properties i required, bez `target`; false → brak obu properties. `compare` w request-scoped MCP: zawsze wymagany obiekt `target`, a tekstowe `subject`/`scope` tylko przy true. Off + przesłane ukryte pola kontekstu → odrzucenie. On + brak/pusta/połowiczna para → błąd przed DB.
- [x] RED: wszystkie narzędzia zależne od celu w request-scoped przyjmują `target: {profile, vars}` i odrzucają dotychczasowy obiekt `scope` (bez aliasu), także gdy kontekst compare jest wyłączony. Dla `compare` przy true obiekt przesłany jako tekstowe `scope` jest błędnym typem. Dla innych narzędzi tekstowe `scope` jest nieznanym argumentem. Zweryfikuj preflight bez DB, allowlist unknown-arguments oraz `tools/list`/`tools/call` dla fixed i request-scoped.
- [x] RED: dwie izolowane instancje o przeciwnych ustawieniach; zmiana pliku config nie zmienia działającego kontraktu, nowy host widzi zmianę.
- [x] Uruchom `dotnet test tests/SqlHarness.Mcp.Tests --filter "FullyQualifiedName~McpToolSchemaTests|FullyQualifiedName~McpRequestScopeTests|FullyQualifiedName~McpMappingTests|FullyQualifiedName~McpStartupTests"`.
- [x] Implementuj przy użyciu aktualnych możliwości rejestracji SDK, z osobnym schematem instancji. Przejrzyj oba katalogi/handlery MCP i każdą ścieżkę parsera/mappera targetu — nie zmieniaj wyłącznie `compare`. Zostaw spójne allowlist unknown-arguments i budżety. Nie wprowadzaj restartu serwera przez agenta.
- [x] GREEN filtra, test rzeczywistego tools/list i tools/call przez izolowany proces stdio bez DB. Zmierz różnicę rozmiaru schematu on/off; nie pinuj liczby tokenów do konkretnego modelu.
- [x] Commit po review.

### T3: Trwały zapis kontekstu i dowodów próby

**Files:** journal z mapy, raporty Core jeśli wymagane do projekcji; testy `Journal/ActivityJournalTests.cs`, `Journal/ActivityJournalBenchmarkTests.cs` i testy summary/describer.

**Interfaces:** `OperationStart.Context`; nullable kolumny `compare_subject`, `compare_scope`. Projekcja porównania utrwala istniejący werdykt regression, tryb/sprawdzenie equivalence per case oraz dostępne warunki pomiaru. Nie zapisuje nowego duplikatu całego raportu bez budżetu.

- [x] RED: migracja istniejącej DB nie traci rekordów, stare pary null; plain/matrix przenoszą parę; rejections/failures zachowują znany kontekst; wyłączony journal nadal nie ingeruje w wykonanie.
- [x] RED: hash-only przechowuje etykiety, hashe i bezpieczne metryki, lecz nie setup SQL, wartości parametrów ani plany. storeSensitive on przechowuje dostępne warunki w dotychczasowych budżetach. Summary zachowuje regresję jednej komórki i niepełną zgodność.
- [x] Uruchom `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Journal"`; potwierdź RED, wdroż migrację i projekcję, potwierdź GREEN.
- [x] Opisz brakujące historyczne dane jawnie; nie otwieraj plików artefaktów wskazanych dowolną ścieżką z DB. Commit po review.

### T4: Read model historii i kontrakt dashboardu

**Files:** nowe `CompareHistoryBuilder.cs`; `JournalReader.cs`, `DashboardModels.cs`, `DashboardServer.cs`; UI API types/queries; testy `Dashboard/JournalReaderTests.cs`, `Dashboard/DashboardContractTests.cs`, nowe `Dashboard/CompareHistoryBuilderTests.cs`.

**Interfaces:** nowy endpoint `GET /api/sessions/{id}/compares?cursor=...` z `Page<CompareAttemptSummary>`; pola każdej próby: operationId, subject/scope, baseline/candidate hash i version (nullable), status, startedAt, summary per case. Osobna agregacja subjects/scopes dla całej zachowanej sesji w SessionDetail; nie wyliczać nagłówków ze strony UI. OperationDetail otrzymuje kontekst, wersje i projekcję dowodów T3. Typy C#/TS przypięte testem kontraktu.

- [x] RED: v0→v1, v1→v2, v0→v2; baseline=candidate; odrębne scope/subject/sesje; null hash; legacy bez kontekstu; kolejność rozpoczęcie/ID i baseline przed candidate.
- [x] RED: 501+ operacji, cursor bez duplikatów/luk i numeracja niezależna od strony; retencja nie daje twierdzenia o pierwotnym początku. Brak N+1 pobierania szczegółów z UI; backend używa ograniczonych projekcji i indeksów.
- [x] RED: najnowszy whole failed, wcześniejszy succeeded; running nie zastępuje zakończonego; brak whole; tylko running whole; v2→v3 nie daje procentu od v0; bezpośrednie v0→v3 daje wynik tylko swoich warunków. Inna baza/parametry nie tworzą trendu.
- [x] Uruchom `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~CompareHistoryBuilderTests|FullyQualifiedName~JournalReaderTests|FullyQualifiedName~DashboardContractTests"`; RED → implementacja → GREEN. Commit po review.

### T5: Sesja i ustawienie kontekstu

**Files:** `SessionPage.tsx`, nowe `CompareSessionOverview.tsx`, `SettingsPage.tsx`, API T4; testy SessionPage/SettingsPage oraz nowy CompareSessionOverview.test.tsx.

**Interfaces:** komponent konsumuje agregację całej sesji i paginowane próby T4; link do operation zachowuje subject/scope przez identyfikator próby, a powrót odtwarza grupę. Ustawienie zapisuje `compare.requireContext` przez istniejący mechanizm config writer.

- [x] RED: dwa subjects, wielokrotne scope, chronologia, rozwijanie grup, link do compare i powrót; Wszystkie operacje nadal dostępne; metadane zwijane; brak kontekstu jest oddzielną grupą.
- [x] RED: failed whole nie pokazuje starej poprawy; tylko fragmenty pokazują brak końcowego compare; loading/error/empty/running/abandoned oraz doładowanie ponad 500 prób nie gubią kontekstu.
- [x] RED: toggle domyślnie on, save off zachowuje inne ustawienia; tekst wyjaśnia wymaganie CLI i restart MCP; nie wykonuje automatycznego restartu.
- [x] W `src/SqlHarness.Dashboard/ui` uruchom `npm run test -- src/pages/SessionPage.test.tsx src/pages/SettingsPage.test.tsx src/components/CompareSessionOverview.test.tsx`; RED → implementacja → GREEN, potem `npm run typecheck`. Commit po review.

### T6: Szczegół compare, diff SQL i obie strony pomiaru

**Files:** OperationPage, VariantPanel i lib/compare; nowe CompareDetail i SqlDiff; testy OperationPage, lib/compare i nowych komponentów.

**Interfaces:** `CompareDetail` konsumuje OperationDetail T4; pozostałe typy operacji zachowują obecny ekran. Porównanie przypadków używa par z istniejącego `comparePairs`, adaptowanych do aktualnych nazw z 061 jeśli wdrożony.

- [x] RED: niezależne status wykonania, regression i equivalence; brak werdyktu nie staje się poprawą; equivalence off, false, partial i legacy null mają odrębne etykiety.
- [x] RED: matrix z jedną regresją/różnymi wynikami; null i zero baseline bez procentu; SQL Server/Postgres bez wymyślonych metryk; runs i min–max widoczne.
- [x] RED: diff oraz pełne SQL; brak storeSensitive; plany/IO obu stron; niedostępne historyczne warunki; duży SQL nie blokuje renderowania. Diff lokalny, deterministyczny, z limitem 256 KiB łącznie i 4000 linii; powyżej komunikat i istniejący bounded podgląd obu tekstów. Bez nowego pakietu do diff, jeśli istniejące zależności/pomocniczy line diff wystarczą.
- [x] Uruchom `npm run test -- src/pages/OperationPage.test.tsx src/lib/compare.test.ts src/components/CompareDetail.test.tsx src/components/SqlDiff.test.tsx`; RED → implementacja → GREEN. Potem `npm run check` i `npm run build`.
- [x] Zweryfikuj responsywność i klawiaturę na izolowanej stronie fixture: desktop oraz szerokość 390 px, taby, grupy, tabele, diff, stany braku danych. Bez operatorowego dashboardu/API i bez publikacji fixture z danymi użytkownika. Commit po review.

### T7: Dokumentacja, integracja i końcowe bramki

**Files:** README.md, docs/mcp.md, skills/sqlharness/SKILL.md, Capabilities.cs, plans/README.md; tests capabilities/schema dopasowane do nowego kontraktu.

- [x] Zaktualizuj wszystkie przykłady CLI/MCP compare o `subject`/`scope`; w **każdym** przykładzie request-scoped MCP i opisie schematu zmień obiekt wyboru bazy `scope` na `target`. Opisz breaking change bez aliasu, globalne ustawienie, ukrywanie tekstowych pól kontekstu off (lecz nie `target`), restart MCP, `whole`, stabilne nazwy, wersje po hashach i ograniczenia retained history. Nie dodawaj kontekstu SQL do wywołań innych narzędzi.
- [x] Capabilities raportuje aktywną politykę kontekstu i semantykę wersji zgodnie z rzeczywistym kontraktem, bez sekretów i nowych instrukcji zbędnych w trybie off.
- [x] Przejrzyj cały diff względem Spec: cały plan, bez mechanizmu wyboru wersji, bez procentów składanych, bez zmian safety. `git diff --check` musi zwrócić 0. Review T7 i całej gałęzi zakończone bez Critical/Important.
- [x] Uruchom foreground `pwsh ./scripts/verify.ps1`, a po zakończeniu `pwsh ./scripts/verify-linux.ps1` (WSL ext4; provision przez istniejący setup jeśli wymagany). Obie bramki zakończone kodem 0, natywne markery są zapisane wraz z `VERIFICATION PASS`, a pełne logi nie zawierają case-sensitive `FAILED`; zob. [dowody](062-verification-evidence.md).
- [x] Uaktualizuj wiersz 062 na faktyczny status i dowody. Commit po review. Integracja/main i push wyłącznie według osobnego polecenia użytkownika; post-merge dowody wymagają sprawdzenia zintegrowanego drzewa.

## Kryterium odbioru

Operator widzi prawidłową historię kilku SQL i fragmentów w jednej sesji, rozpoznaje powrót do poprzedniej wersji, otwiera compare z dowodami obu stron i nie otrzymuje fałszywej deklaracji końcowego sukcesu. Tryb off usuwa tokenowy koszt dwóch pól kontekstu compare z kontraktu MCP, pozostawiając request-scoped `target`. Wszystkie narzędzia DB w request-scoped używają już `target` zamiast starego obiektu `scope`; obie bramki są zielone, dokumentacja odpowiada zachowaniu.
