# Dashboard demo and screenshots

Explore a synthetic optimization session in the real local dashboard without a database connection. It contains three measured SQL versions, one timeout, and a final comparison against the original. All timings, IO counts, result checks, and labels are **demo data**, not benchmark results.

Requires a source checkout, Node.js 24+, and a SQLHarness binary built from the same checkout. For a source build, use the .NET SDK pinned in `global.json` and run `dotnet build src/SqlHarness.Cli -c Release` first. The binary is then at `src/SqlHarness.Cli/bin/Release/net10.0/sqlharness.exe` on Windows, or `sqlharness` on Linux/macOS.

## Explore the session

Run from the repository root, in a fresh PowerShell terminal:

```powershell
# Choose a NEW directory. The script refuses to overwrite an existing home.
node scripts/showcase/seed.mjs "$env:TEMP/sqlharness-showcase" 48139
if ($LASTEXITCODE -ne 0) { throw "Demo seed failed; choose a new directory." }
$env:SQLHARNESS_HOME = "$env:TEMP/sqlharness-showcase"
./src/SqlHarness.Cli/bin/Release/net10.0/sqlharness.exe dashboard --no-open
```

Open the printed dashboard URL locally, then choose **Sessions → Annual sales report**. The initial URL contains a local authentication token: keep it out of screenshots, logs, and shared links. Ctrl+C stops this demo dashboard. Close the dedicated terminal afterward to discard its `SQLHARNESS_HOME` override.

On Linux/macOS, use a new absolute directory for the seed, set `SQLHARNESS_HOME` to that directory in the dedicated shell, and run the `sqlharness` executable without `.exe`.

The target uses the reserved `demo.invalid` hostname. The script only creates a new local SQLite journal and config files; it never executes the displayed SQL. Mutations and transcript collection are disabled. Do not use this home for real queries or point it at an existing personal SQLHarness home.

## Refresh screenshots

Use the dashboard built from the same revision as the journal. The capture script requires Playwright installed in an external tooling directory and its Chromium browser installed. `SHOWCASE_TOOLS_ROOT` names that directory's `node_modules`; it does not modify application dependencies.

```powershell
$env:SHOWCASE_TOOLS_ROOT = "D:/temp/sqlharness-dashboard-audit/node_modules"
node scripts/showcase/capture.mjs "$env:SQLHARNESS_HOME" docs/images
```

The script reads authentication from the isolated home's `dashboard.json`, keeps it in an HTTP-only browser cookie, and blocks API writes. It saves three PNGs plus a capture report. Inspect all images before committing them. The report contains no dashboard token. Keep the report outside the repository if you are only experimenting with crops.

The README captions explicitly identify the synthetic data. Do not present screenshot timings as actual SQL Server performance results. The fixture is a source-development aid, not a supported journal import format; regenerate it when the journal schema changes.
