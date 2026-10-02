# Pomiar zainstalowanego CLI i buildu repo

Status: transkrypt 015/T1. Bez instalacji i bez zmiany kodu.

## 015/T1

Pomiar jest z 2026-10-02 na this Windows host. Baza planu, nazwana w `plans/015-installation-alignment.md`, to `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`. Ten commit jest w repozytorium. `git merge-base --is-ancestor` względem zmierzonego HEAD zakończył się kodem 0, więc baza jest przodkiem, nie tym HEAD. `git rev-parse HEAD` przed tym dokumentem zwrócił `ece0d7fcaef90325d20865db639c12b47bc2de4e`. `git status --short` był wtedy pusty. To nie jest pomiar na bazie z 2026-09-29.

Nie czytano `targets.json`, profili, haseł, connection stringów ani wartości zmiennych środowiska. Nie uruchamiano `dotnet test`. Nie publikowano, nie kopiowano i nie zastępowano binarki. Nie dopisano komend, stubów ani capabilities. Ten krok nie zapisuje procedury aktualizacji.

### Polecenia

| Polecenie | Kod | Stdout | Stderr |
|---|---|---|---|
| `Get-Command sqlharness -All` | brak kodu procesu; cmdlet nie zgłosił wyjątku | jeden obiekt | brak strumienia |
| `sqlharness --help` | 0 | 376 B | 0 B |
| `sqlharness --version` | 0 | 7 B | 0 B |
| `dotnet run --project src/SqlHarness.Cli -- --help` | 0 | 1971 B | 0 B |
| `dotnet run --project src/SqlHarness.Cli -- capabilities --json` | 0 | 6956 B | 0 B |

Dwa wywołania `sqlharness` szły przez `cmd.exe /d /c` w sesji tego hosta. To samo `--help` i to samo `--version` na ścieżce z `Get-Command` dały ten sam kod 0 i ten sam SHA-256 stdout. Stderr też miał 0 B. Katalog roboczy `dotnet run` to `D:\Dev\sqlharness\.worktrees\plan-015-installation-alignment`. W żadnym z dwóch strumieni `dotnet run` nie było logu MSBuild. Stdout helpa repo zaczyna się od `USAGE:`. Stdout capabilities zaczyna się od `{`.

### Get-Command

Jeden wynik. `CommandType` = `Application`. `Name` = `sqlharness.exe`. `Source` i `Path` = `C:\Users\rgone\.local\bin\sqlharness.exe`. `Definition` jest tą samą ścieżką. `Version` obiektu polecenia = `1.0.0.0`. Nie było drugiej aplikacji, funkcji ani aliasu.

`1.0.0.0` jest właściwością obiektu `Get-Command`. Nie jest tekstem stdout `--version`.

### Zainstalowane --version

Stdout ma 7 bajtów: znaki `1.0.0` i końcówka CRLF. SHA-256 `4ca8bbd3c595ee306226c6996b32b8d47a900217c55a7ed0cfc868ee444f34f1`. Tekst wersji:

```text
1.0.0
```

Help tej binarki reklamuje `-v, --version`. Uruchomione `--version` nie wypisało helpa. Wypisało `1.0.0`.

### Zainstalowane --help

Stdout ma 376 bajtów, końcówki CRLF, SHA-256 `da240f1cf6099b26b7736c648b440497379501f8d99a5f143ae4461eccb5512c`. Linie komend są dopełnione spacjami i nie mają opisów. W bloku każda linia stdout jest między `|`. Spacje przed zamykającym `|` należą do stdout. Znak `|` do stdout nie należy. Plik kończy się CRLF po linii `snapshot`; osobnej pustej linii za nią nie ma.

```text
|USAGE:|
|    sqlharness [OPTIONS] <COMMAND>|
||
|OPTIONS:|
|    -h, --help       Prints help information   |
|    -v, --version    Prints version information|
||
|COMMANDS:|
|    query        |
|    measure      |
|    compare      |
|    gain         |
|    plan         |
|    schema       |
|    ping         |
|    counts       |
|    space        |
|    watch        |
|    snapshot     |
```

### Help buildu repo

Stdout ma 1971 bajtów, końcówki CRLF, SHA-256 `77522d1729b33fc99bb9f58d6f4df1e2c3f3088de1588d957b59dd33e1ab8a1c`. Linie komend i kontynuacje opisów mają po 80 kolumn, łącznie ze spacjami dopełnienia. Blok `USAGE` i `OPTIONS` nie jest dopełniony do 80. Opisy są na tej samej linii co nazwa albo na kontynuacji wciętej dalej niż cztery spacje. Ta sama konwencja `|` co wyżej. Plik kończy się CRLF po linii `mcp`.

```text
|USAGE:|
|    sqlharness [OPTIONS] <COMMAND>|
||
|OPTIONS:|
|    -h, --help       Prints help information   |
|    -v, --version    Prints version information|
||
|COMMANDS:|
|    query           Run a bounded SQL query passing the static visible-effects  |
|                    check                                                       |
|    measure         Measure query performance across repeated runs              |
|    compare         Compare baseline and candidate performance and results      |
|    gain            Report local output savings estimates                       |
|    plan            Distill a saved execution plan offline                      |
|    artifact        Read safe sections of a saved benchmark artifact            |
|    schema          Inspect database tables, columns, and relations             |
|    ping            Check a database connection and target identity             |
|    counts          Inspect row counts for database tables                      |
|    space           Inspect database file and table storage                     |
|    watch           Poll a bounded query passing the static visible-effects text|
|                    check until a condition is met                              |
|    snapshot        Capture or compare a named query result                     |
|    qstop           Rank SQL Server Query Store consumers                       |
|    indexes         Inspect SQL Server missing-index evidence                   |
|    capabilities    Describe local commands, engines, limits, and output modes  |
|    doctor          Check local installation and profile-file availability      |
|                    without connecting                                          |
|    validate        Classify SQL offline using a closed profile; static         |
|                    visible-effects check only, never connects                  |
|    mcp             Model Context Protocol adapter                              |
```

Pierwsze 184 bajty obu helpów są równe. Wspólny prefix obejmuje `USAGE`, `OPTIONS`, nagłówek `COMMANDS:` oraz początek linii `query`: cztery spacje, nazwę `query` i osiem spacji, którymi kończy się linia w helpie zainstalowanym. Dalej zainstalowany stdout ma CRLF. Stdout repo zostaje w tej samej linii i dopisuje opis.

### Nazwy w helpie

Nazwa komendy to pierwszy token linii pod `COMMANDS:`, której wcięcie ma dokładnie cztery spacje. Kontynuacja opisu jest wcięta głębiej i nie jest osobną komendą.

| Nazwa | Help zainstalowany | Help repo |
|---|---|---|
| query | tak | tak |
| measure | tak | tak |
| compare | tak | tak |
| gain | tak | tak |
| plan | tak | tak |
| artifact | nie | tak |
| schema | tak | tak |
| ping | tak | tak |
| counts | tak | tak |
| space | tak | tak |
| watch | tak | tak |
| snapshot | tak | tak |
| qstop | nie | tak |
| indexes | nie | tak |
| capabilities | nie | tak |
| doctor | nie | tak |
| validate | nie | tak |
| mcp | nie | tak |

W obu: `query`, `measure`, `compare`, `gain`, `plan`, `schema`, `ping`, `counts`, `space`, `watch`, `snapshot`. Tylko w helpie zainstalowanym: żadna. Tylko w helpie repo: `artifact`, `qstop`, `indexes`, `capabilities`, `doctor`, `validate`, `mcp`.

`capabilities` jest w helpie repo i nie ma jej w helpie zainstalowanym. `validate` tak samo. `mcp` tak samo. Help repo nie wymienia `serve` na tej stronie.

Kolejność zainstalowana: `query`, `measure`, `compare`, `gain`, `plan`, `schema`, `ping`, `counts`, `space`, `watch`, `snapshot`. Kolejność repo: `query`, `measure`, `compare`, `gain`, `plan`, `artifact`, `schema`, `ping`, `counts`, `space`, `watch`, `snapshot`, `qstop`, `indexes`, `capabilities`, `doctor`, `validate`, `mcp`.

Opcje `-h, --help` i `-v, --version` są w obu helpach. Poza wspólnym prefixem help zainstalowany nie niesie opisów komend. Help repo je niesie.

### capabilities --json

Pola szczytowe: `version` = `1.0.0`, `buildId` = `1.0.0+ece0d7fcaef90325d20865db639c12b47bc2de4e`, `contractVersion` = `1`, oraz `commands`, `engines`, `limits`, `outputModes`, `safetyAnalysis`. W pliku stdout znak `+` jest zapisany jako `\u002B`. Po odczycie JSON sufiks `buildId` jest równy zmierzonemu HEAD.

Nazwy w `commands`, w tej kolejności: `capabilities`, `doctor`, `validate`, `query`, `measure`, `compare`, `schema`, `ping`, `counts`, `space`, `watch`, `snapshot`, `qstop`, `indexes`, `plan`, `gain`, `artifact`.

Silniki: `sqlserver` (`supportsQstop` true, `supportsIndexes` true) i `postgres` (oba false). `outputModes`: `text`, `json`, `json-summary`, `agent`. `safetyAnalysis.analysisKind` = `static-visible-effects`, `analysisContractVersion` = `1`, `hiddenEffectsVerified` = false, `objectAndPermissionStatus` = `unknown`.

Zbiór nazw helpa repo i zbiór `commands` różnią się jedną pozycją. `mcp` jest w helpie repo i nie występuje w tym JSON, także jako podciąg. Żadna nazwa z `commands` nie jest pominięta w helpie repo. `serve` nie występuje w tym JSON.

Tekst `version` równa się tekstowi zainstalowanego `--version` (`1.0.0`). To nie jest tożsamość binarki. Ciała helpów mają inny SHA-256 i inną listę nazw. `buildId` dokleja pełny SHA tego HEAD do tego samego `1.0.0`.

To ciało nie zawiera `C:\Users`, `targets.json`, `.sqlharness`, `password`, `Server=`, `User Id` ani `connectionString`. Słowa `profile` i `connection` stoją tylko w opisach komend kontraktu. Ciało jest wklejone w całości. SHA-256 tego stdout to `84dbc47dcd5a4e122ea06117b3946316a242141ba8e4e7545e0e21f34809bef7`, 6956 B, CRLF, końcówka po `}`.

```json
{
  "version": "1.0.0",
  "buildId": "1.0.0\u002Bece0d7fcaef90325d20865db639c12b47bc2de4e",
  "contractVersion": 1,
  "commands": [
    {
      "name": "capabilities",
      "description": "Describe commands, engines, limits, and output modes."
    },
    {
      "name": "doctor",
      "description": "Check local SQLHarness installation and profile-file availability."
    },
    {
      "name": "validate",
      "description": "Classify SQL offline using a closed target profile; never connects. Usages: query, setup, benchmark."
    },
    {
      "name": "query",
      "description": "Run a bounded SQL query."
    },
    {
      "name": "measure",
      "description": "Measure a query."
    },
    {
      "name": "compare",
      "description": "Compare baseline and candidate queries."
    },
    {
      "name": "schema",
      "description": "Inspect database schema."
    },
    {
      "name": "ping",
      "description": "Check a database connection."
    },
    {
      "name": "counts",
      "description": "Inspect table row counts."
    },
    {
      "name": "space",
      "description": "Inspect database storage."
    },
    {
      "name": "watch",
      "description": "Poll a bounded query passing the static visible-effects text check."
    },
    {
      "name": "snapshot",
      "description": "Capture or compare a named query result."
    },
    {
      "name": "qstop",
      "description": "Rank SQL Server Query Store consumers."
    },
    {
      "name": "indexes",
      "description": "Inspect SQL Server missing-index evidence."
    },
    {
      "name": "plan",
      "description": "Distill a saved execution plan offline."
    },
    {
      "name": "gain",
      "description": "Report saved-output estimates."
    },
    {
      "name": "artifact",
      "description": "Read safe sections of a saved benchmark artifact offline."
    }
  ],
  "engines": [
    {
      "name": "sqlserver",
      "supportsQstop": true,
      "supportsIndexes": true,
      "parameterTypes": [
        "nvarchar",
        "nvarchar(max)",
        "varchar",
        "varchar(max)",
        "char",
        "nchar",
        "int",
        "bigint",
        "smallint",
        "tinyint",
        "bit",
        "decimal",
        "decimal(p,s)",
        "numeric",
        "numeric(p,s)",
        "float",
        "real",
        "money",
        "smallmoney",
        "date",
        "time",
        "datetime",
        "datetime2",
        "smalldatetime",
        "datetimeoffset",
        "uniqueidentifier",
        "varbinary",
        "varbinary(max)",
        "hierarchyid",
        "geography",
        "geometry"
      ]
    },
    {
      "name": "postgres",
      "supportsQstop": false,
      "supportsIndexes": false,
      "parameterTypes": [
        "nvarchar",
        "nvarchar(max)",
        "varchar",
        "varchar(max)",
        "char",
        "nchar",
        "int",
        "bigint",
        "smallint",
        "tinyint",
        "bit",
        "decimal",
        "decimal(p,s)",
        "numeric",
        "numeric(p,s)",
        "float",
        "real",
        "date",
        "time",
        "datetime",
        "datetime2",
        "datetimeoffset",
        "uniqueidentifier",
        "varbinary",
        "varbinary(max)"
      ]
    }
  ],
  "limits": {
    "queryTimeoutSeconds": {
      "min": 1,
      "max": 300
    },
    "queryMaxRows": {
      "min": 0,
      "max": 500
    },
    "comparisonRowCapPerRun": {
      "min": 0,
      "max": 1000000
    },
    "comparisonUniqueFingerprintBudgetPerCell": {
      "min": 0,
      "max": 2000000
    },
    "repeat": {
      "min": 1,
      "max": 100
    },
    "qstopTop": {
      "min": 1,
      "max": 500
    },
    "qstopWindowMinutes": {
      "min": 1,
      "max": 44640
    },
    "indexesTop": {
      "min": 1,
      "max": 500
    },
    "agentOutputBytes": {
      "min": 4096,
      "max": 1048576,
      "defaultValue": 16384
    },
    "agentCellChars": {
      "min": 0,
      "max": 4096,
      "defaultValue": 512
    },
    "sessionTempStatements": {
      "sqlserver": [
        "DECLARE scalar variables with analyzed initializers",
        "Plain SET @v = expr to a scalar local declared earlier in the same batch: allowed in query and measured SQL, denied in --setup. The RHS gets the external, stateful and cross-database checks. Compound, cursor, member and session/transaction option SET stay denied",
        "DECLARE @t TABLE (...) then INSERT/UPDATE/DELETE/MERGE/SELECT against it, declared earlier in the same batch. A table variable from --setup is not visible to measured SQL (separate batch): carry data in #temp. OUTPUT INTO @t only when the primary target is also proven local (persistent primary target: MutationNotAllowed). Aliased DML targets and user-defined table types stay denied",
        "TRUNCATE TABLE #temp (unambiguous local temp only)",
        "ALTER TABLE #temp ADD/DROP COLUMN and local CHECK/DEFAULT/NULL/UNIQUE constraints"
      ],
      "postgres": [
        "EXPLAIN over a safe SELECT (plan-only, read-only)",
        "EXPLAIN ANALYZE with full inner-statement effect analysis",
        "SELECT INTO TEMP TABLE with unambiguous single-part name",
        "TRUNCATE [ONLY] of proven current-session temps only, named unqualified or as pg_temp.name; not ON COMMIT DROP. Persistent, mixed, CASCADE, RESTART IDENTITY and other schema-qualified targets stay denied",
        "Session-temp proof for temp DML, DROP TABLE, CREATE INDEX and TRUNCATE targets is name-based. An unqualified name assumes the default search_path (pg_temp first) and is not checked against the server; when search_path may differ, write to pg_temp.name, which does not depend on it. A name must be quoted or all-ASCII-unquoted and at most 63 UTF-8 bytes where declared and where used, else never proven (quote or shorten). DROP TABLE of a name unknown offline revokes every proof. See AGENTS.md"
      ]
    },
    "artifactRead": {
      "sections": [
        "summary",
        "metrics",
        "operators"
      ],
      "manifestVersion": 1,
      "maxReportBytes": 16777216
    },
    "watchNdjson": {
      "events": [
        "started",
        "changed",
        "completed",
        "failed"
      ],
      "schemaVersion": 1,
      "sequence": "strictly increasing from started; exactly one terminal record",
      "history": "no retention: every change is emitted immediately"
    }
  },
  "outputModes": [
    "text",
    "json",
    "json-summary",
    "agent"
  ],
  "safetyAnalysis": {
    "analysisKind": "static-visible-effects",
    "analysisContractVersion": 1,
    "hiddenEffectsVerified": false,
    "objectAndPermissionStatus": "unknown"
  }
}
```

### Kontekst historyczny

To nie jest ten pomiar. Audyt z 2026-09-28 w `plans/2026-09-28-project-audit.md` mówi o helpie pod ścieżką z `Get-Command` i o braku `capabilities`, `validate` oraz `mcp`. Specyfikacja audytu z 2026-09-26 w `docs/superpowers/specs/2026-09-26-project-audit.md` mówi o krótszej liście niż ówczesny HEAD, w tym o braku `qstop` i `indexes`. Bieżącym faktem jest transkrypt powyżej, na HEAD `ece0d7fcaef90325d20865db639c12b47bc2de4e`, nie na `8aa01f8bdf95ae6acbd1e5d4e3137449ddf0d17b`.

Na tym pomiarze rozjazd powierzchni helpa jest. Zainstalowany zestaw nazw jest podzbiorem helpa repo, nie zestawem rozłącznym. Pochodzenie pliku, jego hash i data nie są w tym kroku.

### UNPROVEN

- Tekst `dotnet run --project src/SqlHarness.Cli -- --version`. Warunek: uruchomić to polecenie i zapisać stdout. Help repo reklamuje `-v, --version`. To nie jest ten tekst. Tego polecenia nie uruchamiano.
- Czy zainstalowana binarka przyjmuje `capabilities`, `validate`, `mcp`, `doctor`, `artifact`, `qstop` albo `indexes`. Brak nazwy w `--help` nie jest kodem odmowy. Warunek: uruchomić każdą z tych nazw na ścieżce z `Get-Command` i zapisać kod oraz oba strumienie. Tego nie uruchamiano.
- Czy `sqlharness -v` wypisuje to samo `1.0.0` co `--version`. Warunek: uruchomić `sqlharness -v` na tej ścieżce. Uruchomiono tylko `--version`.
- Czy help `mcp` w buildzie repo wymienia `serve`. Warunek: `dotnet run --project src/SqlHarness.Cli -- mcp --help`. Na stronie głównej helpa jest `mcp`, nie ma `serve`.
- Hash pliku, czas pliku i zasób wersji PE poza właściwością `Version` z `Get-Command`. Warunek: odczyt metadanych tego pliku bez jego zastąpienia. `1.0.0.0` jest tylko tą właściwością.
- Czy pusty stderr `dotnet run` oznacza brak przebudowy. Warunek: log MSBuild z tego samego polecenia. W przechwyconych strumieniach go nie było.

## 015/T2

Odczyt jest z 2026-10-02T19:41:48.1799751+02:00 na this Windows host. Rozjazd z `## 015/T1` nie zniknął. Oznaczenia RESOLVED EXTERNALLY nie ma. Nie było reinstalacji. Nie publikowano, nie kopiowano, nie przenoszono i nie zastępowano binarki. Nie pisano konfiguracji użytkownika. Nie czytano `targets.json`, profili, haseł, connection stringów ani wartości zmiennych środowiska. Nie uruchamiano `dotnet test`, `dotnet run`, `--help` ani `--version`. Pliku nie uruchamiano. Ten krok nie zapisuje procedury aktualizacji.

### Get-Command

`Get-Command sqlharness -All` zwraca jeden obiekt. Wyjątku nie było. `CommandType` = `Application`. `Name` = `sqlharness.exe`. `Source`, `Path` i `Definition` = `C:\Users\rgone\.local\bin\sqlharness.exe`. `Version` obiektu polecenia = `1.0.0.0`. Nie było drugiej aplikacji, funkcji ani aliasu. Ścieżka i ta właściwość `Version` są te same co w `## 015/T1`.

Ta ścieżka jest jedyną binarką tego kroku. Atrybuty to `Archive`. `LinkType` jest null, `Target` jest null. W tym odczycie plik nie jest linkiem. Hash, czasy i zasób wersji dotyczą tego pliku.

### Plik

| Pole | Wartość |
|---|---|
| Ścieżka | `C:\Users\rgone\.local\bin\sqlharness.exe` |
| Długość | 95604247 B |
| SHA-256 | `bb702504d05acc124f0c50030818d61ec4790bf33eb3b35dfe0b651983be5c2f` |
| Utworzenie | 2026-07-19T22:30:24.3089416+02:00 |
| Ostatni zapis | 2026-09-23T10:58:00.4821692+02:00 |

`Get-FileHash -Algorithm SHA256` zwrócił `BB702504D05ACC124F0C50030818D61EC4790BF33EB3B35DFE0B651983BE5C2F`. Osobny skrót SHA-256 ze strumienia tych samych bajtów dał te same cyfry. W tabeli są małymi literami, jak hashe stdout w `## 015/T1`. Długość strumienia jest równa długości pliku, 95604247 B. Po odczycie długość i czas ostatniego zapisu były te same.

Strefa zaobserwowana to Windows `Central European Standard Time`. Ma czas letni. Nazwa letnia to `Central European Daylight Time`. Bazowy offset strefy to `+01:00`. Napis strefy w Windows pokazuje ten bazowy offset. Na obu znacznikach i w chwili odczytu czas letni obowiązywał, więc ich offset to `+02:00`, nie `+01:00`. `Kind` lokalnego czasu utworzenia i ostatniego zapisu to `Local`. Ten sam moment utworzenia w UTC to 2026-07-19T20:30:24.3089416Z. Ten sam moment ostatniego zapisu w UTC to 2026-09-23T08:58:00.4821692Z.

### Zasób wersji

Odczyt `FileVersionInfo` bez uruchamiania pliku. Cztery pola są obecne i niepuste. Żadnego nie brakuje.

| Pole | Wartość |
|---|---|
| FileVersion | `1.0.0.0` |
| ProductVersion | `1.0.0+3d2c41892d15d563c22dba9c917c71aa31cb66ff` |
| OriginalFilename | `sqlharness.dll` |
| FileDescription | `sqlharness` |

`FileVersion` jest równe właściwości `Version` z `Get-Command`. Nie jest tekstem stdout `--version` z `## 015/T1`. Tamten stdout to znaki `1.0.0`. `ProductVersion` jest innym, dłuższym napisem. Nie jest równe `buildId` z `capabilities --json` w `## 015/T1` (`1.0.0+ece0d7fcaef90325d20865db639c12b47bc2de4e`). Sufiks po `+` nie jest SHA-256 tego pliku.

Te 40 znaków hex jest pełnym id obiektu w tym repozytorium. `git cat-file -t` zwrócił `commit`. `git merge-base --is-ancestor` względem HEAD `d2a07291ffc6b51f40792635c681d9fc47f7552e` zakończył się kodem 0, więc ten commit jest przodkiem, nie tym HEAD. Data committera, `git log -1 --format=%cI`, to 2026-09-11T11:28:10+02:00. Temat to `Merge branch 'feat/postgres-engine'`. Czas utworzenia pliku jest wcześniejszy niż ta data committera. Czas ostatniego zapisu pliku jest późniejszy. To trzy czasy, nie log publikacji.

`OriginalFilename` kończy się na `.dll`. Plik z `Get-Command` kończy się na `.exe`. To wartość zasobu. Nie jest metodą publikacji.

### Authenticode

`Get-AuthenticodeSignature`: `Status` = `NotSigned`, `SignatureType` = `None`, `IsOSBinary` = false. Certyfikat podpisującego jest null. Podpisu nie ma, więc nie ma nazwy podpisującego. Brak podpisu nie jest wydaniem GitHub i nie jest lokalnym `dotnet publish`.

### Pochodzenie

Pochodzenie poza ścieżką, długością, SHA-256, dwoma czasami pliku, czterema polami zasobu wersji i brakiem podpisu jest UNPROVEN. Warunek, który by je wykazał: suma kontrolna wydania albo log publikacji równe SHA-256 `bb702504d05acc124f0c50030818d61ec4790bf33eb3b35dfe0b651983be5c2f`. Takiego dopasowania ten krok nie ma. Nie wymyślono źródła.

Przed tym dopiskiem tekst worktree nie zawierał tego SHA-256 ani pełnego id `3d2c41892d15d563c22dba9c917c71aa31cb66ff`. Obiekt gita istnieje osobno od tego tekstu. To nie jest przegląd katalogów poza tym worktree i nie jest sumą wydania.

### Lista komend

Lista dostępnych komend jest listą z helpa zainstalowanego, wyjętą w `## 015/T1` pod `Nazwy w helpie`. Kolejność jest kolejnością z tamtej tabeli, nie sortowaniem. Helpa nie uruchamiano ponownie. To nie jest nowe wykonanie każdej nazwy.

- `query`
- `measure`
- `compare`
- `gain`
- `plan`
- `schema`
- `ping`
- `counts`
- `space`
- `watch`
- `snapshot`

Strona repo jest w `## 015/T1`, w tej samej tabeli. Tamta tabela: tylko w helpie zainstalowanym żadna nazwa; tylko w helpie repo `artifact`, `qstop`, `indexes`, `capabilities`, `doctor`, `validate`, `mcp`. `capabilities`, `validate` i `mcp` nie są na liście powyżej. Rozjazd zostaje.

Różnica `mcp` między helpem repo a `capabilities --json` zostaje w `## 015/T1`. Ten krok jej nie zmienia. Warunek z `## 015/T1`, czy brak nazwy w helpie jest kodem odmowy, też zostaje tam. Tych nazw nie uruchamiano.

### UNPROVEN

- Pochodzenie pliku poza polami zapisanymi wyżej. Warunek: suma kontrolna wydania albo log publikacji równe SHA-256 `bb702504d05acc124f0c50030818d61ec4790bf33eb3b35dfe0b651983be5c2f`. Brak podpisu, `OriginalFilename` = `sqlharness.dll` i sufiks `ProductVersion` tego warunku nie spełniają. Tego dopasowania nie ma w tym kroku.
- Czy bajty zahashowane w tym kroku są bajtami procesu, który wypisał help i `--version` w `## 015/T1`. Warunek: hash pliku zdjęty przy tamtym pomiarze. T1 zapisał ścieżkę i hash stdout, nie hash pliku. W tym kroku pliku nie zastępowano. To nie dowodzi ani wymiany bajtów, ani ich tożsamości.
- Czy commit `3d2c41892d15d563c22dba9c917c71aa31cb66ff` wytworzył te bajty. Warunek jest ten sam co dla pochodzenia: dopasowanie hasha pliku. Sam napis w zasobie wersji i bycie przodkiem HEAD tego nie robią.
