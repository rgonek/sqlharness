# Mocki a dane dashboardu — 2026-10-26

Aktualizacja decyzji UI: użytkownik wybrał standardowe widoczne radio A/B, repetition w Advanced, krótką diagnostykę linkowania wyłącznie w Advanced oraz usunięcie wskazówki Batch SQL. Stan opisany w sekcji 3 poniżej jest historią poprzedniej iteracji; [nowsza implementacja](../plan-options/verification.md) go zastępuje. Macierz danych pozostaje aktualna.

Zakres: zatwierdzone Activity 3a, Operation v2, Session Optimization 4a i Settings, uruchomione z oryginalnym support.js. Mira/Zinc, Inter i Hugeicons pozostają aktualnym systemem wizualnym. Ten dokument rozdziela brak danych od braku projekcji API i od różnic prezentacji. Nie dodano nowego zbierania metryk ani tabel journala.

## Istniejące raporty zamiast odtwarzania ustaleń

Punktem wyjścia są [raport implementacji, Evidence boundaries](../../implementation.md) i [design QA, Expected differences and follow-up polish](../../../../design-qa.md). Wprost odnotowano tam pominięcie SQL preview/search w Activity, komórek wyników query, metryk per statement i części liczników IO; brak sztucznej etykiety Best oraz zachowanie zatwierdzeń w Mutations. Historyczne stwierdzenie, że CPU/elapsed per statement nie są zapisywane w journalu, wymaga korekty: aktualny journal je przechowuje.

## 1. Co mock pokazuje, a implementacja pominęła

| Element mocka | Co rzeczywiście jest dostępne teraz | Gdzie jest luka / co wymaga pracy |
|---|---|---|
| Podgląd SQL w wierszu Activity i wyszukiwanie po treści SQL | SQL jest zapisywany w operacji, gdy storeSensitive jest włączone; dostępny w detailu / wersjach SQL. Activity wykorzystuje label/subject, nie SQL. | **Projekcja i wyszukiwanie API**, a nie całkowity brak zbierania SQL. Potrzebny ograniczony preview i wyszukiwanie z jasno określoną paginacją. Starsze rekordy bez SQL nadal pozostaną bez preview. |
| Tabela wyników query: nazwy kolumn i wartości komórek | Journal operacji przechowuje liczbę result setów i zwróconych wierszy; odpowiedź query i ewentualne artefakty są osobnym źródłem. | **Brak komórek/schematu wyników w kontrakcie historii dashboardu**. Potrzebna integracja z zapisanym artefaktem albo ograniczona retencja wyników. Sam komponent tabeli nie wystarczy. Nie dotyczy tylko kosmetyki UI. |
| Liczba kolumn query w pasku KPI | OperationPage pokazuje obecnie „—”; OperationEnd nie ma dedykowanego column count. | **Brak projekcji trwałego metadatum wyników**. Można rozważyć samo column count/schemat bez przechowywania komórek; nie należy utożsamiać tych dwóch rozszerzeń. |
| CPU i elapsed poszczególnych statementów | **Już zbierane i zapisywane** jako cpu_ms / elapsed_ms w operation_statements. Dostępność zależy od planu/pomiaru. | **Brak w dashboardowym StatementRow / CompareStatementEvidence i agregacji porównania**. Nie wymaga rozpoczęcia zbierania od zera. Trzeba określić semantykę A/B, powtórzeń i sumowania case’ów. Poprawiono mylący tooltip w UI. |
| Memory grant, max used i spills per statement | Journal ma grant/spills na poziomie wariantu/batcha. Ustrukturyzowany rekord statementu nie ma tych pól. Surowy zapisany plan może zawierać część informacji. | **Brak normalizacji i trwałego kontraktu per statement**. Najpierw sprawdzić ekstrakcję z zachowanych planów; nowe wykonania są potrzebne tam, gdzie materiału nie zapisano. Nie kopiować wartości batcha do każdego statementu. |
| Dodatkowe liczniki IO per statement | Logical reads, LOB logical reads i scans mają reprezentację statement/object. Fizyczne reads, read-ahead, LOB physical/read-ahead są dostępne w agregatach batcha, nie w tym samym kontrakcie statement/object. | Rozdzielić **brak projekcji LOB logical** od **braku pozostałych ustrukturyzowanych liczników per statement**. Obecne dodatkowe kolumny statementowe pozostają „—”; nie dzielić arbitralnie sum batcha. |
| Rows w benchmarku, obok reads/CPU/scans | Dostępne jest op.rowsReturned, jeśli je zapisano; rekord wariantu benchmarka nie dostarcza osobnego licznika rows per wykonanie/statement. | **Dostępność zależna od operacji**. Nie traktować ActualRows pojedynczego operatora planu jako liczby zwróconych wyników. Mockowe 118 nie stanowi kontraktu. |
| Mutation: Rows affected i Estimated w KPI | Obecny OperationPage pokazuje „—”; OperationDetail nie dostarcza typowanych pól tych dwóch wartości. Broker i execution mają osobne kontrakty. | **Luka widoku historii / powiązania z wykonaniem i preflightem**. Nie oznacza, że silnik nie zna affected rows. Przed rozszerzeniem retencji trzeba ustalić źródło i znaczenie „Estimated” oraz zachowanie dla wielu instrukcji. |
| Szczegółowa oś akceptacji i historyczne ustawienia policy w zakładce Approval | Lifecycle akceptacji i tryb żądania mają istniejący journal / widok Mutations. Zakładka operacji kieruje do Mutations. | **Brak powiązanej prezentacji w detailu operacji**, a nie brak całego backendu akceptacji. Pełnego historycznego snapshotu timeout/verification z mocka nie należy zastępować dzisiejszą konfiguracją. |
| Zielone Best przy wersji | Są metryki i wyniki case’ów, ale brak zdefiniowanego, wiarygodnego rankingu Best w tym kontrakcie. | **Brak reguły produktu/polityki**, nie brak nowego licznika. Najprościej pozostawić bez Best. Najmniejsze reads nie gwarantuje najlepszego wyniku przy regresjach czasu lub różnych warunkach. |
| Wszystkie wersje w wyborze planu, także niebędące A/B | Historia wersji jest dostępna. Aktualny wybór planu używa dokładnych beforeAction/afterAction i mapowania case’ów dla A/B. | **Ograniczenie nawigacji/API**, nie brak zbierania planów. Inne wersje można ustawić jako A/B w tabeli. Nie dodano pozornego v1 bez pewnego mapowania wersja → operacja → strona → case. |

Źródła aktualnej weryfikacji: [OperationStart/End i mutation metadata](../../../../src/SqlHarness.Core/Journal/JournalModels.cs), [rekordy benchmarka i statementów](../../../../src/SqlHarness.Core/Journal/BenchmarkJournalRecord.cs), [budowanie statementów](../../../../src/SqlHarness.Core/Journal/JournalBenchmarkBuilder.cs), [schemat operation_statements](../../../../src/SqlHarness.Core/Journal/JournalSchema.cs), [liczniki obiektów](../../../../src/SqlHarness.Core/StatementMetrics.cs), [projekcja dashboardu](../../../../src/SqlHarness.Dashboard/JournalReader.cs), [modele porównania](../../../../src/SqlHarness.Dashboard/Optimization/OptimizationModels.cs), [typy UI](../../../../src/SqlHarness.Dashboard/ui/src/api/types.ts), [OperationPage](../../../../src/SqlHarness.Dashboard/ui/src/pages/OperationPage.tsx), [IO](../../../../src/SqlHarness.Dashboard/ui/src/components/optimization/IoComparison.tsx), [Activity](../../../../src/SqlHarness.Dashboard/ui/src/components/ActivityOperations.tsx).

Nie jest brakiem danych: inna liczba operatorów grafu, inne tabele/liczby/SQL/ID, brak przykładowych 118 wierszy, brak wyliczonego z mocka Best ani dostępność zależna od storeSensitive, journala, obsługi silnika czy retencji. Nie uzupełniamy tego wartościami demonstracyjnymi.

## 2. Dodatki aplikacji: co zachować, co uprościć

| Element | Ocena i proponowane uproszczenie |
|---|---|
| `v69 → v70 · measured together … · results equivalent …` | **Ważne; występuje również w wybranym mocku 4a.** Mówi, co i jak porównano oraz czy wyniki były sprawdzone. Przeniesiono z dodatkowego pasa do nagłówka tabów, zgodnie z mockiem. Nie usuwać informacji o różnych uruchomieniach ani nieweryfikowanych/różnych wynikach. Samą parę wersji można później ograniczyć do jednego miejsca, jeśli A/B są jednoznacznie widoczne. |
| Puste `· ·` | **Błąd, nie dodatkowe dane.** PairBasis był liczbą w JSON, a UI oczekiwało nazwy. Naprawiono enum-specific serializer i pomijanie pustych segmentów. To wpływa również na ConditionsDiffer, więc samo usunięcie tekstu zamaskowałoby problem. |
| Powtórzenia pary wersji w tytule, kontekście i IO | **Można łatwo odchudzić prezentację**. Pozostawić parę raz przy porównaniu; etykiety kolumn A/B i wybór planu nadal muszą być jednoznaczne. To nie usuwa danych backendowych. |
| Zawsze widoczny tekst „Batch SQL stays visible…” | **Niski priorytet; łatwy kandydat do usunięcia z głównego widoku / przeniesienia do pomocy.** To wskazówka interakcji, nie wynik pomiaru. Nie usunięto jej bez wyboru zakresu uproszczeń. |
| „SQL only. Metrics live on each compare…” | **Warunkowo przydatne.** Pokazywać przede wszystkim, gdy parę składa się z oddzielnych pomiarów; dla zwykłego bezpośredniego compare można skrócić/pominąć. |
| Case / Repetition / Plan document | **Ważne, gdy jest więcej niż jeden wybór.** Repetition już znika przy pojedynczej wartości. Można analogicznie schować pojedynczy case/dokument, zachowując opis wybranego źródła. Nie usuwać rozróżniania wielu case’ów i dokumentów tylko dlatego, że mock pokazuje prosty przykład. |
| Puste CPU/elapsed/grant/max used/spills w każdym rozwinięciu statementu | **Można łatwo zwinąć w jeden komunikat o niedostępnych dodatkowych metrykach.** Wartości dostępne, w tym executions, muszą pozostać. CPU/elapsed warto następnie udostępnić z istniejącego journala. |
| Fallback attribution, omitted statements, hidden work/UDF, mixed regression, provisional versions | **Ważne; zachować widoczne.** Zmieniają interpretację wyników i kompletność dowodów. Krótszy tekst lub spójny Alert jest możliwy; całkowite ukrycie byłoby mylące. |
| „Comparison evidence and scripts”, identyfikatory operacji, provenance i kopiowanie | **Przydatne, ale drugoplanowe.** Już są zwinięte. Można skrócić duplikaty lub schować rzadkie akcje w menu, zachowując możliwość odtworzenia wykonania. |
| Engine node id, table reference id, source-match rule, pełne properties | **Diagnostyczne; zostawić w inspektorze po kliknięciu operatora.** Nie muszą obciążać głównego widoku grafu. |
| Loaded/shown counts i matching-only wyjaśnienia | **Ważne dla prawdziwej historii z paginacją.** Mock ma skończone demonstracyjne rekordy; usunięcie kwalifikatorów sugerowałoby, że częściowa lista to całość. Można skrócić tekst, zachować znaczenie. |
| Typografia Mira, Inter/Hugeicons, czterolinkowa nawigacja | **Celowo zachowane decyzje operatora**, nie brak zgodności do cofnięcia do starszego mocka Session. |

Rekomendowana kolejność dalszych uproszczeń: usunąć stałe wskazówki z głównego widoku; zredukować powtórzenia pary wersji; ukryć selektory bez realnego wyboru; zwinąć nieobecne metryki statementu. Nie usuwać równoważności wyników, podstawy porównania ani ostrzeżeń o kompletności.

## 3. Co poprawiono w tej iteracji

- Segmentowy RadioGroup dla diff pozostaje zgodnie z odpowiedzią operatora.
- Wybór planu A/B jest teraz pojedynczym RadioGroup z szarym tłem grupy, wybranym segmentem i Badge A/B; B ciemne, A szare. Grupa ma 28px, a selektory planu mieszczą się obok niej na desktopie.
- Taby zachowują shadcn Tabs `variant="line"`. Zmieniono wyłącznie kompozycję i klasy użycia: szerokość tekstu, 44px wiersz, 14px etykiety, podkreślenie przy linii sekcji, kontekst po prawej.
- Selektory All cases / aggregate i Copy summary wróciły do nagłówka Optimization nad tabelą, jak w mocku; tooltip Select comparison pozostaje.
- Naprawiono tekstowy kontrakt PairBasis i mylący opis niedostępnych metryk. Bez nowego zbierania danych.

Źródła shadcn: [Tabs](https://ui.shadcn.com/docs/components/base/tabs), [RadioGroup](https://ui.shadcn.com/docs/components/base/radio-group). Pliki `components/ui/*` pozostały bez zmian. [Galeria porównań](index.html) dokumentuje renderowane mocki, stan przed/po i mobilny Plan.
