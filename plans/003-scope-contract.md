# 003/T1 — Model zakresu MCP dla artefaktów (kontrakt)

Status: **kontrakt (docs-only, T1)**. Krok wyłącznie dokumentacyjny: zero zmian kodu
produkcyjnego i testów. Wdrożenie (T2–T5) musi zrealizować dokładnie ten model.

## 1. Definicja zakresu (co jest tożsamością właściciela)

Zakres MCP = krotka zamrożona w `McpScope` w momencie startu procesu:

- `TargetRequest`: `Profile` (nazwa profilu) + `Vars` (kanoniczny zestaw zmiennych,
  słownik `StringComparer.OrdinalIgnoreCase`, odrzuca puste klucze i wartości null;
  `src/SqlHarness.Mcp/McpScope.cs`, `SqlTargetRequest` w `src/SqlHarness.Core/Contracts.cs`).
- `ResolvedTarget`: `Server`, `Database`, `Auth`, `Mode`, `Engine`
  (`SqlEngine.SqlServer` domyślnie), `Transport` (`PostgresTransport` dla Postgres);
  `src/SqlHarness.Core/Targets/TargetResolver.cs`.
- Kanoniczny zestaw zmiennych = znormalizowana postać `Vars` z `TargetRequest`
  (klucze case-insensitive, kolejność sortowana przy serializacji do metadanych;
  porównanie właściciela po parach klucz=wartość, nie po kolejności argumentów CLI).

Właściciel artefaktu = ta krotka w postaci danych:

```text
owner = { profile, vars-kanoniczne, engine, server, database }
```

Bez haseł i sekretów: `Auth`/`AuthSpec` (użytkownik, strategia, `passwordEnvVar`)
oraz wartości zmiennych środowiskowych NIGDY nie trafiają do metadanych właściciela
ani do raportów. Sekrety zostają w środowisku procesu i magazynie profili operatora
(`docs/mcp.md`: "Connection secrets stay in the process environment...").
Nazwa zmiennej środowiskowej z hasłem może być częścią `AuthSpec`, sama wartość — nigdy.

Czego NIE wolno używać jako autoryzacji (warunek stopu planu):

- sama nazwa bazy (`Database` bez `Server` + `Engine` + profilu/vars),
- sama nazwa katalogu artefaktu (id to nieprzezroczysty segment katalogu, nie prawo dostępu),
- tekst SQL ani nazwa pliku raportu.

## 2. Gdzie właściciel jest zapisywany (wybrany wariant)

Wybrany wariant (verbatim z zadania): **wersjonowane metadane właściciela tworzone
przez zaufaną ścieżkę zapisu; brak właściciela oznacza odmowę MCP.**

Konkretnie, ugruntowane w dzisiejszym kodzie:

- `ArtifactManifest` v1 (`src/SqlHarness.Core/ArtifactReader.cs`):
  `CurrentManifestVersion = 1`, pola `ManifestVersion`, `ArtifactKind`, `ReportFile`,
  `Sections`. Rodzaje: `compare`, `measure`, `measure-set` (`CompareKind`,
  `MeasureKind`, `MeasureSetKind`). Sekcje: `summary`, `metrics`, `operators`
  (`SummarySection`, `MetricsSection`, `OperatorsSection`). Pisarz:
  `ArtifactManifest.ForReport` + `ArtifactDirectoryPublisher` (`src/SqlHarness.Core/Artifacts.cs`).
- `SnapshotDocument` v1 (`src/SqlHarness.Core/SnapshotStore.cs`):
  `Version`, `CreatedAt`, `ResultSets`, `ResultHash`, `CurrentVersion = 1`.
  Pisarz: `SnapshotDocument.Create` via `SnapshotRunner` (`src/SqlHarness.Core/SnapshotRunner.cs`).

Kontrakt T3/T4 (do wdrożenia, nie w tym kroku):

- Pola właściciela są **ściśle addytywne**: istniejące pola manifestu/dokumentu snapshotu
  zostają bez zmian; dochodzi wersjonowane pole właściciela (np. podniesienie
  `ManifestVersion`/`Version` przy zachowaniu wstecznego odczytu v1 przez CLI).
- Zaufana ścieżka zapisu = istniejący pisarz Core (`ArtifactDirectoryPublisher`,
  `SnapshotStore.Save`): to on, nie adapter MCP ani CLI, wpisuje metadane właściciela
  z kontekstu operacji, który otrzymał z zamrożonego scope.
- Enforcement właściciela jako **opt-in per wywołanie** (ruling kontrolera, wiążący):
  parametr `owner` w Core reader/mapper (`ArtifactReader.ReadSection`,
  `McpOperationMapper.ReadArtifactSection`); MCP przekazuje owner ze frozen scope,
  CLI nie przekazuje i zachowuje pracę po nazwie. Core pozostaje wspólnym miejscem
  safety bez gałęzi per-adapter.
- **Brak właściciela = odmowa MCP**: artefakt legacy (katalog bez manifestu lub
  manifest v1 bez pola właściciela, snapshot v1 bez targetu) jest czytelny dla CLI
  (dotychczasowa świadoma praca po nazwie, offline reader bez zmian), ale MCP odmawia
  przed zwróceniem danych — bezpieczny błąd bez treści, `isError=true`.
  MCP nie może sam nadać sobie prawa do starego artefaktu na podstawie jego nazwy
  (zakaz samonadawania, test T2).
- Granica integralności: **lokalny pisarz z uprawnieniami procesu jest poza granicą
  integralności** — manifest jest metadanymi do egzekwowania zakresu w ramach jednego
  zaufanego hosta, nie dowodem kryptograficznym. Nie obiecujemy odporności na
  fałszowanie manifestu przez lokalnego pisarza (ręczna edycja `manifest.json` /
  `report.json` pod `SqlHarnessPaths.CompareDir` lub pliku snapshotu). Kontrakt
  gwarantuje tylko: bez właściciela zgodnego ze scope MCP odmawia; z niezgodnym
  właścicielem MCP odmawia; zgodny właściciel + zgodne sekcje = projekcja
  `summary`/`metrics`/`operators` bez raw SQL, planów, `queries.jsonl` i komórek.

## 3. Trwałość po restarcie

- `McpScope` jest niemutowalny i zamrożony na proces: profil odczytany raz,
  target zresolvedowany raz, brak przeładowania (`McpScope.Create`, `docs/mcp.md`
  "Scope lifetime"). Nowy profil/vars/target wymaga restartu procesu.
- Właściciel artefaktu jest **trwały**: zapisany w manifeście/dokumencie snapshotu
  obok artefaktu, więc własny artefakt działa po restarcie — nowy proces z tym samym
  profilem + kanonicznymi vars + resolved `engine`/`server`/`database` spełnia
  porównanie właściciela bez żadnego stanu w pamięci serwera.
- Porównanie po restarcie odbywa się wyłącznie na danych z metadanych (profil, vars,
  engine, server, database); hasła nigdy nie są odtwarzane z artefaktu.
- Legacy (brak pola właściciela) po restarcie zachowuje się identycznie jak przed:
  CLI czyta, MCP odmawia. Nie migrujemy ani nie nadpisujemy automatycznie
  istniejących plików (T4).

## 4. Właściciel dla matrix i param-set

- `compare --matrix`: jeden wymiar, co najmniej dwie typowane wartości, kolejność
  użytkownika, **nowe połączenie i jeden setup per wartość**, pierwszy błąd zatrzymuje
  run, ukończone komórki zostają (`CompareCellRunner`: `ResolvedTarget Target` per
  komórka). Właściciel: każda komórka dziedziczy właściciela zakresu wywołania
  (ten sam profil/vars/engine/server/database; różni się tylko wartością macierzy,
  która jest parametrem wykonania, nie częścią tożsamości scope). Read MCP całej
  macierzy wymaga właściciela zakresu; partial (`status: partial`) nie poszerza dostępu.
- `measure --param-set`: **jedna sesja, jeden setup** (setup wiąże pierwszy zestaw),
  pliki `.sqljson` strict (`name` + `parameters` tylko, ≤64 KiB, bez BOM/komentarzy),
  rotacja pomiarów `r modulo setCount`. Właściciel: jeden właściciel zakresu dla
  całego artefaktu `measure-set`; poszczególne sety (`name`, hash wartości, typy)
  są danymi wewnątrz, nie osobnymi właścicielami. Stabilność per-set nie jest
  twierdzeniem o równoważności cross-set.
- Oba tryby: parametry/wartości macierzy nigdy nie trafiają do metadanych właściciela
  ani do raportów (`Values are never copied into reports`); owner to scope, nie dane.

## 5. Kontrakt odczytu (co czyta, co odmawia)

Dzisiejszy defekt (punkt odniesienia, verbatim):

```text
return ArtifactReader.ReadSection(SqlHarnessPaths.CompareDir, id ?? string.Empty, section);
```

`ReadArtifactSection(scope, id, section)` przyjmuje scope, ale go nie sprawdza —
czyta globalny `CompareDir`. Docelowo (T3): sprawdzenie właściciela **przed projekcją
raportu**, w Core, na ścieżce `ArtifactReader.ReadSection(root, artifactId, section)`
z opt-in `owner`. Limity zostają: id = pojedyncza nazwa katalogu związana manifestem
(≤128 znaków, wzorzec, brak traversalu/linków), manifest ≤64 KiB (`MaxManifestBytes`),
raport ≤16 MiB (`MaxReportBytes`), tylko sekcje z mapowania manifestu, błędy bez treści
(`ArtifactReadException`, exit `Safety`/`LocalStorage`).

Snapshoty (T4): osobno capture/diff. MCP wymaga właściciela i odmawia obcego zakresu;
CLI zachowuje pracę po nazwie. `--diff` nigdy nie drukuje wartości komórek
(tylko lokalizacje/rodzaje); exit `8` przy różnicach, `7` dla watch-timeout — oba
zachowane.

## 6. Globalne wiążące ograniczenia (niezmienne w tym planie)

- Kody exit `0/2/3/4/5/6/7/8` zachowane; legacy JSON zachowany; oba silniki
  (`sqlserver`/`postgres`, `SqlEngine`) zachowane.
- Pola tylko addytywne (manifest, snapshot, raporty, envelope MCP).
- Core wspólnym miejscem safety; adaptery (MCP/CLI) nie tworzą własnego klasyfikatora.
- Testy: istniejące wzorce fake reader/session/module i syntetyczny
  `SQLHARNESS_HOME`; syntetyczne raporty, nie artefakty użytkownika.
- Nie czytać rzeczywistych sekretów ani artefaktów użytkownika; `.sqlplan`,
  `queries.jsonl`, snapshoty i parametry runtime są lokalnie wrażliwe.

## 7. Mapa źródeł (worktree, baza 637a59b)

- `src/SqlHarness.Mcp/McpScope.cs` — frozen scope: `TargetRequest`, `ResolvedTarget`, vars.
- `src/SqlHarness.Core/ArtifactReader.cs` — `ArtifactManifest` v1, `ReadSection`, limity.
- `src/SqlHarness.Core/SnapshotStore.cs` + `SnapshotRunner.cs` — `SnapshotDocument` v1, zapis/odczyt.
- `src/SqlHarness.Core/Artifacts.cs`, `Contracts.cs` — `ArtifactDirectoryPublisher`, raporty,
  `SqlTargetRequest`, `SqlHarnessExitCode`.
- `src/SqlHarness.Core/Targets/TargetResolver.cs` — `ResolvedTarget` (Server/Database/Auth/Mode/Engine/Transport).
- `src/SqlHarness.Mcp/McpOperationMapper.cs` (`ReadArtifactSection`),
  `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs` — dzisiejsza ścieżka bez sprawdzenia.
- `docs/mcp.md` (Scope lifetime, Artifacts), `AGENTS.md` (silnik, exit codes).
- Matrix: `CompareCellRunner.cs`; param-set: strict `.sqljson`, jedna sesja.

## 8. Kryteria akceptacji T1

- [x] `plans/003-scope-contract.md` istnieje i zawiera model z §1–§7.
- [x] `git diff --check` → exit 0; jedyna zmiana to nowy `plans/003-scope-contract.md`.
- [ ] Regresja kodowa (T2): stary kod nie przechodzi nowych testów obcego zakresu/legacy
      — poza zakresem T1, do wykonania w T2.
