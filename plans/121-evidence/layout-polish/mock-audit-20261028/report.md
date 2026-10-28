# Audyt dashboardu względem mocków — 2026-10-28

## Wynik i granice

Bieżący dashboard zachowuje główny układ i workflow mocków. Świeże sprawdzenia potwierdziły działanie hover całych wierszy, niezależnych linków, rozwijania klawiaturą, edycji notatki, wyboru profilu i otwierania/zamykania mutation dialogu. Nie znalazłem P0/P1 w uruchomionych scenariuszach. Pozostały dwa istotniejsze tematy dostępności/semantyki komponentów, jedna wyraźna różnica układu tabeli i trzy drobniejsze tematy zgodności/utrzymania. To raport audytu, bez wdrożenia zaleceń.

Oceniany stan: `dashboard-layout-polish`, baza `ed280c41403572efde4930365dd1c41e3d4bc653` i lokalne zmiany hover w `ActivityFeed.tsx` / `IoComparison.tsx`. Otwierałem samodzielnie serwowany aktualny `ui/dist` na `127.0.0.1:48138`, z przechwyceniem wszystkich `/api/**` przez syntetyczne fixtures. Nie oceniałem zainstalowanego dashboardu `47800`, który nie zawiera jeszcze tej poprawki hover. Mocki: bieżące pliki w `docs/design/dashboard-2026-10-25`, serwowane na `48137`.

Artefakty: [galeria porównań](index.html), [wyniki scenariuszy i pomiary szerokości](browser-results.json), [mutation/klawiatura](detail-results.json), [etykiety i błędy formularza](field-results.json), [shadcn info](shadcn-info.json), [tożsamość dist](dist-identity.json). Screenshoty wykonano ponownie podczas tego audytu, nie skopiowano wcześniejszych wyników.

Nie wykonywałem instalacji komponentów, zmian kodu produktu, publikacji, restartu prawdziwego dashboardu, merge ani push. Jeden PUT Settings został obsłużony wyłącznie przez fixture. Nie wysłano decyzji mutation. Nie powtarzałem pełnego gate dla audytu dokumentacyjnego. Istniejący błąd testu backendowego shutdown nie jest findingiem zgodności z mockiem.

## Findings

### F1 — P2: Settings nie łączy widocznej etykiety, opisu i błędu z kontrolką

**Typ:** dostępność i kompozycja komponentów; potwierdzone w przeglądarce, nie tylko brak preferowanego wrappera.

**Miejsce:** `src/SqlHarness.Dashboard/ui/src/pages/SettingsPage.tsx:20` (`SettingRow`), `:23` (`Toggle`), `:28` / `:44` / `:50` / `:59` (`NumberField`).

**Obecnie:** tytuł i opis są zwykłymi `div`, a Switch otrzymuje tylko `aria-label`. Kliknięcie widocznego „Connection diagnostics” nie przełącza stanu (`false → false`); Switch ma pustą listę etykiet i nie ma `aria-describedby`. Wszystkie 10 sprawdzonych przełączników nie ma połączenia z opisem. Wpisanie `0` do Connection timeout i Enter pokazuje poprawny błąd, a Input ma `aria-invalid=true`, lecz brak `aria-describedby` / `aria-errormessage`; komunikat nie ma ID ani roli alert. Samo ograniczenie wartości działa, ale pomoc i wynik walidacji nie są powiązane z kontrolką dla technologii asystujących.

**Oczekiwane:** zachować wygląd wierszy Settings, umożliwić klik widocznej etykiety oraz udostępnić znaczenie i błędy przy skupieniu kontrolki. Nie zakładam, że mock definiuje kompletną dostępność — to ustalenie z rzeczywistej semantyki formularza.

**Komponent:** istniejące `FieldGroup`, `Field orientation="horizontal"`, `FieldContent`, `FieldLabel htmlFor`, `FieldDescription`, `FieldError` oraz obecne `Switch` / `Input`. Nadać opisom i błędom stabilne ID i jawnie połączyć `aria-describedby`; samo zastąpienie `div` przez Field nie gwarantuje tych relacji w lokalnej implementacji. Zachować `aria-invalid`, dodać `data-invalid` na Field. [Field](https://ui.shadcn.com/docs/components/base/field), [Switch](https://ui.shadcn.com/docs/components/base/switch).

**Dowód:** [field-results.json](field-results.json), [Settings z błędem](settings-validation-app.png). Test Space na samym Switch przechodzi — kontrolka nie jest całkowicie niedostępna, problem dotyczy etykiet/opisów/walidacji.

### F2 — P2: wybór wersji A/B korzysta z niezależnych Toggle zamiast semantyki dwóch grup wyboru

**Typ:** dobór komponentu i zachowanie klawiatury; nie propozycja redesignu.

**Miejsce:** `src/SqlHarness.Dashboard/ui/src/components/optimization/VersionTable.tsx:28` i `:29`.

**Obecnie:** w każdym wierszu są dwa `Toggle`. Faktycznie użytkownik wybiera jedną wersję A i jedną B z wielu wierszy, a nie niezależnie włącza/wyłącza sześć opcji. `onPressedChange` ignoruje proponowany stan i zawsze wybiera daną wersję, więc Toggle zaznaczony nie działa jak przełącznik on/off. Brak grupowania i nawigacji strzałkami: w tabeli są 0 elementów radio; ArrowDown z A v0 pozostawia A v0. Tab i aktywacja przyciskiem nadal umożliwiają wybór — nie jest to blokada klawiatury.

**Oczekiwane:** dwie nazwane, niezależne grupy wyboru pojedynczego („Baseline A”, „Candidate B”), z aktualnym zaznaczeniem i nawigacją właściwą dla takiej grupy. Zachować zatwierdzone przez operatora okrągłe etykiety A/B i ich obecne kolory/rozmiary. Brak ArrowDown nie jest błędem API Toggle; jest skutkiem wyboru wzorca niezależnych przełączników dla ekskluzywnego wyboru.

**Komponent:** preferowane semantycznie `RadioGroup` / `RadioGroupItem`; przy rozmieszczeniu grup w kolumnach tabeli trzeba zachować prawidłowy HTML, bez wstawiania wrapperów `div` między `tbody`, `tr` i `td`. Jeżeli Base UI nie daje bezpiecznej kompozycji dwóch przeplatanych grup w tej tabeli, uzasadniony wyjątek to natywne radio z osobną nazwą per strona i obecnym wyglądem. `ToggleGroup` jest właściwszy dla zestawu przełączanych przycisków; nie należy mechanicznie stosować go tutaj tylko dlatego, że skill wspomina zestawy 2–7 opcji. Istniejący `SegmentedRadioGroup` w toolbarach Plan/IO/Settings już ma właściwą semantykę i nie wymaga wymiany. [Radio Group](https://ui.shadcn.com/docs/components/base/radio-group), [Toggle Group](https://ui.shadcn.com/docs/components/base/toggle-group).

**Dowód:** [detail-results.json](detail-results.json), [tabela A/B w Session](session-plan-app.png). Wygląd jest bliski mockowi; finding dotyczy znaczenia kontrolek i grupowania.

### F3 — P2: lista operacji Session pozostaje szeroką tabelą ogólną zamiast zwartej listy z mocka

**Typ:** widoczna różnica fidelity, bez stwierdzenia błędu danych lub wykonania.

**Miejsce:** `src/SqlHarness.Dashboard/ui/src/pages/SessionPage.tsx:98`–`:110` — `All operations` z `OperationTable`.

**Mock:** sekcja „Operations”, liczba załadowanych operacji obok nagłówka, Show internal operations po prawej; zwarte wiersze ID/czas/transport/rodzaj/opis/status/czas wykonania/reads. Nie powtarza właściciela sesji, agenta i targetu w osobnych szerokich kolumnach.

**Obecnie:** ogólna tabela ma m.in. Owner, Label, Started, Session / subject, Agent, Operation, Status, Target, Duration, Logical reads, Flags. Sekcja jest znacznie większa i powtarza kontekst strony. Na 390px pierwsze widoczne kolumny to głównie Owner i Label; status/czas/reads wymagają znacznego przewijania poziomego. Przewijanie jest lokalne i działa — nie ma przepełnienia całej strony. Nie oceniam liczby i wartości wierszy między różnymi syntetycznymi datasetami jako defektu.

**Komponent:** nadal `Card` + `CardHeader` / `CardTitle` / `CardDescription` / `CardAction` oraz `Table`, z dedykowaną projekcją kolumn dla kontekstu sesji. Można współdzielić prezentację kompaktowych wierszy z Activity, zamiast budować drugi zestaw przycisków/statusów. Zachować paginację, show-internal, istniejące linki i agent keep. Bez dodawania nieistniejącego SQL preview.

**Dowód:** dolna sekcja [Session app](session-plan-app.png) vs [Session mock](session-plan-mock.png), [Session 390px](session-mobile-app.png); mock `Session Optimization.dc.html:68`.

### F4 — P3: Profiles pomija część czytelnych nazw i objaśnień, mimo dostępnych danych

**Typ:** różnica treści i prezentacji, nie zmiana polityki runtime.

**Miejsce:** `src/SqlHarness.Dashboard/ui/src/pages/ProfilesPage.tsx:43`–`:45`; mock `Profiles.dc.html:84`–`:90`.

**Obecnie:** writer wyświetla identyfikator `sameAsReader`, a mode `yolo`; TLS to samo `optional`. Mock pokazuje czytelne „same as reader”, nazwę trybu oraz objaśnienia przy TLS i writerze. Aplikacja poprawnie ostrzega, że YOLO działa bez approval, więc nie jest to całkowity brak informacji. Mock ma również pomoc przy password env var i template w konkretnej komórce, a app przesuwa template do stopki karty. Te teksty nie wymagają nowych agregatów backendu.

**Komponent:** zachować `Card` + `Table`; w `TableCell` użyć czytelnego mapowania znanych wartości i podrzędnego tekstu pomocy. Zachować domenowe znaczenie YOLO i jawność writer identity; nie zmieniać klasyfikacji mutation, credential policy ani domyślnych trybów. Dla ostrzeżenia wymagającego uwagi można użyć istniejącego `Alert`; zwykłe wyjaśnienie pola powinno pozostać tekstem pomocy, nie każdym razem alertem. Opcjonalnie `InputGroup` z ikoną dla search, jeśli wymagana jest zgodność wizualna z mockiem. [Table](https://ui.shadcn.com/docs/components/base/table), [Alert](https://ui.shadcn.com/docs/components/base/alert).

**Dowód:** [Profiles app](profiles-app.png), [Profiles mock](profiles-mock.png). Brak parsed variable choices, usage count i ostatniego użycia profilu nie należy do tego findingu — to ograniczenia kontraktu opisane niżej.

### F5 — P3: Session ma inną szerokość i hierarchię nawigacji niż mock

**Typ:** optyczna różnica layoutu, do rozstrzygnięcia przed ewentualną zmianą.

**Miejsce:** `src/SqlHarness.Dashboard/ui/src/index.css:183`–`:184`, `src/SqlHarness.Dashboard/ui/src/pages/SessionPage.tsx:53`; mock `Session Optimization.dc.html:15`.

**Obecnie:** przy 1280px Session korzysta z szerokości Activity (maks. 1280px, zawartość zaczyna się przy 16px), podczas gdy mock ma `max-width:1200px` i padding 24px (zawartość od 64px). Operation już ma węższy układ. W Session nie ma też mockowego breadcrumb „Activity / nazwa sesji”; nawigacja globalna Activity pozostaje dostępna.

**Zalecenie:** jeśli celem jest dalsza zgodność z mockiem, uzgodnić jednolity kontener Session/Operation i przywrócić niewielki breadcrumb. Do breadcrumb użyć `Breadcrumb` po świadomej instalacji (nie jest obecnie na liście zainstalowanych komponentów); szerokość to layout zwykłego kontenera, bez potrzeby Card/Sidebar. Nie traktować tego jako wymogu zmiany responsywności: mock przy 390px sam przepełnia dokument.

**Dowód:** [Session app](session-app.png), [Session mock](session-mock.png), pomiary scroll width w [browser-results.json](browser-results.json).

### F6 — P3: lokalne nadpisania styli shadcn są rozproszone i utrudniają utrzymanie

**Typ:** dług utrzymania, obserwacja statyczna; nie osobny defekt działania w tej sesji.

**Miejsce:** `src/SqlHarness.Dashboard/ui/src/index.css:258`–`:260`, `:309`, `:323`, `:332`, `:374`–`:377`.

**Obecnie:** te same selektory session-heading są definiowane kilkakrotnie, częściowo z `!important`, a KPI zmieniają wnętrza Card przez globalne selektory `data-slot`. Style rozmieszczone w kilku blokach decydują o paddingu, cieniach, typografii i geometrii komponentu. To nie oznacza, że każdy override jest błędem — wiele odzwierciedla zatwierdzony design. Powoduje jednak, że stan hover/expanded/focus trzeba odtwarzać z kilku miejsc.

**Zalecenie:** przy kolejnej pracy nad tymi sekcjami zebrać zatwierdzone warianty w jednym miejscu i korzystać z publicznych wariantów/kompozycji komponentu, zachowując wygląd. Nie usuwać wyjątków masowo. Widoczny nagłówek strony nie potrzebuje Card pozbawionego border/shadow/padding wieloma override; można użyć `header`. Standardowa karta danych powinna zachować `CardHeader` / `CardContent`. Nie zmieniać switch 16px ani okrągłych A/B w ramach „porządkowania”. [Card](https://ui.shadcn.com/docs/components/base/card).

## Dobór komponentów shadcn według widoku

CLI `npx shadcn@latest info --json` potwierdziło `base-mira`, `base=base`, Vite, Tailwind v4, alias `@`, Inter, Zinc, Hugeicons. Nie ma podstaw do zmiany presetu, ikon ani frameworka. Skill odczytano ze wskazanej przez użytkownika ścieżki; pobrano także jego reguły composition i Base-vs-Radix. Dokumentację uzyskano poleceniem CLI `docs` i otwarto oficjalne strony.

| Obszar | Właściwe komponenty | Ocena obecnej kompozycji / zakres zalecenia |
|---|---|---|
| Activity | `Input` / opcjonalnie `InputGroup`, `Select` lub `Popover` z checkboxami, `Toggle`, `Button`, `Badge`, `Collapsible` | Filtry status/type są niezależne: Toggle jest uzasadniony. Nie wymieniać ich na RadioGroup. Cały nagłówek klikany, ale niezależny Button disclosure pozostaje dla klawiatury; poprawny świadomy wzorzec. |
| Session | `Collapsible` + `CollapsibleTrigger render={<Button />}`, `Textarea`, `Button` dla zapisanej notatki, `Card`, `Table`, opcjonalnie `Breadcrumb` | Details i note działają; sam panel Conversations to lista metadanych, nie chat. Nie potrzebuje Message/Bubble/MessageScroller. Ulepszyć tabelę operacji w kontekście sesji (F3). |
| Optimization | `Table`, semantyka wyboru A/B z F2, `Tabs`, `Popover`, `Checkbox`, istniejący `SegmentedRadioGroup` | TabsTrigger są w TabsList, Popover używa Base `render`, istniejące FieldSet w More columns ma sens. Nie zastępować działającego segmented radio samym Button active. |
| Operation | `Tabs`, `Table`, `Badge`, `Popover`, `Collapsible`; SQL/plan jako Monaco i wyspecjalizowany graf | Nie ma shadcn zamiennika planu wykonania ani edytora SQL. Użycie wyspecjalizowanych bibliotek jest uzasadnione; nie przerabiać grafu na karty ani zwykły Alert. |
| Profiles | `Input` / `InputGroup`, `Button` jako wybór elementu, `Card`, `Table`, `Badge`, warunkowo `Alert` | Karty mają Header/Title/Content; nie stwierdzam błędu kompozycji Card. Sidebar app shell byłby nadmiarem dla krótkiej listy profili. Czytelność tekstów: F4. |
| Usage | `Card`, `Table`, `Select`, `Tabs` lub segmentowy wybór okresu, `Collapsible`, istniejący `Chart` | Obecna kompozycja kart jest poprawna. `ProfileDimensionsPanel` i dodatkowy accounting to istniejący workflow — nie usuwać dla prostoty mocka. Puste kolumny z brakującymi kontraktami są ograniczeniem danych, nie dowodem potrzeby nowego komponentu. |
| Settings | `FieldGroup`, `Field`, `FieldLabel`, `FieldDescription`, `FieldError`, `Input`, `Switch`, `RadioGroup`, `Select` | Kontrolki są właściwe; ich opakowanie i relacje mają konkretny problem z F1. Nie zamieniać wyboru pojedynczego trybu mutation na niezależne Toggle. |
| Mutation | istniejący Base `Toast`, `ToastProvider`/manager, `Dialog`, `DialogTitle`/Description, `Alert`, `Table`, `Button`, `Separator` | Obecny provider/portal/dialog działa. Dialog ma nazwę, delay, Escape i niezależne Review. Nie migrować do Sonner (projekt Base), nie aktualizować komponentów tylko dlatego, że najnowsza dokumentacja pokazuje nowszy renderer Toast. |

`Empty` nie jest zainstalowany. Krótki komunikat „No profile matches.” przy pustym wyszukiwaniu nie wymaga automatycznie nowej zależności i rozbudowanej kompozycji Empty. `Separator` jest dobry dla semantycznego podziału sekcji; border wiersza tabeli lub gridu nie jest automatycznie niewłaściwym użyciem. `CardFooter` / `CardDescription` nie muszą być dodawane do każdej karty, jeśli nie ma dla nich treści. Wymogi skilla stosowano do znaczenia UI, nie jako mechaniczną listę regexów.

## Potwierdzone i świadome odstępstwa

- Activity ma szczegółowe filtry zamiast uproszczonych chipów mocka; README mocków jawnie zachowuje te filtry. To nie finding.
- Hover całego nagłówka sesji i IO jest obecny w ocenianym dist; klik w metadane rozwija, disclosure Enter działa, link tytułu otwiera sesję. Nie zgłaszam ponownie naprawionego hover.
- Conversations: tytuł, GUID, agent i liczba operacji po prawej. Zgodne z potwierdzoną decyzją operatora; nie proponuję odwrócenia układu.
- Switch: zatwierdzona geometria 16px / thumb 14px ma pierwszeństwo przed mechanicznym przywróceniem upstream 16.6px.
- Inter/Hugeicons i tokeny Mira pozostają. Nie przenosić raw kolorów, fontów i ikon z ilustracji do wszystkich komponentów.
- Usage nie ma kontraktu per-agent model/session/token/cost ani SQL preview/reads/last-run; Profiles nie ma usage count/last-used ani struktury parsed variable choices. Nie rekomenduję generowania tych danych z wyglądu mocka.
- Pending mutation nie ma wszystkich pól agent/session i TTL fraction z mocka. Dialog zachowuje dokładny SQL i osobną tabelę parametrów. Mock Monaco nadal wyświetla „Loading SQL viewer…”; porównano chrome i geometrię, nie rendering edytora mocka.
- Mock Operation ma pasek wyboru typu operacji wyłącznie jako narzędzie demonstracji. Nie jest brakującą funkcją produktu. Fixture grafu zawiera trzy węzły zamiast sześciu; to różne dane, nie wada rendererów.
- Wersje, liczby rekordów, czasy, targety i procenty są ilustracyjne; audyt nie dowodzi poprawności agregacji ani zgodności API z rzeczywistym DB.

## Matrix faktycznie uruchomionych sprawdzeń

| Widok | Stany faktycznie uruchomione | Viewport | Wynik / czego nie sprawdzono |
|---|---|---|---|
| Activity app/mock | początkowy widok; app metadata click, Enter disclosure, title navigation, hover | 1280×900; app 390×900 | 4 checks PASS, bez overflow dokumentu. Nie testowano wszystkich kombinacji filtrów ani history pagination. |
| Session app/mock | początkowy; Details; app zapis notatki, Enter re-edit, Cancel; SQL diff, IO, Plan | 1280×900; app 390×900 | 4 dalsze checks PASS (note i IO). Mock Details/IO/Plan przełączono. Nie testowano wielu conversation IDs, błędów zapisu i długich notatek. |
| VersionTable | focus A v0, ArrowDown; odczyt ról | 1280×900 | F2: brak radio i zmiany strzałką. Nie wykonano pełnego audytu czytnikiem ekranu. |
| Operation | measure i compare otwarte; SQL i syntetyczny graf | 1280×900; measure 390×900 | Brak page errors/overflow; porównanie wyglądu. Nie klikano każdego operatora, nie badano Timing/Details dla wszystkich typów ani pełnej zgodności source-map. |
| Profiles | dwa profile; wybór staging; pusty search | 1280×900 i 390×900 | 2 checks PASS; poprawny reflow. Nie badano realnego targets.json, missing/invalid config i bardzo długich nazw. |
| Usage | początkowy dashboard z kartami, tabelami i wymiarami | 1280×900 i 390×900 | Wygląd/overflow OK w danych fixture. Nie wykonano pełnego macierza zmian scope/range/drill-down i accounting; nie oceniano poprawności agregatów. |
| Settings | pełna strona; Space na Switch; klik widocznej etykiety; 0 + Enter w NumberField | 1280×900 i 390×900 | Switch Space PASS; F1 label/description/error relations. PUT tylko fixture. Nie wykonywano browser notifications, odtwarzania dźwięku ani czyszczenia transcriptów. |
| Mutation app / mock 1b | pending toast; Review Enter; początkowo disabled Approve; enabled po delay; Escape; ponowne otwarcie; Close | 1280×900; app dialog 390×900 | Wszystkie 6 kontroli zachowania PASS, brak overflow; bez approve/deny requests. Nie sprawdzono expiry, wielu pending, busy/error, terminal state, pełnej pętli Tab ani live policy. |
| Mocki mobile | Session, Profiles, Settings | 390×900 | Mock overflow odpowiednio 691, 780, 588px. Służą do diagnozy granic referencji, nie jako wzorzec pogarszania responsywności aplikacji. |

Łącznie pierwszy harness zapisał 20 widoków/pomiarów i 10 przechodzących kontroli, bez `pageerror`. Kontrole szczegółowe są osobno w JSON; obserwacje F1/F2 są zapisanymi wynikami audytu, nie „failed gate”. Desktop screenshoty są light theme. Nie wykonano nowej pełnej macierzy dark theme, zoom 200%, klawiatury/screen reader dla całego produktu, Linux ani live DB UAT.

## Kolejność ewentualnych poprawek

1. F1: relacje formularza i walidacji — mały, weryfikowalny zakres bez zmiany layoutu.
2. F2: ustalić bezpieczną semantykę dwóch grup A/B, zachowując zatwierdzony wygląd i prawidłowy HTML tabeli; osobny focused keyboard review.
3. F3: dedykowana prezentacja operacji w Session; wymaga świadomego wyboru zakresu widocznych kolumn, nie mechanicznego usuwania funkcji.
4. F4/F5: kosmetyka i treści; po wyborze operatora.
5. F6: porządkować przy pracy w danych sekcjach, bez oddzielnej masowej migracji całego CSS.

## Reprodukcja

Uruchomić istniejący `plans/121-evidence/visual/harness/static-server.mjs` dla `ui/dist` na 48138 i katalogu mocków na 48137. Skopiować trzy zachowane skrypty `mock-audit*.mjs` oraz `../harness/layout-polish-fixtures.mjs` do katalogu z Playwright, np. `D:/temp/sqlharness-visual-tools`. Ustawić `SQLHARNESS_VISUAL_ROOT` na ten checkout i uruchomić skrypty Node po kolei. Nie potrzebują prawdziwego backendu. Zamknąć oba serwery po zakończeniu. Raport nie uruchamia publikacji ani realnego dashboardu.
