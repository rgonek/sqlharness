# SQL safety audit remediation Implementation Plan

**Status (2026-10-08):** DONE — T1 f75a25d/07bbb0f/0cfd7a4, T2 c900888, T3 044c0c7/bcb9e69/222ca22/bba0a9d, T4 4e31382. Checkboxy poniżej nie były odhaczane w trakcie wykonania.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Uszczelnić rozpoznawanie trwałych zapisów, redakcję wejścia i kontrolę celu; ustalić wykonalną politykę funkcji PostgreSQL.

**Architecture:** Naprawić istniejącą granicę bezpieczeństwa przed większą refaktoryzacją. Core jest autorytetem dla celu i dopuszczalnych efektów SQL; CLI tylko przekazuje żądanie.

**Tech Stack:** .NET 8, ScriptDom, SqlParserCS, Npgsql, xUnit.

**Spec:** [audyt S1–S4, S6 i duplikacja targetu](../specs/2026-09-26-project-audit.md), [zasady programu](2026-09-26-audit-roadmap.md).

## Global Constraints

- Obowiązują wspólne zasady roadmapy; oba silniki pozostają wspierane.
- Bez niejawnej zgody na mutacje, trwałego DDL i dowolnego wykonywania funkcji uznanego za read-only wyłącznie po kształcie SELECT.
- S4 jest luką pokrycia AST do sprawdzenia, nie potwierdzoną podatnością serwera.
- Naprawy OUTPUT i redakcji nie czekają na przebudowę całego modułu.

## Review Focus

- Alias o nazwie #temp wskazujący trwałą tabelę: T1.
- Drugi cel zapisu w OUTPUT i różne bazy: T1.
- Błędna wartość z InnerException, częściowo poprawna lista parametrów: T2.
- Funkcja ukryta w widoku, operatorze lub źródle tabelowym: T3.
- Bezpośrednie wywołanie API Core omijające walidację CLI: T4.

## T1 — wszystkie cele zapisu SQL Server

**Files:** `src/SqlHarness.Core/SqlSafety.cs`; `tests/SqlHarness.Tests/SqlSafetyTests.cs`, `QueryTests.cs`, `MeasureTests.cs`, `CompareTests.cs`.

**Interfaces:** zachować `SqlSafetyClassifier.Classify(...) -> SqlSafetyDecision`; `HasMutation` ma opisywać cały batch, a `HasSessionLocalWork` rzeczywiste obiekty, nie nazwy aliasów.

- [ ] Dodać testy: `TempInsertWithPersistentOutputRequiresApproval`, `HashAliasOfPersistentTableRequiresApproval`, `AliasOfActualTempRemainsLocal`. Sprawdzić UPDATE i DELETE, quoted alias, alias niejednoznaczny oraz INSERT/UPDATE/DELETE/MERGE z OUTPUT INTO.
- [ ] Uruchomić `dotnet test --filter 'FullyQualifiedName~SqlSafetyTests'`; nowe przypadki trwałego zapisu mają ujawnić obecny błąd.
- [ ] Rozwiązywać cel względem FROM/aliasów i zbierać wszystkie cele zapisu. Niejednoznaczny cel odrzucać. Trwały OUTPUT wymaga zgody w query; w setup/measure/compare pozostaje niedozwolony. Cross-database pozostaje niedozwolone także ze zgodą.
- [ ] Sprawdzić pełną ścieżkę modułu: odmowa przed ConnectAsync, brak artefaktu sukcesu, poprawny exit 2. Zgoda i dokładna nazwa bazy dopuszczają tylko kontrakt dozwolonego DML.
- [ ] Uruchomić testy SqlSafetyTests, QueryTests, MeasureTests, CompareTests; zapisać wyniki i lokalny commit `fix: classify all SQL Server write targets`.

## T2 — redakcja przed parsowaniem

**Files:** `src/SqlHarness.Core/SqlSafety.cs`, `SecretRedactor.cs`, `SqlHarnessModule.cs`, `MeasureParameterSets.cs`; `tests/SqlHarness.Tests/SecretRedactorTests.cs`, `SqlParameterParserTests.cs`, `QueryTests.cs`, `WatchTests.cs`, `SnapshotTests.cs`, `CompareTests.cs`, `MeasureTests.cs`.

**Interfaces:** zachować `SecretRedactor.Redact(Exception, IReadOnlyList<string>) -> string`; bezpieczny błąd walidacji nie może odziedziczyć wartości z wyjątku wewnętrznego.

- [ ] Dodać testy z `n:int=private-audit-value`: wynik błędu żadnej z pięciu komend nie zawiera wartości ani pełnej deklaracji; nazwa parametru i oczekiwany typ pozostają użyteczne.
- [ ] Rozszerzyć przypadki o decimal, datę, GUID, Base64, Unicode, wartości nakładające się i błąd drugiego parametru po poprawnym pierwszym. Nie używać rzeczywistych sekretów.
- [ ] Oddzielić wyjątki diagnostyczne od komunikatów publicznych i zebrać wartości do redakcji przed konwersją; ujednolicić zwykłe parametry, matrix i param-set. Nie publikować inner exception jako tekstu użytkownika.
- [ ] Uruchomić wymienione klasy testów filtrem `FullyQualifiedName~...`; sprawdzić stdout i zapisane raporty/artefakty błędów.
- [ ] Zapisać wynik oraz lokalny commit `fix: redact invalid parameter values before parsing`.

## T3 — model zagrożeń i polityka PostgreSQL

**Files:** `docs/superpowers/specs/2026-09-26-postgres-safety-policy.md` (nowy); `src/SqlHarness.Core/Postgres/PostgresSafetyClassifier.cs`, `NpgsqlSessionFactory.cs`; `tests/SqlHarness.Tests/Postgres/PostgresSafetyTests.cs`; nowy `tests/SqlHarness.Tests/Integration/PostgresSafetyIntegrationTests.cs` po ustaleniu polityki.

**Interfaces:** najpierw specyfikacja polityki; nie dodawać pustych interfejsów. Spec ma określić zachowanie Classify i zakres dozwolonego wykonania sesji dla Query oraz CompareSetup.

- [ ] Zapisać threat model: agent z dostępem do CLI, istniejące funkcje/widoki/operator overloads, uprawnienia konta, search_path i rozwiązywanie nazw. Rozdzielić brak trwałego DML, brak efektów administracyjnych i brak dostępu zewnętrznego.
- [ ] Zdefiniować macierz: set_config, pg_cancel_backend, pg_terminate_backend, advisory locks, sequence functions, funkcja VOLATILE wykonująca DML, SECURITY DEFINER, funkcja w widoku i funkcja o nazwie lo_ bez efektów. Każda pozycja ma jawny dozwolony efekt, sposób egzekwowania i komunikat odmowy.
- [ ] Porównać pełną analizę AST, zaufane funkcje po rozstrzygnięciu przeciążeń, minimalne uprawnienia i transakcje read-only. Nie opierać obietnicy bezpieczeństwa na samej denyliście ani na samej transakcji. Nie zmieniać ról/uprawnień bazy automatycznie.
- [ ] Dodać offline testy rekursji AST z CTE, INSERT source i CTAS; odróżnić składnię akceptowaną tylko przez bibliotekę od poprawnej serwerowo. Nie oznaczać S4 jako exploitu bez poprawnego serwerowo przykładu.
- [ ] Uzupełnić spec o wybraną politykę, kompatybilność setup/temp i wpływ na liczbę wywołań. Dopiero wtedy rozpisać/wykonać implementację; brak rozstrzygniętej granicy zaufania blokuje deklarację „read-only gwarantowane”.
- [ ] Na autoryzowanej jednorazowej bazie wykonać testy funkcji i temp, w tym brak skutku dla odrzuconego przypadku. Zachować wyłącznie syntetyczne dowody. Lokalny commit specyfikacji oddzielić od commitu wdrożenia polityki.

## T4 — identyczne zasady targetu w Core i CLI

**Files:** `src/SqlHarness.Core/Targets/TargetResolver.cs`, `Contracts.cs`; `src/SqlHarness.Cli/Commands/SqlHarnessCommands.cs`; `tests/SqlHarness.Tests/Targets/TargetResolverTests.cs`, `Cli/CommandTests.cs`.

**Interfaces:** `TargetResolver.Resolve(SqlTargetRequest, IReadOnlyDictionary<string, TargetProfile>) -> ResolvedTarget` pozostaje źródłem prawdy.

- [ ] Dodać test Core: profil + Engine kończy się safety rejection, także dla nieznanego silnika; bez połączenia. Pokryć profil + direct auth, direct + vars i brak pełnego celu.
- [ ] Uwzględnić Engine w wykrywaniu opcji direct. Nie ignorować przekazanej wartości. CLI może walidować format, ale nie ustanawia innych zasad bezpieczeństwa.
- [ ] Uruchomić TargetResolverTests i Cli.CommandTests; uzupełnić AGENTS.md/README tylko o faktycznie wdrożone zachowanie, zapisać lokalny commit.
