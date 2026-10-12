# Compare jako centrum dashboardu

Status: projekt uzgodniony z użytkownikiem, 2026-10-12. Implementacja: [Plan 062](../../../plans/062-compare-context-dashboard.md).

## Cel

Operator ma zobaczyć przebieg optymalizacji SQL przez agenta: kolejne próby, testy fragmentów i bezpośrednio zmierzony wynik całości. SQLHarness nadal oszczędza tokeny i czas budowania harnessu oraz utrzymuje granice credentiali i mutacji.

## Kontekst compare

- Dwa pola: `subject` identyfikuje optymalizowany SQL, `scope` identyfikuje testowany fragment. Dla całości agent używa dokładnie `whole`.
- Przykład: `{ "subject": "orders-report", "scope": "history" }`. Kontekst nie zawiera opisów SQL, wersji ani uzasadnień.
- Globalne ustawienie `compare.requireContext`, domyślnie `true`, niezależne od profilu i ustawienia journala.
- Przy `true`: oba pola są widoczne i wymagane w schemacie MCP oraz wymagane jako `--subject` i `--scope` w CLI. Brak poprawnej pary jest błędem walidacji przed połączeniem z DB.
- Przy `false`: oba pola są całkowicie usunięte ze schematu MCP i nie są akceptowane jako ukryte argumenty. W CLI są opcjonalne; jeśli użytkownik poda jedno, musi podać oba.
- MCP zamraża ustawienie na czas procesu. Zmiana obowiązuje po restarcie serwera i ponownym połączeniu klienta; CLI odczytuje je na początku wywołania.
- Nie ma okresu kompatybilności dla wywołań beta. Starsze rekordy journala pozostają czytelne bez kontekstu.
- Etykiety: 1..64 znaków ASCII, wzorzec `^[a-z0-9][a-z0-9._-]{0,63}$`, rozróżnianie ordinal; nie zmieniać ani nie przycinać wejścia. Agent używa krótkich etykiet bez danych biznesowych i sekretów oraz konsekwentnie ponawia istniejące nazwy.

## Sesja i wersje

- Układ: sesja → `subject` → `scope` → chronologiczne próby compare. Inne operacje pozostają w zakładce Wszystkie operacje. Metadane sesji są w zwijanym panelu.
- W obrębie `(sessionId, subject, scope)` każda nowa treść SQL otrzymuje `v0`, `v1`, itd. według pierwszego wystąpienia hasha; baseline jest pierwszy przy remisie w jednej próbie. Ponownie użyty hash zachowuje numer.
- Baseline/candidate to role w próbie. `v1` może występować po obu stronach. Numer nie oznacza pochodzenia wersji ani wyboru przez agenta.
- Brak hasha oznacza nieznaną wersję. Nie wyliczać tożsamości po podobieństwie SQL, nazwie pliku ani kolejności operacji.
- Kontekst pochodzi od klienta i nie jest zweryfikowanym opisem SQL. Nie wywodzić relacji fragment–całość z hashy.
- Rekordy bez pary kontekstu mają odrębną grupę Bez przypisanego zakresu. Nie zakładać jednego SQL i nie prezentować ich jako `whole`.
- Numeracja opiera się na pełnej zachowanej historii w journalu, przed stronicowaniem i filtrowaniem. Nie zapisujemy osobnego stanu wersji. Po retencji numeracja może się zmienić; UI wyjaśnia, że pokazuje zachowaną historię.

## Nagłówek subject

- Pokazuje najnowszy zakończony compare `scope=whole`, parę wersji i wynik tej konkretnej próby. Zakończone oznacza `succeeded`, `failed`, `rejected` lub `abandoned`; running nie zastępuje zakończonego wyniku. Kolejność według rozpoczęcia, następnie ID.
- Błąd/rejection/abandoned ma odpowiednią etykietę i nie pożycza zielonego wyniku wcześniejszej próby.
- Bez compare całości: Brak końcowego porównania całości. Z samą trwającą próbą całości: Porównanie całości w toku, bez wyniku.
- Poprawa od początku jest prezentowana tylko dla bezpośredniej próby `v0 → wersja kandydata ostatniego zakończonego compare whole`, z jej własnymi pomiarami i warunkami. Nie mnożyć procentów kolejnych prób ani nie sumować popraw fragmentów.
- Nie ma mechanizmu oznaczania wersji wybranej, zwycięzcy, automatycznej rekomendacji ani twierdzenia o poprawności biznesowej.

## Szczegół compare

- Nagłówek: subject / scope / para wersji, link do sesji i właściwej grupy.
- Oddzielne informacje: status wykonania; wydajność (poprawa, regresja, nierozstrzygające lub brak oceny); zgodność obserwowanych wyników (zgodne, różne, nie sprawdzono, sprawdzenie niepełne).
- Wydajność wykorzystuje istniejący werdykt Core z Plan 051. Procent mediany sam nie stanowi werdyktu statystycznego. Nie odtwarzać nowego klasyfikatora w UI.
- Obok siebie baseline, candidate i delta: elapsed, CPU, logical reads/bufory odpowiedniego silnika, grant i spills. Liczba powtórzeń i zakres min–max są widoczne; brak metryki i baseline zero nie dają zmyślonego procentu.
- Dla matrix/parameter sets tabela przypadków zawiera oddzielny wynik, zgodność i metryki każdego przypadku. Nie ukrywać regresji pojedynczej komórki w agregacie. Nazwy i wartości przypadków respektują politykę przechowywania danych w journalu.
- Zakładki: Przypadki, Zmiany SQL (diff domyślnie, pełne wersje dostępne), Plany i IO (obie strony), Warunki pomiaru (profil, rozwiązana baza, vars/parametry jeśli zapisane, setup, repeat, timeout, tryb equivalence, dostępne ustawienia cache i rozrzut).
- Dane nieutrwalone lub historycznie niedostępne są opisane jako niedostępne; nie pobierać nowych pomiarów ani czytać dowolnych ścieżek artefaktów na potrzeby widoku.
- `journal.storeSensitive=false` nadal pozwala pokazać kontekst, hashe i bezpieczne metryki, ale SQL/setup, wartości parametrów i pełne plany pozostają nieutrwalone.
- Tokeny raw/emitted pozostają informacją pomocniczą; nie utożsamiać czasu compare z zaoszczędzonym czasem użytkownika.

## Granice

Brak zmian zasad połączeń, scope DB, sekretów, persistent-mutation classifier i budżetów wyjścia. Nie uruchamiać dashboardu operatora ani jego API podczas pracy agenta: weryfikować przez izolowane fixture/test host i UI. Brak live DB, zmiany konfiguracji użytkownika, instalacji, publikacji czy push w ramach implementacji tego planu. Czytelne SQL i dane nie są zastępowane maskowaniem; obowiązuje obecna polityka przechowywania i credential filtering.
