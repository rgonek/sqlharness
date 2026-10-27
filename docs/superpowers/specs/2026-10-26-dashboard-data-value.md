# Dashboard: użyteczne dane nowych sesji

Status: zakres wybrany przez operatora 2026-10-26; implementacja wszystkich sześciu zadań autoryzowana 2026-10-27. Końcowe dowody w `plans/122-evidence/verification.md`.

## Cel i źródła

Udostępnić istniejące dowody przydatne w optymalizacji i kontroli wykonania, następnie wybrane metryki z planów. Podstawa: audyt `plans/121-evidence/shadcn/mock-alignment/data-audit.md` z commita `3eff7808` (gałąź `dashboard-shadcn-fixes`; materiał źródłowy nie jest jeszcze na main), ocena w rozmowie i korekta operatora: „Nie robimy żadnego backfillu, ewentualne zmiany tylko dla nowych sesji” oraz „Co do reszty UX to na razie nic nie robimy”.

## Zakres do wykonania

1. CPU/elapsed per statement oraz LOB logical reads per statement/object z istniejącego źródła.
2. SQL preview Activity, maksymalnie 240 znaków Unicode; bez SQL search.
3. Rzeczywiste affected rows i istniejący approval lifecycle w detailu mutation.
4. SQL Server: requested/granted/max-used grant oraz spill warning per statement z materiału bieżącego wykonania, bez dodatkowego SQL i kopii raw planów.
5. Usunięcie pól UI bez planowanego źródła: Physical, Read-ahead, LOB physical i LOB read-ahead w By statement; query Columns; mutation Estimated; benchmark Rows bez kontraktu właściwego dla benchmarku. Zachować rzeczywiste dane Whole batch, query Rows i affected rows.

## Nowe sesje: ścisła granica

Sesja oznacza rekord journala `sessions` z `session_key`, nie grupę Optimization ani otwarcie karty. Dodać nullable `sessions.dashboard_evidence_version`. Istniejące rekordy pozostają NULL. Tylko INSERT nowej sesji przez nowy program zapisuje 1. Upsert/wznowienie starego klucza i nowa operacja w starej sesji nie ustawiają markera. Restart sam w sobie nie dowodzi powstania nowej sesji.

Marker steruje nowymi projekcjami i zapisami. Stare operacje pozostają na dotychczasowym kontrakcie nawet wtedy, gdy zapisane CPU/plan pozwalałyby wydobyć więcej. Przy mieszanym porównaniu starej i nowej operacji brak nowej wartości jednej strony nie staje się zerem ani kompletną deltą. Usunięcie bezużytecznych placeholderów jest globalną zmianą prezentacji, bez zmiany historycznych danych.

Dozwolony addytywny DDL: nullable kolumny/nowe tabele. Zakazane: UPDATE historycznych rekordów, rekonstrukcja, odczyt starych artefaktów dla nowych pól, leniwy backfill przy GET i nowe skanowanie historii. Test migracji ma potwierdzić niezmienione wartości starych danych. Kolejne rozszerzenia wymagają nowej wersji markera, bez reinterpretowania zapisanej wersji.

## Kontrakty znaczeniowe

- CPU/elapsed pochodzą z runu wybranego przez `SelectMedianTotalRun` według logical reads; nie są medianą czasu/CPU. Pokazać repetition i istniejący dokładny link planu. Nie zmieniać domyślnego repetition w Plan.
- Nowe metryki statementów: pojedynczy case. W All cases jawna niedostępność, bez arbitralnego sumowania czasów/grantów i bez częściowego agregatu. Istniejące metryki batcha bez zmian.
- LOB logical nie dolicza się ponownie do total reads. Zachować object identity, occurrence i kompletność.
- Granty pochodzą z QueryPlan konkretnego statementu. Pierwsza wersja spills to nullable boolean `HasSpillWarning`, nie suma niejednorodnych liczników/objętości. True oznacza znalezione ostrzeżenie; false wymaga kompletnego obsługiwanego materiału; w pozostałych przypadkach NULL. Nie kopiować batch grantów na statementy.
- Affected rows: `SqlHarnessQueryReport.RecordsAffected` po zakończeniu wykonania, jako wartość providera dla całego polecenia. -1/niedostępność to NULL. Nie deklarować liczby unikalnych rekordów ani rozbicia per statement. Unknown outcome nie jest potwierdzonym skutkiem.
- Approval: wyłącznie zapisane zdarzenia i dokładna relacja request → operation. Bez dopasowania po czasie/SQL; aktualne settings nie zastępują historycznego snapshotu.
- Preview respektuje `storeSensitive`, ma limit 240 znaków Unicode i jawne oznaczenie ucięcia. Dla compare preview baseline; pełny before/after w detailu. Nie zwiększać zakresu zapisu SQL ani maskowania treści.
- Showplan CPU/LOB/grants/spills nie mają pozornego odpowiednika PostgreSQL. Preview, affected rows i lifecycle wspólne tam, gdzie provider dostarcza dowód.

## Odłożone i wyłączone

Bez schema/count kolumn, komórek wyników, nowych benchmark rows, SQL search, nowych physical IO statementów, Estimated affected rows, pełnego snapshotu policy, Best i dowolnych wersji planu. Podjęcie ich wymaga osobnego wyboru zakresu. Nie dodawać pustych tabel wyników; obecny komunikat Results o braku retencji pozostaje.

Bez pozostałych zmian UX: Advanced, selektorów Plan, popupu dokumentów, repetition, unmatched, tekstu Batch SQL, deduplikacji pary wersji i wyglądu A/B. Nie zmieniać `components/ui/*`. Zachować conditions differ, równoważność, missing/omitted evidence, hidden work, mixed regression i paginację.

Bez live DB, samodzielnego approval, zmiany mutation policy/classification, instalacji, restartu, merge i push. Parser korzysta z już uzyskanego materiału w obecnych limitach; zapisuje małe scalare i provenance. Nie powiela planów. Offline narzut nie jest live acceptance.
