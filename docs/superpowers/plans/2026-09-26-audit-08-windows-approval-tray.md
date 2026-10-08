# Windows approval tray Implementation Plan

**Status (2026-10-08):** TODO — brak implementacji. Wymaga decyzji produktowej: łamie obecny kontrakt MCP v1 („exactly 11 tools”, „no persistent mutations”).

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Dodać do naszego SQLHarness MCP jednorazowe zatwierdzanie trwałego DML w lokalnej aplikacji Windows z ikoną tray i powiadomieniami.

**Architecture:** Tray jest lokalnym brokerem decyzji i wykonania nad Core. MCP może zgłosić, sprawdzić i anulować żądanie, ale nie może zatwierdzić go przez API. Neutralna biblioteka reguł pozwoli później zastąpić Windows UI/IPC na Unix.

**Tech Stack:** .NET zgodny z ukończonym planem 07, C#, WinForms NotifyIcon, Windows App SDK notifications, System.IO.Pipes, System.Text.Json, xUnit; Windows 11 x64 jako pierwszy cel.

**Spec:** [Windows approval tray design](../specs/2026-09-26-windows-approval-tray-design.md), [roadmapa](2026-09-26-audit-roadmap.md).

## Global Constraints

- Po kompletnym 07 i jego poprzednikach. Tylko nasz MCP, bez globalnego proxy.
- Dokładny niezmienny SQL + parametry + cel + polityka; każda zmiana wymaga nowej jednorazowej zgody.
- Brak odpowiedzi/expiry/restart oznacza brak nowego wykonania. Brak ponowień mutacji, także po niepewnym wyniku.
- Persistent DDL, dynamic SQL i inne zabronione konstrukcje pozostają zabronione mimo zatwierdzenia.
- Windows UI zatwierdza lokalnie; IPC i MCP nie mają metody Approve. Nie obiecywać izolacji przed dowolnym procesem pod tym samym kontem Windows.
- SQL/parametry tylko w pamięci i lokalnym podglądzie; journal i powiadomienia bez ich treści.
- Domyślny MCP pozostaje bez trwałych mutacji. Tryb --approval-mode tray włączany przy starcie przez operatora.

## Review Focus

- TOCTOU: zmiana pliku SQL/profilu/parametrów między podglądem a wykonaniem: T1–T3.
- Replay, podwójny klik, restart i awaria między SQL a zapisem rezultatu: T2–T3.
- Spoofing IPC, requestId obcego scope i próba approved=true: T4–T6.
- Sleep/lock/DND, schowane powiadomienie i bardzo długi batch: T5.
- Błąd/timeout po częściowym zapisie, cancel Executing i kolizja benchmarku: T3/T6–T7.

## File structure

| Plik/projekt planowany | Odpowiedzialność |
|---|---|
| `src/SqlHarness.Approvals/SqlHarness.Approvals.csproj` | biblioteka neutralna dla UI i platformy |
| `ApprovalRequest.cs`, `ApprovalStateMachine.cs`, `ApprovalDigest.cs` | niezmienne żądania, stany i związanie zgody |
| `ApprovalJournal.cs`, `ApprovalBroker.cs`, `ApprovedMutationExecutor.cs` | trwałe consume, decyzja i jedno wykonanie Core |
| `ApprovalProtocol.cs`, `ApprovalClient.cs` | typowane Submit/Status/Cancel bez Approve |
| `src/SqlHarness.Approval.Windows/SqlHarness.Approval.Windows.csproj` | Windows executable, osobna paczka |
| `Program.cs`, `TrayApplicationContext.cs`, `ApprovalQueueForm.cs`, `ApprovalDetailForm.cs` | single instance, tray i UI |
| `WindowsNotifications.cs`, `WindowsApprovalPipe.cs`, `WindowsSessionState.cs` | toast, IPC security, lock/sleep |
| `src/SqlHarness.Mcp/Tools/MutationTool.cs` | jedno opcjonalne narzędzie i integracja scope/gate |
| `tests/SqlHarness.Approvals.Tests/` | neutralne testy reguł i brokera |
| `tests/SqlHarness.Approval.Windows.Tests/` | Windows IPC/activation/UI |
| `docs/approval-tray.md` | instalacja, konfiguracja i granice ochrony |

Nowe nazwy są docelowe. W T1 uzgodnić faktyczne interfejsy ukończonego Core/MCP i nie tworzyć drugiego parsera/bindera. Projekty Windows odseparować od cross-platform build/test.

## T1 — kontrakt niezmiennego żądania i model zagrożeń

**Files:** ApprovalRequest.cs, ApprovalDigest.cs, ApprovalProtocol.cs; spec; nowe ApprovalRequestTests.cs, ApprovalDigestTests.cs.

**Interfaces:** kontrakt Submit(Proposal)→ApprovalStatus, GetStatus(requestId, scope)→ApprovalStatus, Cancel(requestId, scope)→ApprovalStatus. Proposal niesie requestKey i materializowaną treść z typowanymi parametrami, nie ścieżkę do dowolnego pliku. Decision nie należy do protokołu publicznego.

- [ ] Zapisać bindingi rzeczywistych API 07, reguł bezpieczeństwa 01 i target/TLS 02, wraz z commitami. Potwierdzić Windows 11 x64 i wybrany sposób paczkowania notifications.
- [ ] Dodać testy digestu: zmiana SQL, kolejności instrukcji, wartości/null/typu parametru, celu, timeout lub polityki zmienia digest; zmiana kolejności wejściowej nazw parametrów przy tej samej kanonicznej mapie nie zmienia znaczenia.
- [ ] Zamrozić żądanie i niezależnie rozstrzygnięty cel; callback UI nie może odczytać ponownie mutable DTO/file. Test zmiany sources po Submit.
- [ ] Test API nie zawiera approve/allowMutation/confirmDatabase; nieznane pola odrzucane. Zdefiniować enumerację stabilnych stanów i kodów zgodnie ze spec.
- [ ] Uruchomić `dotnet test tests/SqlHarness.Approvals.Tests/SqlHarness.Approvals.Tests.csproj`; lokalny commit `feat: define immutable approval requests`.

## T2 — maszyna stanów, journal i zużycie zgody

**Files:** ApprovalStateMachine.cs, ApprovalJournal.cs, ApprovalDigest.cs; ApprovalStateMachineTests.cs, ApprovalJournalTests.cs.

**Interfaces:** journal musi atomowo przeprowadzać pending/approved→consumed przed SQL i odtwarzać stan bez przywracania prawa wykonania. Lokalny UI wywołuje decyzję wewnątrz brokera; nie zwraca tokenu uprawniającego MCP do consume.

- [ ] Testy tablicy przejść: Pending→Denied/Expired/Cancelled; Approved→Executing tylko raz; konkurencyjne approve/cancel/expiry mają jednego zwycięzcę. Podwójny klik nie uruchamia drugiej próby.
- [ ] TTL 120 s, konfiguracja 30..600; start do 30 s po decyzji i przed expiry. Test fake clock oraz sleep/resume/clock rollback, bez polegania wyłącznie na zegarze ściennym do przedłużania ważności.
- [ ] Zapisać wersjonowany journal z atomowym publish/flush; test fail-before-write/fail-after-write/fail-on-flush. Brak trwałego consumed → brak ExecuteAsync.
- [ ] Restart: Pending/Approved→Invalidated, consumed bez wyniku→OutcomeUnknown. Test dedupe key+digest i conflict dla innej treści. Retencja 30 dni bez zapisywania SQL i wartości.
- [ ] Test wartości syntetycznych nie występujących w journal/log/error; HMAC key nie trafia do IPC/MCP. Uruchomić testy i lokalny commit `feat: persist single-use approval state`.

## T3 — broker wykonujący dokładnie zaakceptowany batch

**Files:** ApprovalBroker.cs, ApprovedMutationExecutor.cs; istniejące Core composition po 05; nowe ApprovedMutationExecutorTests.cs.

**Interfaces:** executor przyjmuje tylko wewnętrzny consumed request otrzymany z maszyny stanów, nie bool approved. Wywołanie Core ma AllowMutation=true/ConfirmDatabase wyliczone wyłącznie w brokerze. Bez publicznego endpointu „execute this approval”.

- [ ] Test brak DB execution dla Pending/Denied/Expired/Cancelled/Invalidated, niewłaściwego scope i zmienionego digestu.
- [ ] Test jednego ExecuteAsync po zgodzie, z tym samym SQL i wartościami, oraz kontroli actual database przed SQL. Persistent DDL/cross-database/nieobsługiwana funkcja pozostaje odmową nawet przy kliknięciu.
- [ ] Broker używa własnej konfiguracji połączenia do tego samego zatwierdzonego celu; mismatch mapowania MCP→broker kończy się odmową. Hasła/tokeny nigdy nie wracają do MCP/UI.
- [ ] Test częściowego wykonania batcha, błędu SQL, utraty odpowiedzi, cancel Executing i nieudanego zapisu wyniku. Odpowiedź nie może twierdzić „brak zmian” bez dowodu; brak retry, OutcomeUnknown gdy potrzeba.
- [ ] Test slotu scope: najwyżej jedna mutacja, brak otwartej transakcji/połączenia podczas oczekiwania. Nie wprowadzać niejawnego rollback wrappera.
- [ ] Uruchomić nowe testy oraz regresje safety/Core; lokalny commit `feat: execute approved mutations through the broker`.

## T4 — lokalne IPC i powiązanie procesów

**Files:** ApprovalProtocol.cs, ApprovalClient.cs, WindowsApprovalPipe.cs; WindowsApprovalPipeTests.cs i ApprovalProtocolTests.cs.

**Interfaces:** lokalny wersjonowany named pipe; metody Submit/Status/Cancel z bounded length framing, nie serializacja dowolnych typów .NET. Maksymalna materializowana propozycja 16 MiB łącznie; nadmiar odrzucić przed pełną alokacją. Limit połączeń, handshake deadline 5 s.

- [ ] Ustalić ACL, odmowę klientów sieciowych, user/session/peer PID i kontrolę ścieżki instalacji w obu kierunkach. Test pipe squatting i niewłaściwego procesu. Nie uznawać PID/CurrentUserOnly za kryptograficzny dowód decyzji człowieka.
- [ ] Test malformed frames, nieznana wersja, oversized length, rozłączanie w połowie, replay Submit i requestId innego scope. Nie logować niepoprawnego payloadu.
- [ ] Nie rejestrować Approve przez IPC; fuzz pól decision/approved/allowMutation ma skutkować odmową, nigdy zmianą stanu.
- [ ] Ustalić niewrażliwy journal scope mapping po reconnect; zerwane połączenie unieważnia oczekujące zgody, a executing otrzymuje best-effort cancellation z zachowaniem niepewnego wyniku.
- [ ] Uruchomić neutralne protocol tests oraz Windows IPC tests na Windows; lokalny commit.

## T5 — tray, kolejka, podgląd i decyzja operatora

**Files:** wszystkie pliki UI Windows z tabeli; nowe ApprovalViewModelTests.cs i WindowsApprovalUiTests.cs; docs/approval-tray.md szkic.

**Interfaces:** UI renderuje niezmienny ApprovalRequest z brokera; lokalna metoda decyzji sprawdza aktualny requestId/digest/state/expiry przy każdym kliknięciu. Toast otwiera szczegóły albo odrzuca, nigdy zatwierdza.

- [ ] Zbudować pojedynczą instancję aplikacji, ikonę tray, kolejkę do 20 pending/5 scope, historię i pause. Nie otwierać nowego okna dla każdego request; agregować powiadomienia bez gubienia wpisów.
- [ ] Okno pokazuje pełny SQL i typed parameters, rzeczywisty cel, cele OUTPUT, timeout/expiry i wieloinstrukcyjność. Wartości parametrów odkrywane tylko lokalnie; bez haseł, webview i wykonywania linków z SQL/uzasadnienia modelu.
- [ ] Przyciski „Zatwierdź jednorazowo”/„Odrzuć”; Enter nie zatwierdza, zamknięcie okna niczego nie akceptuje. Test stale view po expiry, podwójnego kliknięcia i usunięcia requestu.
- [ ] Powiadomienie generyczne bez danych; activation argument tylko nawiguje. Test DND/disabled notifications: kolejka nadal działa. Test lock/unlock oraz suspend/resume: brak decyzji w zablokowanej sesji, TTL bez przedłużania.
- [ ] Test dużego batcha, końcowego DELETE poza pierwszym ekranem i incomplete preview: UI nie może zatwierdzić podglądu obciętego przez błąd. Zapewnić klawiaturę, czytelność high DPI i focus bez domyślnej akceptacji.
- [ ] Ręczny odbiór Windows: screenshoty wyłącznie danych syntetycznych; uruchomić view-model/UI tests. Lokalny commit `feat: add Windows tray approval UI`.

## T6 — opcjonalne narzędzie mutacji naszego MCP

**Files:** `src/SqlHarness.Mcp/Tools/MutationTool.cs`, katalog/opcje/scope/gate z 07; nowe McpMutationTests.cs i McpApprovalScopeTests.cs w projekcie testowym MCP.

**Interfaces:** `sqlharness_mutation` action request/status/cancel zgodnie ze spec. Startup --approval-mode tray jawnie rozszerza katalog z 11 do najwyżej 12; budżety wyniku i listy nadal 16/32 KiB. Publiczny query pozostaje read-only.

- [ ] Test mode off: brak tool i każda próba trwałego DML nadal blokowana. Mode tray bez brokera: APPROVAL_UNAVAILABLE, bez fallbacku do Core mutation.
- [ ] Request zwraca pending szybko; model otrzymuje requestId/expiry, nie token autoryzacji. Status/cancel nie mogą przyjmować SQL/nowego celu/decision. Polling nie częściej niż 2 s; brak ponowień Submit z nowym key przez adapter.
- [ ] W trybie tray skoordynować gate MCP i brokera per scope. Najpierw uzyskanie slotu i trwały consume, dopiero wykonanie; kolizja read benchmark/mutation daje BUSY bez DB call. Pending nie blokuje zwykłych odczytów. Test utraty gate owner, reconnect i kilku instancji MCP tego samego scope.
- [ ] Test nowych stanów against outputSchema i isError: request pending oraz poprawne pobranie denied/unknown mają jawny stan; brak mylenia z sukcesem wykonania. Błąd wykonania opisany osobno od powodzenia odczytu statusu.
- [ ] Wynik przechodzi wspólną projekcję/redakcję i budżety; bez SQL/parametrów/credentials w status. Gain liczy wykonanie raz w brokerze, status polling osobno jako koszt, bez wielokrotnego sukcesu tej samej mutacji.
- [ ] Uruchomić testy MCP, schema/token budget i neutralne broker tests; lokalny commit `feat: gate MCP mutations on local Windows approval`.

## T7 — dowody end-to-end, paczka Windows i dokumentacja

**Files:** nowe WindowsApprovalEndToEndTests.cs, opt-in integration obu silników; Windows solution filter/job i release workflow; docs/approval-tray.md, README.md, AGENTS.md, spec 07 z oznaczeniem wersji rozszerzenia.

**Interfaces:** osobna paczka tray dla win-x64, istniejąca binarka MCP zachowuje cross-platform działanie w trybie off. Klient testowy używa rzeczywistego stdio i IPC, a SQL domyślnie fake executor.

- [ ] E2E: tools/call request → Pending → lokalna decyzja testowa w procesie UI → dokładnie jedna próba → status. Testowe sterowanie decyzją dostępne wyłącznie przez dependency injection w testach, nigdy endpoint w release.
- [ ] E2E odmowa/expiry/brak tray/restart/pipe spoof/zmiana pliku/anulowanie; offline próba wykazuje brak wywołania executora dla wszystkich odmów.
- [ ] Po autoryzacji jednorazowych baz SQL Server/PG sprawdzić rzeczywisty efekt DML, kontrolę celu, brak wykonania po odmowie oraz niepewny wynik po utracie odpowiedzi. Nie używać produkcyjnych profili; zapisać dowody syntetyczne.
- [ ] Build/test CLI i MCP na Linux/macOS bez Windows runtime; Windows release sprawdza tray, notifications activation, aktualizację i cleanup rejestracji. Nie instalować aplikacji ani autostartu użytkownikowi bez osobnego zlecenia.
- [ ] Dokumentacja: instalacja/start/pairing, one-shot, TTL, częściowe zmiany, unknown outcome, lokalna retencja i model zaufania. Brak deklaracji exactly-once lub ochrony wszystkich narzędzi agenta.
- [ ] Końcowy review całej ścieżki consent→consume→execute, wyniki testów i lista niewykonanych live checks. Lokalny commit; brak push.

## T8 — przygotowanie późniejszego portu Unix (bez implementacji UI)

**Files:** nowy `docs/superpowers/specs/2026-09-26-approval-unix-port-notes.md` tworzony po odbiorze Windows, gdy będą znane rzeczywiste granice modułów.

- [ ] Zapisać adaptery wymagające wymiany: notifications/tray, session lock, IPC peer identity, klucz HMAC i packaging; zachować neutralną maszynę stanów/journal/executor.
- [ ] Rozdzielić Linux desktop i macOS menu bar oraz headless → brak zgód i brak wykonania. Nie zakładać jednego API tray dla każdego środowiska.
- [ ] Wskazać testy neutralne przenoszone bez zmian oraz nowe testy platformowe. Oddzielny plan wdrożenia Unix po decyzji o pierwszej platformie; obecny zakres kończy się działającym Windows.

## Definition of done

Na Windows operator może zobaczyć, zatwierdzić raz albo odrzucić dokładną mutację naszego MCP. Brak decyzji, podmiana żądania, retry i awaria nie prowadzą do cichego wykonania. Niepewne efekty są jawne. Zwykły MCP bez włączonego rozszerzenia pozostaje bez trwałych mutacji. Dokumentacja bez implementacji i dowodów nie oznacza ukończenia tego planu.
