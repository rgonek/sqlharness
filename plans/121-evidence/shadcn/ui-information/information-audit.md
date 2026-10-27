# Przegląd informacji w dashboardzie

Zakres: Activity, Sessions/Session/Agent session, Operation, Optimization/SQL diff/IO/Plan, Profiles/dimensions, Stats i Settings; przegląd treści w kodzie oraz wizualna kontrola zmienionych ekranów. To nie jest deklaracja, że każda kombinacja błędów i danych została odtworzona w przeglądarce.

## Wprowadzone porządki

| Informacja / element | Decyzja i powód |
|---|---|
| Conversations jako osobna karta | Przeniesione do Details na górze sesji. Identyfikator rozmowy jest diagnostyczny, nie musi poprzedzać wyników pracy. |
| Session ID w podtytule | Przeniesiony do Details; pozostaje dostępny w całości. Tytuł zastępczy nadal może być identyfikatorem. |
| Add note po Optimization | Przeniesione przed Optimization w zwykłej sesji i sesji agenta. |
| Select comparison i tooltip z „default versions and cases” | Usunięte. Każda historia już wyświetla własne porównanie. Przycisk zapisywał kontekst historii w URL i usuwał jawne A/B/case/aggregate/view, pozwalając zastosować wartości początkowe API. Nie wykonywał SQL ani nie wybierał globalnego ustawienia. Jawne wybory A/B i pozostałe kontrolki nadal aktualizują kontekst URL. |
| Powtórzona para A→B nad tabelą IO | Usunięta; pozostaje w nagłówku porównania. |
| CPU/Elapsed/Memory grant/Max used/Spills z samymi kreskami pod każdym statement | Zastąpione jednym krótkim komunikatem o niedostępności dodatkowych metryk statement. Liczba wykonań pozostaje, jeśli znana. Nie usuwamy rzeczywistych danych IO. |
| „SQL only. Metrics live on each compare…” | Usunięte z diffu: nie daje działania i powtarza podział widoku. |
| „Counts respect other filters” | Usunięte; liczby i aktywne filtry nadal widoczne. |
| Długi opis ograniczeń pod Activity | Skrócony do zakresu listy i linków do historii. Pole wyszukiwania nadal wskazuje zakres metadanych. |
| Instrukcja pod grafem planu | Skrócona; zachowana instrukcja zaznaczenia oraz znaczenie linii ciągłej/przerywanej. |
| „No note” w sesji agenta | Usunięte; sam przycisk Add note wystarcza. |

W poprzednim zatwierdzonym zakresie repetition i krótka diagnostyka niedopasowanych fragmentów przeszły do Advanced, a statyczne „Batch SQL stays visible…” i lista powtarzalnych unmatched notices zostały usunięte. Zwykły widoczny RadioGroup planu z etykietami A/B pozostaje zgodny z ostatnim wyborem operatora.

## Informacje, które powinny pozostać

| Obszar | Dlaczego informacja jest potrzebna |
|---|---|
| Conditions differ, basis, equivalence | Bez nich liczby mogą sugerować poprawne porównanie, chociaż warunki lub wyniki są różne albo niezweryfikowane. Jedno zwięzłe miejsce zamiast duplikatów. |
| Mixed results / najgorszy przypadek | Suma poprawy może ukrywać regresję pojedynczego przypadku. |
| Partial attribution, omitted/not stored, unavailable counters | Brak danych nie jest zerem; wpływa na znaczenie sum i dopasowania SQL do planu. |
| Provisional version labels | Ważne przy interpretacji starszej historii i odnośników do wersji. |
| Błędy i retry, loading, zapis/odrzucenie edycji | Użytkownik musi odróżniać brak danych od nieudanego pobrania lub zapisu. |
| Loaded / total, paginacja, zakres czasu | Lista i agregaty mogą obejmować wycinek historii. |
| Profil, baza, zakres porównania, parametry | Określają, co faktycznie zmierzono; nie usuwać w imię czystości layoutu. |
| Exact/inferred/unassigned membership | Sesja agenta może łączyć operacje o różnej pewności przypisania. |
| Retencja, sensitive storage, approvals, restart | Wyjaśniają skutki ustawień, brak zachowanych danych i moment zastosowania zmian. |
| Ceny szacunkowe, źródło cen, niepełne pokrycie kosztu/czasu | Koszt i czas nie powinny wyglądać jak kompletne, dokładne pomiary, jeśli nimi nie są. |
| Limit diffu / bounded preview | Wyjaśnia, dlaczego pełnego diffu nie ma; zachować informację o ograniczeniu. |

## Kolejne uproszczenia do rozważenia

- **Operation / Results:** tekst o niezachowanych komórkach wyników zostawić tylko w pustym Results. Nie dodawać nowej karty z tym samym wyjaśnieniem.
- **Operation / mutation:** pola Rows affected i Estimated z samym „—” są kandydatami do usunięcia, dopóki projekcja ich nie dostarcza. Osobny plan danych 122 obejmuje takie placeholdery; ta poprawka nie zmienia kontraktu backendu.
- **Plan:** wiele ostrzeżeń o omitted/not stored można docelowo zgrupować w licznik i rozwijane szczegóły. Nie ukrywać samego faktu brakującego materiału, jak zrobiliśmy z czysto technicznym unmatched linking.
- **Settings:** tekst „WebAuthn is not available in this plan” to język implementacji. Można zastąpić samym opisem obecnego deliberate click; nie zmieniamy tu polityki zatwierdzania ani zakresu planu 078.
- **Profiles / dimensions:** brak definicji lub brak zmierzonych operacji to przydatny stan pusty; długie objaśnienia modelu agregacji lepiej utrzymywać w jednym opisie lub tooltipie.
- **Identyfikatory i hashe:** pełna wartość w Details, pickerze lub tooltipie, krótka w głównym układzie. Nie skracać informacji potrzebnej do odróżnienia dwóch dokumentów bez dostępu do pełnej wartości.

Zbieranie nowych danych jest osobną decyzją. [Inwentaryzacja danych](../mock-alignment/data-audit.md) rozróżnia dane już zapisane, luki projekcji API i rzeczywiste braki w przechowywaniu. Handoff oceny wartości i trudności pozostaje w `D:/temp/handoff-dashboard-data-value-20261026-223615.md`; nowy plan 122 został przygotowany osobno i nie jest częścią wdrożenia tej poprawki.
