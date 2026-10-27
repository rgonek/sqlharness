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

## Uproszczenia zaakceptowane i wdrożone

Operator zaakceptował poniższe rekomendacje. Ich [wdrożenie i weryfikacja](../audit-recommendations/verification.md) zastępują wcześniejszy status propozycji. Wygląd Plan A/B ma obecnie ponownie być segmentowy, jak Side by side / Unified, zgodnie z najnowszymi screenshotami.

- **Operation / Results:** potwierdzone w przeglądarce: tekst o niezachowanych komórkach wyników występuje tylko w pustym Results. Bez nowej karty z tym samym wyjaśnieniem.
- **Operation / mutation:** usunięte puste Rows affected i Estimated; również Columns zapytania, które nie miało źródła wartości. Rzeczywiste metryki pozostają. Bez zmiany kontraktu backendu i zbierania nowych danych.
- **Plan:** omitted/not stored zgrupowane w widoczny licznik fragmentów oraz rozwijane szczegóły z dokumentem, statementem i pełnymi powodami. Duplikaty manifest/source-map nie zawyżają licznika. Fakt brakującego materiału pozostaje widoczny.
- **Settings:** usunięte „WebAuthn is not available in this plan”; opis wyjaśnia obecne świadome kliknięcie approve/reject. Polityka zatwierdzania bez zmian.
- **Profiles / dimensions:** zachowane stany puste, pokrycie, sumy i ostrzeżenia o brakujących czasach. Objaśnienia agregacji i tożsamości targetu w dostępnych z klawiatury tooltipach shadcn.
- **Identyfikatory i hashe:** kompletne ID sesji/rozmowy oraz hash SQL operacji w Details; krótkie hashe w tabelach Usage i selektorach planu mają tooltip z pełną wartością i powiązaniem dla czytnika ekranu.

Zbieranie nowych danych jest osobną decyzją. [Inwentaryzacja danych](../mock-alignment/data-audit.md) rozróżnia dane już zapisane, luki projekcji API i rzeczywiste braki w przechowywaniu. Handoff oceny wartości i trudności pozostaje w `D:/temp/handoff-dashboard-data-value-20261026-223615.md`; nowy plan 122 został przygotowany osobno i nie jest częścią wdrożenia tej poprawki.
