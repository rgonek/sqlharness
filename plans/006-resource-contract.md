# 006 — Kontrakt zasobów equivalence (porównanie wyników)

Cel: jawny budżet całej operacji porównania wyników (`compare`), wiążący
dla implementacji (T3) i capabilities (T5). Status: zdefiniowany, do
wyegzekwowania. Baza: `df0e2bf`. Ruling R2 z ledgera: niniejszy dokument
wiąże zadania T3/T5 — liczby i reguły poniżej muszą zostać zaimplementowane
dokładnie tak, jak zapisano.

## 1. Koperta per-run (istniejąca, zachowana)

`CanonicalComparisonAccumulator.MaximumComparedRows == 1_000_000` wierszy
na jeden mierzony przebieg. Przekroczenie rzuca `SqlHarnessSafetyException`
(fails closed) — nigdy nie obcina danych.

## 2. Budżet całkowity per cell (nowy, do wyegzekwowania w T3)

`MaxUniqueComparisonFingerprintsPerCell == 2_000_000` (2 × limit per-run).
Liczy UNIKALNE przechowywane fingerprinty w obrębie jednej komórki compare
(baseline + candidate, wszystkie mierzone przebiegi). Współdzielone
reprezentacje identycznych przebiegów liczone raz — deduplikacja jest
premiowana, nie karana. Wpisy histogramu mieszczą się w tym samym liczniku
(co najwyżej jeden wpis na unikalny fingerprint).

## 3. Rozliczenie

Każdy mierzony przebieg z `captureComparison == true` rejestruje swoje
wiersze w budżecie komórki. Warm-up (repetition 0) i tryb
`ResultComparisonMode.Off` nie rejestrują nic (nie przechowują fingerprintów).

## 4. Odmowa bez obcinania

Wyczerpanie budżetu rzuca `SqlHarnessSafetyException`. Komunikat zawiera
słowa `comparison budget` oraz obie liczby: użyte i limit. NIGDY nie zwraca
`Equivalent == true` na obciętych danych — odmowa = wyjątek, brak raportu
(fail closed).

## 5. Prezentacja vs equivalence

Limity prezentacji (`--max-rows` query, budżety wyjścia agenta/MCP) tną
wyłącznie wyświetlanie; nigdy nie zasilają ścieżki equivalence. Equivalence
zawsze widzi pełne capture do limitu per-run.

## 6. Matrix

Budżet jest resetowany per cell — każda wartość macierzy to świeży budżet
`2_000_000`. Ukończone komórki zachowują artefakty przy błędzie późniejszej
komórki (istniejące `CompareMatrixCellFailedException.PartialReport`);
pierwszy błąd zatrzymuje run.

## 7. Deadline / anulowanie

Token anulowania przekazany do `CompareCellRunner.RunAsync` / `ResultComparer`
MUSI być obserwowany w fazach CPU (pętle par, budowa histogramów)
w ograniczonych odstępach pracy. Anulowanie = `OperationCanceledException`.

## 8. Liczby w capabilities (zapowiedź T5)

Capabilities ogłaszają: `comparisonRowCapPerRun = 1000000`,
`comparisonUniqueFingerprintBudgetPerCell = 2000000`.

## 9. Górny zakres wejść

`repeat` do 100 → do 200 mierzonych przebiegów i do 10 000 par
baseline×candidate to DOZWOLONY zakres wejść, nie gwarancja ukończenia —
budżet może odmówić wcześniej (reguła 4).
