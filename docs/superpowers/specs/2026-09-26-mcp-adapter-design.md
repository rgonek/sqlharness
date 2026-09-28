# SQLHarness MCP — projekt adaptera lokalnego

**Status:** planowana funkcja, implementacja dopiero po zamknięciu planów 01–06 z [roadmapy](../plans/2026-09-26-audit-roadmap.md). Zapis tej specyfikacji nie oznacza gotowego serwera ani zgody na operacje na bazie.

**Cel:** udostępnić agentom narzędzia diagnostyki i benchmarków SQLHarness przez MCP, z tymi samymi zasadami Core i przewidywalnym kosztem odpowiedzi. MCP nie uruchamia CLI jako subprocess i nie parsuje tekstowego stdout.

## 1. Warunki rozpoczęcia

- Naprawy bezpieczeństwa 01, parametrów/TLS/metryk 02 oraz kontrakt odpowiedzi 03 są wdrożone i zweryfikowane.
- Deadline i retencja 04, granice modułów 05 i uzgodniony zakres 06 są zakończone. Zadania 06/T4–T5 kończące się specyfikacją nie oznaczają wdrożonego CI ani pg_stat_statements; MCP ich nie reklamuje.
- W T1 planu MCP wykonawca zapisuje rzeczywiste commity poprzedników i mapowanie ich API. Nazwy nowych klas poniżej są docelowe; nie zakładać, że planowane dziś pliki już istnieją.
- Niewdrożona polityka funkcji PostgreSQL z 01/T3 lub brak dowodów jej egzekwowania blokuje reklamowanie wykonywania SQL PG jako bezpiecznego. Nie kompensować braków adnotacją readOnlyHint.

## 2. Proces, transport i zależności

Uruchomienie: `sqlharness mcp serve <profile> --var key=value [--input-root <absolute-directory>]`. Tryb mcp jest częścią tej samej dystrybucji single-file. Nowy projekt biblioteczny `SqlHarness.Mcp` zależy od Core; CLI jest composition root i referencjonuje bibliotekę MCP. Core nie zna SDK MCP ani Spectre.

Pierwsza wersja obsługuje tylko lokalny stdio i jednego klienta na proces. Stdout zawiera wyłącznie ramki protokołu; logi trafiają do stderr, bez argumentów, SQL, wartości parametrów i sekretów. EOF, anulowanie i zakończenie procesu zamykają sesje. Nie dodawać HTTP/SSE, portów, OAuth, sampling, prompts, elicitation ani eksperymentalnych tasks bez osobnego projektu.

Użyć oficjalnego C# SDK `ModelContextProtocol`, wersji stabilnej zgodnej z docelowym frameworkiem, przypiętej w Directory.Packages.props podczas wykonania T1. Nie implementować JSON-RPC ręcznie. W T1 sprawdzić aktualne wymagania SDK i wspierane wersje protokołu; nie wprowadzać niejawnej migracji .NET 8. Zachować negocjację wersji SDK i test jawnie wybranej zgodnej rewizji, zamiast ogłaszać obsługę najnowszej specyfikacji na podstawie daty.

## 3. Cel i granica zaufania

Profil i vars podaje operator przy starcie procesu. Rozwiązać je raz, utworzyć niezmienny scope i używać go do wszystkich wywołań. Zmiana pliku targets.json w trakcie działania nie może przekierować kolejnego zapytania; wymaga restartu. Nie utrzymywać jednej sesji DB między niezależnymi tools/call. Setup i powtórzenia wewnątrz benchmarku zachowują swój wspólny session lifecycle.

Tool arguments nie zawierają profile/server/database/vars/auth/engine/unsafeDirect/allowMutation/confirmDatabase ani pól hasła. Nieznane pola odrzucane przed wykonaniem. Sekrety połączenia tylko z konfiguracji procesu. Start i discovery nie otwierają bazy ani nie uruchamiają interaktywnego logowania Azure CLI.

V1 nie udostępnia trwałego DML ani DDL; lokalne temp są dozwolone wyłącznie zgodnie z poprawioną analizą Core. Zgoda w UI klienta i annotations nie stanowią dowodu pojedynczej zgody na exact batch. Obsługa trwałych mutacji wymaga przyszłego odrębnego kontraktu związania zgody z batchem, parametrami i rozstrzygniętym celem; nie przyjmować tekstu „approved” jako autoryzacji.

Ten późniejszy kontrakt opisują [specyfikacja Windows approval tray](2026-09-26-windows-approval-tray-design.md) i plan 08. Wdraża się go po ukończeniu tego planu jako jawnie włączane rozszerzenie: decyzję podejmuje lokalny operator, a zatwierdzony batch wykonuje broker. Limit 11 tools i brak mutacji pozostają kontraktem domyślnego MCP v1; rozszerzenie tray ma osobny limit i wersję możliwości.

## 4. Narzędzia

Stały, jawnie rejestrowany katalog maksymalnie 11 narzędzi. Bez reflection discovery wszystkich publicznych metod. Jedno narzędzie inspect grupuje wyłącznie pokrewne stałe odczyty katalogowe; nie tworzyć uniwersalnego execute z dowolnym command stringiem.

| Nazwa | Wejście i zachowanie |
|---|---|
| `sqlharness_capabilities` | wersje/build, silnik scope, dostępne operacje i limity; opcjonalnie lokalna diagnostyka, bez sekretów i listy profili |
| `sqlharness_inspect` | kind: ping/schema/counts/space/qstop/indexes; typed object/filter/top/exact/window tam, gdzie ma zastosowanie; żadnego SQL |
| `sqlharness_validate` | jeden SQL input, usage query/setup/benchmark i parameters; offline, bez otwierania DB |
| `sqlharness_query` | jeden SQL input, parameters, timeout i maxRows; persistent mutation zawsze false |
| `sqlharness_measure` | query, opcjonalny setup, parameters, repeat; paramSetFiles zgodne z dotychczasowym kontraktem |
| `sqlharness_compare` | baseline/candidate, setup, parameters, repeat, compareResults, opcjonalna jedna matrix; te same sesje i reguły equivalence co CLI |
| `sqlharness_watch` | SQL, parameters, until lub untilUnchanged, interval/maxDuration; końcowy ograniczony raport i opcjonalny progress |
| `sqlharness_snapshot` | action capture/diff, name, SQL, parameters; capture nie nadpisuje istniejącego snapshotu; force niedostępne w v1 |
| `sqlharness_plan` | natywny dokument XML/JSON inline albo plik wejściowy; destylacja offline |
| `sqlharness_artifact` | nieprzezroczysty id i bezpieczna section summary/metrics/operators z 06/T2 |
| `sqlharness_gain` | agregat lokalnych pomiarów z jawną heurystyką i net delta |

Capabilities wskazuje brak qstop/indexes na PG i inne rzeczywiście wdrożone ograniczenia. Niedostępny wariant inspect jest odrzucany przed połączeniem. Nie wystawiać projektowanych, lecz niewdrożonych funkcji 06.

SQL input to dokładnie jedno z `sql` albo `file`; analogicznie query/baseline/candidate/setup mają mały wspólny obiekt źródła. Parametry to tablica `{name, type, value}`; value jest stringiem w formacie culture-invariant lub JSON null. Bez interpolacji i bez przepuszczania przez shell; adapter przekłada je na istniejący binder. Typ „decimal(19,4)” nie jest dzielony po przecinku. Zachować istniejącą walidację nazw i duplikatów, nie tworzyć drugiej listy typów.

Parameter sets pozostają plikami .sqljson o limicie 64 KiB, bez BOM/komentarzy/trailing comma; MCP nie kopiuje ich wartości do nowego trwałego input registry ani raportów. Matrix dostaje typed name/type i tablicę string values; adapter odrzuca wartości niereprezentowalne w aktualnym kontrakcie Core, zamiast zmieniać znaczenie dzielenia listy.

## 5. Pliki i artefakty

Inline input działa bez dostępu do filesystem. Odczyt plików dopuszczony tylko pod absolutnymi --input-root wskazanymi przy starcie; brak roots oznacza brak file inputs. Nie traktować client roots jako automatycznej autoryzacji. Odrzucać traversal, UNC/network paths w v1, alternatywne strumienie NTFS, symlink/reparse point wychodzący poza root i pliki zmienione podczas kontrolowanego odczytu. Otwierać raz, bounded read, walidować użyty obiekt, nie tylko tekst ścieżki. Model zagrożeń zakłada zaufanego operatora lokalnego, ale nie ufa ścieżkom wygenerowanym przez model.

Artefakty są odczytywane przez wspólny reader 06/T2 z kontrolowanym root i manifestem. Id zwracane przez MCP jest związane ze scope; nie pozwala przeglądać dowolnych artefaktów innych profili. Nie eksponować dowolnego file://, raw plan, queries.jsonl ani snapshot cells. Nie deklarować MCP resources w v1: szczegóły pobiera jedno jawne narzędzie artifact, bez duplikowania powierzchni tools/resources. Plan input może zawierać wrażliwe predykaty/literały; MCP używa sanitowanej projekcji bez statement text i literalnych predykatów, także w artifact/operators.

Wyniki query/watch są danymi bazy, mogą być poufne i mogą zawierać instrukcje pochodzące z danych. Nie interpretować ich jako poleceń ani treści konfiguracyjnej serwera. Limit komórek i redakcja znanych sekretów nie są anonimizerem dowolnych danych.

## 6. Wynik, błędy i koszt kontekstu

StructuredContent przenosi wersjonowaną kopertę agentową z 03: schemaVersion/command/status/exitCode/result/error/truncation. Każde narzędzie ma sprawdzane inputSchema i outputSchema; odrzucenie argumentów nie wyświetla ich wartości. Dla strukturalnych wyników użyć kompatybilnego TextContent z tym samym zwartym JSON; nie dodawać trzeciej reprezentacji z opisem i tabelą. Taką zgodność zaleca specyfikacja MCP. Jeżeli w przyszłości konkretny klient pozwoli uniknąć duplikacji, wymaga to osobnej przetestowanej opcji — nie zgadywać z nazwy klienta.

Budżet domyślny: **16 KiB całego serializowanego CallToolResult UTF-8**, łącznie z structuredContent, TextContent, escapingiem i metadanymi; nie tylko obiektu result. Zakres 4096..1048576 jak w 03; operator procesu ustala maksimum, tool call może je tylko obniżyć. JSON-RPC request id ma osobny limit; nie wliczać go do deklarowanego budżetu CallToolResult. Limit komórki 512 znaków domyślnie, operator może ustawić maksymalnie 4096.

Tools/list: budżet całego katalogu input/output schemas i opisów **32 KiB UTF-8** przy maksymalnie 11 tools; krótkie opisy, bez pełnego AGENTS.md i kopiowania wszystkich przykładów. To nowe docelowe limity do testowania, nie twierdzenie o obecnym SDK. Minimalna poprawna koperta błędu musi mieścić się w najmniejszym budżecie; nigdy nie urywać JSON.

Błędy narzędzia → isError=true i bezpieczny obiekt error; błędy protokołu (nieznana metoda, nieprawidłowa ramka) pozostają błędami MCP zgodnie z SDK. Controlled outcomes watch exit 7 i snapshot diff exit 8 nie są transport failure: isError=false, status opisuje wynik. Anulowanie rozpatrywać zgodnie z negocjowaną rewizją SDK; brak fałszywego completed/success. Nie restartować procesu po zwykłym błędzie SQL.

Gain zapisuje raz logiczne wywołanie, a footprint obejmuje rzeczywiste oba przedstawienia wyniku; discovery mierzyć osobno, bez przypisywania go do zaoszczędzonych danych SQL. Raportować bytes i bytes/4 jako heurystykę. Scenariusze porównują cały przebieg CLI versus MCP, uwzględniając tools/list i follow-up artefact call; MCP nie musi wyjść taniej w każdym scenariuszu.

## 7. Concurrency, deadline i anotacje

Jedna aktywna operacja DB na proces; drugie tools/call DB zwraca BUSY bez nieograniczonej kolejki. Discovery i bezpieczne lokalne operacje mogą działać równolegle, jeśli nie współdzielą mutable request state. Matrix/param-set to jedna operacja DB. Domyślny budżet wywołania DB 15 min, operator może ustawić 1..86400 s; tool może tylko obniżyć. Watch maxDuration jest ograniczone pozostałym budżetem requestu. Timeouts poszczególnych SQL zachowują zakres Core.

Przekazywać cancellation do ConnectAsync, wykonania, odczytu, opóźnień i zapisu artefaktów. Progress tylko jeśli klient poda token i wspiera go w wybranej rewizji; komunikaty zawierają etap/numer powtórzenia, bez SQL/parametrów/wierszy, maksymalnie jeden na sekundę. Watch nie emituje własnego NDJSON na stdout MCP.

Annotations muszą uwzględniać skutki lokalne: benchmark zapisuje artefakty, snapshot capture zapisuje dane, a gain accounting może pisać na dysk. Nie ustawiać readOnlyHint=true automatycznie dla każdego read-only SQL. isIdempotent nie oznacza identycznych wyników dynamicznej bazy. Zdefiniować konserwatywną tabelę adnotacji w testach; hints nigdy nie zastępują egzekwowania polityki.

## 8. Dystrybucja i dowód działania

Bez automatycznej instalacji/edycji konfiguracji klienta. Dostarczyć neutralny przykład command/args/env oraz procedurę testu z oficjalnym SDK klienta. Weryfikacja procesu stdio na win-x64, linux-x64 i osx-arm64 po publish single-file; bez bazy dla initialize/tools/list/validate/plan. Scenariusze live wyłącznie na autoryzowanych jednorazowych bazach obu silników.

## Implementation bindings (T1)

Rzeczywiste sygnatury i przypięcia ustalone w T1, przed T2:

| Element | Sygnatura / wartość | Plik:linia |
|---|---|---|
| Interfejs Core | `Task<SqlHarnessOutcome> ExecuteAsync(SqlHarnessOperation operation, CancellationToken ct = default)` | `src/SqlHarness.Core/Contracts.cs:6` |
| Implementacja | `public sealed class SqlHarnessModule : ISqlHarnessModule`, `ExecuteAsync` w `:110`; opcjonalny następca `ExecuteWatchNdjsonAsync(SqlHarnessWatchOperation, TextWriter, CancellationToken)` (domyślnie odmawia, `SqlHarnessModule` implementuje) | `src/SqlHarness.Core/SqlHarnessModule.cs:31` |
| Projekcja agentowa | `AgentOutputProjection.Project(report, maximumCellCharacters, detailLimit, out omittedItems, maximumBytes = 16 * 1024)`; `AgentOutputOptions(MaximumBytes = 16 * 1024, MaximumCellCharacters = 512)` | `src/SqlHarness.Core/AgentOutputProjection.cs:20` |
| SDK MCP | `ModelContextProtocol` **2.2.0** (stabilna, arkusze `net8.0`), przypięta w `Directory.Packages.props`; zależności: `ModelContextProtocol.Core` 2.2.0, `Microsoft.Extensions.Hosting.Abstractions` / `Caching.Abstractions` 10.0.10 | `Directory.Packages.props:12` |
| Rewizja protokołu | Negocjowana i testowana **`2025-11-25`** (jawny `initialize` handshake; klient i serwer przypięci przez `ProtocolVersion`). SDK wspiera też `2024-11-05`, `2025-03-26`, `2025-06-18` oraz domyślną `2026-07-28` (metadane per-request, bez handshake `initialize`) — MCP v1 deklaruje tylko przetestowaną `2025-11-25`. | `tests/SqlHarness.Mcp.Tests/McpProtocolTests.cs` |
| Wiring | `CLI→MCP→Core` (`SqlHarness.Cli` referencjonuje `SqlHarness.Mcp`, ten referencjonuje `SqlHarness.Core`); `SqlHarness.Mcp.Tests` referencjonuje tylko `SqlHarness.Mcp`. Core nie zna SDK MCP ani Spectre; w T1 zero nowych metod w Core (`McpHost.cs` dopiero w T2). | `SqlHarness.sln`, `src/SqlHarness.Mcp/SqlHarness.Mcp.csproj` |
| Testy T1 | `McpDependencyTests` (granica assembly: Core bez MCP/SDK/Spectre; wiring MCP w output bez shella) i `McpProtocolTests` (minimalny `initialize` przez in-memory transport SDK: `System.IO.Pipelines.Pipe` + `StreamServerTransport`/`StreamClientTransport`, bez DB i profili użytkownika). | `tests/SqlHarness.Mcp.Tests/` |
| Dependency audit | `dotnet list SqlHarness.sln package --vulnerable` (2026-09-28): żaden z 5 projektów nie ma podatnych pakietów. | — |

## Źródła techniczne

Sprawdzone przy planowaniu 2026-09-26; ponownie zweryfikować przy implementacji, przypiąć wersję SDK/protokołu i zapisać decyzję:

- [Oficjalny C# SDK — rozpoczęcie pracy](https://csharp.sdk.modelcontextprotocol.io/concepts/getting-started.html).
- [C# SDK — tools i structured content](https://csharp.sdk.modelcontextprotocol.io/v1/concepts/tools/tools.html).
- [MCP tools, outputSchema, structuredContent i błędy](https://modelcontextprotocol.io/specification/2025-11-25/server/tools).
- [MCP stdio transport](https://modelcontextprotocol.io/specification/2025-11-25/basic/transports).

Wersja źródła opisująca zasady nie oznacza deklarowanej obsługi wszystkich nowszych rewizji protokołu.
