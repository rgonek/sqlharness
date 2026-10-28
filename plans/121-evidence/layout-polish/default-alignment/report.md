# Dashboard: zgodność z mockami przy zachowaniu defaultów shadcn

## Zakres i stan

Realizacja dyrektywy operatora z 2026-10-28: usunąć ustalenia audytu, preferując obecne shadcn base-mira / Base UI, a odstępstwa od mocków ocenić pod kątem kosztu odejścia od defaultu. [Spec](../../../../docs/superpowers/specs/2026-10-28-dashboard-default-alignment.md), [plan](../../../121-default-shadcn-followup.md), [audyt przed zmianami](../mock-audit-20261028/report.md).

**IMPLEMENTED AND VERIFIED na `c2c3dcfd`.** Końcowy `just verify` zakończył się exit 0: pięć etapów, 11m00s. UI 414/414 w 70 plikach, Core/Dashboard/CLI 4771 passed / 11 existing skips, MCP 535 passed / 4 existing skips. Build, typecheck i lint PASS; format pominięty dla 0 zmienionych plików C#. [Receipt](accepted-gate/result.json), [build](accepted-gate/build.log), [UI](accepted-gate/ui-check.log), [Core](accepted-gate/test-SqlHarness.Tests.log), [MCP](accepted-gate/test-SqlHarness.Mcp.Tests.log). Gałąź pozostaje lokalna; bez merge/push/publikacji/restartu zainstalowanego dashboardu.

Semantyka Settings/A-B: `fa5aba0f`, niezależne review spec i quality PASS. Session/Profiles/Usage/CSS: `1fa7e8fc`. Wcześniejszy hover: `e0de0823`. Trzy końcowe poprawki layoutu: `3fe231df`, scoped re-review wszystkich trzech findings ADDRESSED bez nowych uwag. Korekta starego selektora testowego: `c2c3dcfd`, 12/12 focused tests i [niezależne review](reviews/test-selector-review.md) PASS. Nie zmieniła kodu runtime.

Świeże porównanie ma [41 przechodzących kontroli](browser-check-summary.json), [pełne wyniki scenariuszy](browser-results.json), [klawiaturę i dialog](detail-results.json), [powiązania formularza](field-results.json) i [galerię](index.html). Brak błędów page/console aplikacji i przepełnienia dokumentu w sześciu mobilnych widokach. Zapisano trzy błędy konsoli po stronie eksportu mocka Operation: nierozwinięte `ed.W`, `ed.H`, `ed.d` w atrybutach SVG. To ograniczenie referencji; diagnostyka pozostaje w surowym JSON.

## Co wyrównano

- F1: widoczne etykiety Settings, opisy i błędy są powiązane z kontrolkami; invalid input nie zapisuje się.
- F2: A i B to dwa niezależne wybory pojedyncze. Przeglądarka potwierdza ArrowDown `A v0 → A v1`; testy izolują również dwie tabele na jednej stronie.
- F3: Session ma kompaktowe siedem kolumn zamiast ogólnej tabeli z powtarzanym kontekstem. Zachowano gwiazdkę, agent keep, opis/preview jeśli istnieje, target, flagi, status, czas/reads, linki powrotne, internal toggle i paginację.
- F4: znane wartości TLS/mutation/writer mają czytelne nazwy i pomoc; nieznane nadal wyświetlają prawdziwą wartość. YOLO ma standardowy Alert z jawną informacją o braku approval.
- F5: Session ma Breadcrumb z oficjalnego rejestru, kontener 1200px i desktop inset 24px, bez pogarszania mobile.
- F6: semantyczny header zastąpił nagłówek udający Card; usunięto jego powielone override/`!important`, skórki wnętrza KPI Cards i ręczne rozmiary tekstu/wysokości wierszy VersionTable.

## Zasada rozstrzygania różnic

Układ i treść można dopasować zwykłym layoutem i kompozycją. Geometria/typografia/kolory wnętrza kontrolek pozostają po stronie defaultu, chyba że istnieje konkretny powód użytkowy lub jawna wcześniejsza decyzja operatora. Nie dodajemy brakujących metryk tylko dlatego, że mock je ilustruje.

## Odstępstwa i opłacalność dokładnego dopasowania

| Obszar | Różnica względem mocka | Wybrany wzorzec | Czy warto odejść od defaultu dla zgodności? |
|---|---|---|---|
| Settings | Drobniejsze różnice typografii, odstępów i układania pól | Zainstalowany `Field` / `FieldLabel` / `FieldDescription` / `FieldError`, `Input` i `Switch`; jawne powiązania dostępności | Nie. Czytelne, dostępne defaulty są ważniejsze niż ręczne rozmiary i stylowanie kopii mocka. |
| Wybór A/B | Zamiast liter we własnych 24px przyciskach są standardowe okrągłe radio w kolumnach A/B | Natywne nazwane grupy, unikalne per tabela, semantyczny `accent-primary` | Nie warto przywracać przycisków pozbawionych semantyki wyboru. To jawny wyjątek od komponentu shadcn: dwie przeplatane grupy w tabeli wymagają poprawnego HTML. Base UI RadioGroup można rozważyć dopiero z innym układem, bez imitowania radio własną klawiaturą. |
| Tabela Operations w Session | Siedem kolumn, pełna data/czas i nieco wyższe opisy zamiast ręcznie zagęszczonych ośmiu kolumn; bez powtarzania transportu w każdym wierszu | Default `Table` w `Card`, siedem kolumn; rzeczywisty transport sesji w nagłówku, bo OperationSummary nie ma własnego transportu | Nie warto usuwać dowodów/akcji lub dodawać skórki dla gęstości. Dalsze skrócenie czasu do HH:mm można rozważyć jako mały layout/content follow-up, jeśli operator uzna je za potrzebne; pełny zapis pozostaje czytelny. |
| Session header / Breadcrumb | Standardowa geometria Breadcrumb i semantyczny header zamiast własnego stylowania tekstu i pozbawionej obramowania Card | Oficjalny shadcn Breadcrumb; zwykły layout kontenera 1200px / 24px | Nie. Zwykły layout daje istotną zgodność bez zmiany wnętrza komponentów. |
| Usage KPI | Osobne karty z odstępem, standardowy mniejszy CardTitle zamiast wspólnego obramowanego paska i ręcznych 20px liczb | Pięć zwykłych shadcn Cards w responsywnej siatce 1/2/5 kolumn | Nie. Dokładny pasek wymagałby ponownego usunięcia default border/radius/shadow i zmiany typografii CardTitle. Lepszy jest czytelny standard. |
| Profiles | Wyszukiwarka pozostaje zwykłym Input bez ikonki; pomoc o szablonie pozostaje pod tabelą, ostrzeżenie YOLO jest Alert zamiast czerwonego akapitu | Standardowe `Input`, `Card`, `Table`, `Alert`; czytelne mapowania z niezmienioną polityką | Nie warto dodawać InputGroup wyłącznie dla dekoracji. Warto dopasowywać tekst i znaczenie, co wykonano; nie potrzeba własnej skórki. |
| Activity | Szczegółowe filtry i semantyczne kolory hover zamiast dokładnych surowych kolorów mocka | Obecne kontrolki shadcn i zachowany hover całych wierszy | Nie. Filtry są uzgodnioną funkcją, a tokeny utrzymują motyw. |
| SQL i graf planu | Monaco i wyspecjalizowany graf zamiast ilustracji eksportu; mock edytora pozostaje na Loading | Obecne komponenty domenowe plus shadcn do otaczającego UI | Nie ma równoważnego zamiennika shadcn. Warto zmieniać tylko konkretne problemy czytelności, nie odtwarzać statycznego rysunku. |
| Conversations i Switch | Uzgodniony układ Conversations i switch 16px mogą odbiegać od eksportu/upstream | Zachowana jawna decyzja operatora | Nie przywracać automatycznie upstream 16.6px ani zmieniać układu Conversations w ramach wyrównywania. |
| Dane Usage/Profiles/Mutation | Mock ilustruje agregaty, usage/last-used i metadane, których nie zapewnia dany kontrakt | Rzeczywiste dane i jawny brak dostępności | To nie kwestia skórki. Ewentualna zmiana wymaga osobnego kontraktu danych; nie warto fabrykować treści dla podobieństwa wizualnego. |

Pozostawiono wyspecjalizowany layout toolbarów porównań, SQL/grafu, zatwierdzony pasek zapisanej notatki i istniejące semantyczne wyróżnienia wyników. Nie jest to masowa migracja wszystkich historycznych selektorów CSS. Dalsze upodobnienie tych obszarów do defaultu ma sens przy konkretnej usterce lub niezależnie wybranym zakresie; nie warto wracać do ręcznych skór w celu kopiowania eksportu.

## Pokrycie i ograniczenia

Desktop 1280×900 oraz mobile 390×900: Activity, Session, Operation, Profiles, Usage, Settings; dodatkowo mutation dialog mobile. Sprawdzono hover, linki, rozwijanie Enter, saved-note re-edit, profil/search, A/B ArrowDown, Switch Space/etykietę, walidację, Review Enter, początkowe disabled Approve, opóźnienie 1s i Escape. Nie wysłano approve/deny. Oryginalny mock mobile przepełnia dokument w Session/Profiles/Settings; nie kopiowano tego zachowania. Screenshoty light theme; wcześniejszy hover ma odrębne dark-theme evidence, nowej pełnej macierzy dark nie wykonano. Nie deklarujemy screen-reader audit, Linux ani live DB UAT. Jedyny PUT w harness dotyczy fake Settings.

Funkcje paginacji/internal/return context/star PATCH i nieznane wartości profili mają focused testy Vitest. [Dodatkowe 14 kontroli](layout-edge-results.json) potwierdza zawijanie prose operator/YOLO na 390px, pełną długą nazwę profilu bez overflow przy 390/1280px oraz intrinsic width pustego Add note i pełną szerokość edytora/zapisanej notatki. To łącznie 55 przechodzących kontroli przeglądarkowych. Część wizualna używa ilustracyjnych danych, nie dowodzi agregacji produkcyjnego DB.

[Manifest źródeł](tested-ui-source.json) obejmuje 222 tracked inputs UI na `c2c3dcfd`, combined SHA-256 `aed6ee2a5e98403189fa1315f11d30b7e94d4b975e3cb103349df745d84aa525`. Wszystkie hashe oraz [tożsamość dist](dist-identity.json) sprawdzono ponownie po zielonym gate: bez zmian. Runtime UI i screenshoty pochodzą z `3fe231df`; późniejsza jednoliniowa korekta dotyczyła wyłącznie testu, a dist pozostał identyczny.

Niezależne review: [Settings/A-B](reviews/task-1-review.md), [Session/Profiles/Usage](reviews/task-2-review.md), [całość przed poprawkami](reviews/final-review.md), [scoped re-review trzech poprawek](reviews/final-fix-review.md). Raporty implementerów i ledger są zachowane w tym samym katalogu `reviews/`.

Pełny gate korzysta z wcześniej istniejącej, ignorowanej konfiguracji runnera Core: `parallelizeTestCollections=false`, `maxParallelThreads=1`, `diagnosticMessages=true` ([snapshot](core-runner-settings.json)). Nie zmieniano tracked runner policy, C# ani selekcji testów; działa standardowy filtr non-integration.

Pierwszy `just verify` zakończył się exit 1 po 8m37s: build PASS, UI 413 PASS / 1 FAIL. `OptimizationSection.test.tsx` nadal wyszukiwał `All operations`, choć autoryzowana zmiana używa `Operations (liczba)`. Zachowano asercję kolejności sekcji i poprawiono wyłącznie selektor. Testy .NET i etap format nie zostały w tej próbie uruchomione. [Log UI](initial-gate/ui-check.log), [build](initial-gate/build.log), [manifest pierwszej próby](initial-gate/tested-ui-source.json). Ta próba nie jest dowodem PASS ani wcześniejszą awarią shutdown z poprzedniego zakresu; dowodem akceptacji jest dopiero późniejszy pełny zielony gate.

## Granice

Nie zmieniamy credential/mutation policy, schematu ani historycznych sesji. Przeglądarka używa fixture API; nie zatwierdza prawdziwych mutation. Brak merge, push, lokalnej publikacji lub restartu zainstalowanego dashboardu w tym zakresie. Wcześniejsze logi błędu testu shutdown pozostają odrębnym dowodem i nie są ukrywane.
