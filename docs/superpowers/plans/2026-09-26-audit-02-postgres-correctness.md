# PostgreSQL correctness audit Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Usunąć fałszywe odrzucenia zapytań i połączeń oraz błędne metryki PostgreSQL.

**Architecture:** Parametry i tożsamość należą do silnika; metryki zachowują jawną semantykę i dostępność. Nowa polityka TLS ma migrację zgodną z istniejącymi profilami.

**Tech Stack:** .NET 8, Npgsql, SqlParserCS, xUnit.

**Spec:** [audyt B1–B3 i S5](../specs/2026-09-26-project-audit.md), [roadmapa](2026-09-26-audit-roadmap.md), istniejąca specyfikacja `2026-09-10-postgres-engine-design.md`.

## Global Constraints

- PostgreSQL 14+; oba silniki i istniejące profile muszą zachować obsługę.
- Historyczna specyfikacja świadomie nie miała sslMode. T2 jest zmianą kontraktu, nie cichą reinterpretacją trustServerCertificate.
- Wyniki equivalence pochodzą z sidecara; nie przedstawiać ich jako wierszy zwróconych przez EXPLAIN.
- Nie tłumaczyć natywnego PostgreSQL na T-SQL ani nie usuwać sprawdzania celu.

## Review Focus

- @param w komentarzu, stringu i dollar-quoted literal: T1.
- Parametr użyty tylko w setupie lub w jednym wariancie: T1.
- DNS, IPv6, proxy, zmiana adresu i niewłaściwa baza: T2.
- Niezaufany certyfikat i sprzeczna konfiguracja TLS: T2.
- Plan bez buforów, plan równoległy, ułamkowe ms: T3.

## T1 — referencje parametrów właściwego dialektu

**Files:** `src/SqlHarness.Core/Dialect/ISqlDialect.cs`, `Dialect/SqlServerDialect.cs`, `Postgres/PostgresDialect.cs`, `SqlSafety.cs`, `SqlHarnessModule.cs`, `MeasureParameterSets.cs`; nowy `Postgres/PostgresParameterReferenceValidator.cs`; `tests/SqlHarness.Tests/SqlParameterReferenceValidatorTests.cs`, `Postgres/PostgresParameterTests.cs`, `MeasureParameterSetValidationTests.cs`.

**Interfaces:** dodać do dialektu `void ValidateParameterReferences(IReadOnlyList<SqlHarnessParameter> parameters, params string?[] batches)`. Wszystkie komendy i walidacja param-set wywołują ten sam kontrakt. T-SQL deleguje do obecnego walidatora; PostgreSQL korzysta z natywnej analizy, zgodnej ze sposobem bindowania Npgsql.

- [ ] Testy pozytywne `SELECT @n::int`, `SELECT @n LIMIT 1`, JSON operators, dollar quoting; testy negatywne dla rzeczywiście nieużytego parametru i błędnej składni.
- [ ] Testy komentarzy/stringów, powtórnych referencji, setup-only i wielu zestawów. Nazwy parametrów rozpoznawać zgodnie z binderem, bez wykrywania regexem wewnątrz literalów.
- [ ] Uruchomić testy i potwierdzić obecne fałszywe odmowy, wdrożyć routing we wszystkich query/measure/compare/matrix/watch/snapshot/param-set.
- [ ] Współdzielić sparsowany dokument tam, gdzie bezpieczeństwo i benchmark analizują ten sam SQL; jeśli wymaga to większej zmiany, ograniczyć T1 do poprawnego routingu, a konsolidację wykonać w 05/T1.
- [ ] Uruchomić wskazane testy i PostgresQueryTests/PostgresBenchmarkTests; lokalny commit `fix: validate parameter references with the selected dialect`.

## T2 — TLS i tożsamość połączenia

**Files:** `Targets/TargetProfile.cs`, `Targets/TargetResolver.cs`, `Contracts.cs`, `Postgres/PostgresConnectionString.cs`, `Postgres/NpgsqlSessionFactory.cs`, `SqlExecution.cs` pod `src/SqlHarness.Core`; `docs/example-targets.json`, `README.md`, `AGENTS.md`; `tests/SqlHarness.Tests/Postgres/PostgresConnectionStringTests.cs`, `Targets/ProfileStoreTests.cs`, `SqlExecutionTests.cs`; nowa spec `docs/superpowers/specs/2026-09-26-postgres-transport-policy.md`.

**Interfaces:** planować opcjonalne `sslMode` i `rootCertificate` w profilu PostgreSQL; wartości trybu `verify-full`, `verify-ca`, `require`, `disable`. Sekret hasła nadal wyłącznie w env. Szczegóły mapowania do ResolvedTarget zapisać w spec przed zmianą rekordu.

- [ ] Zapisać macierz migracji: brak nowych pól zachowuje stary tryb i raportuje legacy policy; jawny sslMode jest autorytatywny, konfiguracja sprzeczna z trustServerCertificate odrzucana; rootCertificate tylko z trybem weryfikującym. Nowe przykłady połączeń zdalnych używają verify-full.
- [ ] Testy buildera: VerifyFull, CA, Require, Disable, brak/nieczytelny CA, sprzeczność i profil SQL Server z polami PG. Nie odczytywać hasła do komunikatów błędów.
- [ ] Rozdzielić sprawdzanie tożsamości silników. Dla PG zawsze sprawdzić database; tożsamość endpointu powiązać z faktycznie zestawionym i zweryfikowanym połączeniem, nie równością DNS z inet_server_addr. Zapisać politykę proxy/loopback i trybów nieweryfikujących w spec; nie traktować samego DNS jako uwierzytelnienia.
- [ ] Testy jednostkowe na kontrolowanym resolverze/endpoint metadata: DNS→IP przechodzi zgodnie z polityką, inna baza zawsze odpada, inny uwierzytelniony host odpada. Bez sieci w testach domyślnych.
- [ ] Opt-in test TLS na jednorazowym środowisku: zły host, niezaufany CA, prawidłowy CA oraz połączenie bez TLS. Wymaga autoryzowanego celu, nie korzysta z profili użytkownika.
- [ ] Zaktualizować dokumentację migracji i uruchomić wskazane testy; osobny lokalny commit. Nie zmieniać historycznej specyfikacji bez adnotacji o zastąpieniu polityki.

## T3 — poprawne, jawne metryki

**Files:** `src/SqlHarness.Core/Postgres/PostgresBenchmark.cs`, `Postgres/PostgresPlanDistiller.cs`, `Artifacts.cs`, `BenchmarkSummary.cs`, `MeasureParameterSets.cs`; `src/SqlHarness.Cli/Commands/Renderer.cs`; `tests/SqlHarness.Tests/Postgres/PostgresBenchmarkTests.cs`, `BenchmarkSummaryTests.cs`, `MeasureParameterSetReportTests.cs`.

**Interfaces:** `PostgresBenchmark.ParseStats(string)` zachowuje całkowite logicalReads = shared/local hits+reads korzenia. Stare pola long pozostają kompatybilne; precyzyjny czas i dostępność CPU dodać jako jawne metadane raportu i przenieść do summary, według kontraktu 03/T1.

- [ ] Test root=10, child=10 → total=10; dodanie warstwy Aggregate nie zmienia total. Dodać fixtures z local buffers, równoległością i brakującymi metrykami.
- [ ] Oddzielić globalne bufory od diagnostyki per-relation; opisać źródło i brak addytywności, zachować schema-qualified relacje jeśli dostępne. Nie sumować workerów drugi raz.
- [ ] Zachować ułamkowy czas w dodatkowej metryce; 0,3 ms nie może być prezentowane jako zmierzone dokładne 0. CPU PG oznaczyć unavailable; cross-set CPU nie może wskazywać zwycięzcy na podstawie samych zer zastępczych.
- [ ] Opisać planning/execution/sidecar i ostrzeżenia w maszynowym kontrakcie. Pełne i skrócone odpowiedzi muszą przekazywać tę samą dostępność metryk.
- [ ] Uruchomić wskazane testy oraz CompareTests i MeasureTests, zapisać lokalny commit `fix: report PostgreSQL benchmark metrics without double counting`.
