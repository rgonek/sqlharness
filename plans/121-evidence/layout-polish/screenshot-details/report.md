# Poprawki według 15 zrzutów i kółek A/B

## Stan

Implementacja i niezależne review zakończone na `bbda23d2`. Świeże 104 asercje przeglądarkowe przeszły. Pełny Windows gate PASS na `b3e921cd`: UI 436, Core 4774/11 skips, MCP 535/4 skips; exit 0, 15m26s. Końcowe dwa commity aktualizują wyłącznie testy. Spec: [wymagania operatora](../../../../docs/superpowers/specs/2026-10-28-dashboard-detail-corrections.md), [plan](../../../121-screenshot-detail-followup.md).

## Odpowiedź: czy Session odświeża operacje automatycznie?

**Tak.** AppLayout instaluje globalny `/api/live` EventSource. Zdarzenie `operation` unieważnia klucz danej sesji, którego prefiksem objęta jest też paginowana lista operacji. Nie wprowadzono pollingu ani nie zadeklarowano fikcyjnego interwału.

[Przeglądarka — pięć kontroli](session-live-results.json): po fikcyjnym zdarzeniu SSE liczba GET listy wzrosła z 1 do 2, nowa operacja pojawiła się, poprzednia pozostała i zaktualizowała status. Zachowany test Session sprawdza również załadowane strony, brak zgubionych wierszy i brak pomieszania cache szczegółów. To dowód działania mechanizmu na fixture, nie test prawdziwego agenta/DB.

## Dyspozycja uwag

| Zrzut | Poprawka / decyzja |
|---|---|
| 1 | Jedna linia opisu z pełnymi wartościami w title/detail, HH:mm:ss z pełną datą w title, większy Hugeicons star, spark zamiast badge keep, running jako Badge. Zachowane flagi, star PATCH, internal/paging/return context. |
| 2 | Opisy wersji inline obok v0/v1/v2, pełna treść w podpowiedzi. |
| 3 | Jedyna dodatkowa kolumna LOB jest toggle obok Scans; More columns pozostaje tylko dla wielu sensownych opcji whole-batch. |
| 4 | Przezroczysty przycisk disclosure nie tworzy osobnej powierzchni na pasku statementu, także po hover. |
| 5 | Zakładki Operation i wiersz parametrów mają oddzielne, poprawnie mierzone obszary bez nakładania. |
| 6 | Details Operation ma ten sam ghost Button/chevron i geometrię co Session. |
| 7 | Wspólny pas KPI, failed/rejected jako counts z dodatkowym procentem, prawdziwa liczba sesji; tylko oszacowania faktycznie zmierzonego outputu, bez wymyślonych kosztów/chat tokens. |
| 8 | Logical reads oraz osobny pełnopopulacyjny ranking SQL; jawna semantyka sumy zapisanych median i pokrycia. |
| 9 | Usunięta sekcja Detailed usage and workflow accounting wraz z jej treścią UI; podstawowe kontrakty accounting pozostają. |
| 10 | Preview ma ikonę play. |
| 11 | Retention korzysta z FieldGroup i spokojnego obramowania zamiast szarego insetu, z zachowaniem walidacji/confirmations. |
| 12 | Clear matched data wyrównany do prawej, summary zachowuje zawijanie mobile. |
| 13 | Refresh ma ikonę odświeżania; Add note plus. |
| 14 | Pending toast używa istniejącego shadcn Base UI Toast: nagłówek Approve change on staging? z czerwonym środowiskiem, dwuliniowy SQL preview, Review i Deny. Błąd Deny pozostawia toast oraz komunikat; usunięcie następuje po autorytatywnym SSE. |
| 15 | Dialog: target i Requested by Codex z rzeczywistym linkiem Session w headerze, Values inline / As sent. Oryginalne SQL/parameters/digest oraz approve guards pozostają autorytatywne. |
| A/B | Widoczne kółka z literami wróciły nad prawdziwymi, unikalnie nazwanymi radio inputs. Wybrane A jest szare, B ciemne. Natywna obsługa klawiatury/instancji zachowana. |

## Znaczenie danych Usage

Failed/rejected rate = `(failed + rejected) / wszystkie pasujące operacje`; pusty zbiór nie daje sztucznego 0%. Sessions to unikalne sesje dziennika zawierające wybrane operacje, nie liczba conversations. Output KPI wykorzystuje tylko `workflow-output`, nie historyczne artifact-volume; estymacja tokenów nie oznacza pełnego zużycia modelu ani podstawy do wymyślenia kosztu.

Logical reads to suma dostępnych zapisanych median wierszy metryk, bez mnożenia przez repetitions; nie jest to rzeczywista suma odczytów wszystkich wykonań. Osobne pokrycie operacji i metryk pozwala rozróżnić brak, zero i częściowe dane. Agregacja metryk przed joinem nie zwiększa liczby operacji ani czasu. Ranking logical reads powstaje przed LIMIT na całym wybranym zbiorze. Metryki respektują wspólny zakres czasu/profil/unprofiled/wymiary; kompletna lista profili jest świadomie metadanymi wyboru zakresu.

Review wykrył i zamknął regresje filtrów: KPI/SQL wykorzystują te same aktualnie dostępne wymiary co panel; nie utrzymują niewidocznego filtra po zmianie czasu. Błąd pobrania metadanych pokazuje ErrorState bez render crash lub starych KPI. Test RED/GREEN i siedem kontroli browser potwierdzają zachowanie wyboru oraz odzyskanie widoku po przywróceniu metadanych.

## Dowody i granice

- [Kontrolki — 28 kontroli](control-results.json), [Usage — 12 kontroli](usage-check-results.json), [live Session — 5 kontroli](session-live-results.json).
- [Pełne capture app/mock](browser-results.json) obejmuje 20 widoków/pomiarów i 10 kontroli interakcji. Trzy nierozwinięte atrybuty SVG mocka Operation są zachowane jako diagnostyka referencji; błędów aplikacji nie zaobserwowano.
- Dane browser są jawnie syntetyczne. Semantykę rzeczywistej agregacji pokrywają testy .NET. Brak migracji, nowych zapisów danych lub backfillu.
- Brak merge, push, lokalnej publikacji lub restartu zainstalowanego dashboardu. Brak Linux/live DB UAT. Testy przycisków mutation użyły wyłącznie przechwyconego fake API: dwa Deny, zero Approve.


## shadcn i odstępstwa od mocka

Użyto istniejących Button (ghost/outline/default/secondary), Badge, Table, Tabs, Field/FieldGroup, Card, Separator, Base Toast i Dialog. Warianty oraz typografia pozostają zgodne z presetem base-mira, Inter i Hugeicons. Przycisk Details jest kompozycją ghost Button z chevron. Toast mutation jest oficjalnym komponentem Base UI dodanym przez shadcn; własna pozostaje jego treść domenowa.

| Różnica | Ocena |
|---|---|
| Domyślna typografia, promienie, odstępy i rozmiary kontrolek Mira zamiast pikselowego odtworzenia mocka | Zachować default. Dalsze kopiowanie mocka oznaczałoby skórkowanie komponentów bez istotnego zysku. |
| Okrągłe A/B z literami, A szare / B ciemne | Jawnie wybrany wyjątek operatora; istniejące buttonVariants i semantyczne tokeny. Pod spodem działają prawdziwe radio inputs z klawiaturą i osobnymi grupami. |
| Opis wersji inline obok vN | Jawny wyjątek operatora; lepsze wykorzystanie miejsca, pełna treść dostępna w title. |
| Połączony pas KPI | Dopasowanie do mocka przez układ Card/Separator, bez nowych stylów kontrolek. Dane pochodzą z dziennika, więc nie odtwarzają przykładowych liczb/kosztów mocka. |
| Retention z FieldGroup i pionowym obramowaniem | Zachować default i czytelny układ formularza zamiast szarej wielkiej powierzchni mocka. |
| Values inline / As sent jako istniejący segmented RadioGroup | Wybór wykluczający się; zachowana klawiatura. Nie wprowadza dodatkowego przełącznika boolean. |
| Tabele na wąskim ekranie przewijają się lokalnie | Zachować; aplikacja nie ma poziomego przepełnienia dokumentu. Referencyjne mocki Session/Profiles/Settings mają takie przepełnienie przy 390px. |

Nie znaleziono pozostałego błędnego użycia komponentu w zmienionym zakresie po task reviews i końcowym scoped review. To ocena tego zakresu, nie deklaracja audytu wszystkich komponentów repozytorium.

## Mutation: pochodzenie i ograniczenia podglądu

Requester pochodzi wyłącznie z zachowanej korelacji mutation_requests.operation_id → operations.session_id → sessions.agent_kind. SSE pending jest wzbogacane po istniejącym LinkMutation. Brak wiarygodnej korelacji daje unknown requester; nie zgadujemy autora ani sesji z SessionKey lub najnowszego wpisu. Nie ma migracji ani backfillu.

Values inline jest tylko prezentacją. Renderer rozpoznaje dialekt, literały, komentarze, identyfikatory, SQL Server @@ oraz PostgreSQL dollar quotes; obsługuje konserwatywny podzbiór typów tekstowych, liczbowych, bool i null. Nieznany/duplikowany/brakujący parametr albo nieobsługiwany typ wyłącza inline z wyjaśnieniem i zachowuje dokładne As sent. Obowiązują limity 100 000 znaków i 500 parametrów; budżet obejmuje każde powtórzenie wstawianego literału przed ekspansją. Regresja ponadmilionowego preview przeszła test i browser fallback. Nie zmieniono parametrów, digest, payloadu decyzji ani silnika wykonania. Zachowano opóźnienie Approve, ochronę Enter, expiry oraz busy/error feedback.

## Weryfikacja przeglądarkowa i review

[Podsumowanie 104 asercji](browser-check-summary.json): 10 ogólnych interakcji, 13 pomiarów dokumentu aplikacji, 5 Session SSE, 28 kontrolek, 12 Usage, 7 błędu/odzyskania Usage, 29 mutation. [Galeria](index.html) zawiera świeże porównania z niezmienionymi mockami z docs/design/dashboard-2026-10-25.

Trzy błędy SVG z nierozwiniętych szablonów występują wyłącznie w referencyjnym mocku Operation. Cztery celowe odpowiedzi HTTP 500 należą do dwóch negatywnych scenariuszy fixture (trzy próby metadanych Usage i jeden Deny); są zapisane oddzielnie od nieoczekiwanych błędów. Nie zaobserwowano błędów renderowania/console aplikacji poza tymi celowymi odpowiedziami.

[Review Task 1](reviews/task-1-review.md), [Task 2 po poprawce](reviews/task-2-rereview.md), [Task 3 po poprawce](reviews/task-3-rereview.md) zaakceptowane. [Review całości](reviews/final-review.md) wskazał cztery uwagi; jedna wspólna fala poprawek zakończona [zaakceptowanym scoped re-review](reviews/final-fix-review.md). Szczegóły testów celowanych, w tym RED/GREEN, są w raportach zadań i [raporcie końcowej poprawki](reviews/final-fix-report.md).


## Pierwszy pełny gate i korekta testu

Pierwszy `just verify` na bbda23d2 zatrzymał się w ui-check po 7m43s: 435 testów przeszło, jeden nieaktualny test StatusBadge wymagał, żeby running nie było Badge. To przeczy wymaganiu operatora ze zrzutu 1. Zachowano [pełny log](gate-initial/ui-check.log) i [build](gate-initial/build.log); etap .NET test nie został wtedy uruchomiony.

Commit b23f2272 aktualizuje tylko istniejący test: wymaga Badge z tier=info oraz braku przycisku błędu. Nie zmienia kodu runtime i nie usuwa pokrycia interakcji błędów. Celowany zestaw StatusBadge przeszedł 9/9, a niezależny reviewer zaakceptował korektę. Powtórny pełny gate wykonuje się na b23f2272. Wcześniejsze zrzuty bbda23d2 mają identyczne źródła runtime; jedyną różnicą jest test.

Drugi pełny przebieg na b23f2272 przeszedł UI 436/436 oraz MCP 535/4 skips, ale zakończył się w Core: 4773 passed, 11 skips, 1 failed (11m57s). Jedynym błędem była nieaktualna lista pól DashboardStats w DashboardContractTests; brakowało dwóch wcześniej dodanych pól. [Log Core](gate-retry1/test-SqlHarness.Tests.log), [MCP](gate-retry1/test-SqlHarness.Mcp.Tests.log), [UI](gate-retry1/ui-check.log).

Commit b3e921cd aktualizuje wyłącznie test: dodaje topSqlByLogicalReads/sessionCount do pełnej listy i sprawdza zgodność z rzeczywistą deklaracją TypeScript istniejącym helperem. Całe DashboardContractTests przeszło 9/9; niezależny reviewer zaakceptował korektę. Runtime nadal identyczny z bbda23d2. Kolejny pełny gate testuje b3e921cd.

Szerszy [audyt app/mock](../mock-audit-20261028/report.md) i wcześniejszy [raport default shadcn](../default-alignment/report.md) pozostają źródłem porównania pozostałych ekranów. Ten raport zamyka późniejsze piętnaście uwag ze zrzutów i najnowszy wybór A/B.


## Końcowy wynik

**IMPLEMENTED AND VERIFIED, lokalna gałąź dashboard-layout-polish.** Końcowy `just verify` na b3e921cd42a0fcc8d512cd0b85936acbc407e8aa zakończył się exit 0: pięć etapów, 15m26s. [Receipt](gate-final/receipt.json), [build](gate-final/build.log), [UI](gate-final/ui-check.log), [Core/Dashboard/CLI](gate-final/test-SqlHarness.Tests.log), [MCP](gate-final/test-SqlHarness.Mcp.Tests.log).

- UI: 436/436, 71 plików; typecheck/lint PASS.
- Core/Dashboard/CLI: 4774 passed, 11 existing platform skips; MCP: 535 passed, 4 existing skips.
- Format gate pominął 0 niezatwierdzonych C#; oddzielna [kontrola całej gałęzi](branch-format.log) objęła wszystkie pięć zmienionych C# i przeszła. Lokalny, ignorowany outputowy [runner xUnit](xunit-runner-snapshot.json) zachowuje sekwencyjne kolekcje; nie zmieniono śledzonej konfiguracji ani asercji w celu skrócenia testów.
- [104 asercje browser](browser-check-summary.json), zatwierdzone task/final reviews i test-only rereviews. Zrzuty pochodzą z runtime bbda23d2; późniejsze dwa commity zmieniają wyłącznie testy.
- [Manifest 224 plików UI](tested-ui-source.json), [zmienione wejścia produktu/testów](tested-changed-inputs.json), [hash dist index](dist-identity.json). Przed końcowym commitem dokumentacji potwierdzono identyczność źródeł i dist; commit dokumentacji nie zmienia src/tests/scripts.

Surowe logi zachowane bez normalizacji końców linii. Pierwsze dwa nieudane gate są również zachowane; zielony wynik dotyczy trzeciego pełnego przebiegu, bez pomijania etapów testowych. Scratch orkiestracji pozostał lokalnie; omyłkowo śledzony raport Task 3 przeniesiono do reviews. Brak merge/push/publikacji/restartu, brak Linux lub live DB acceptance. Serwery fixture zamknięto; dashboard 47800 pozostał bez zmian.
