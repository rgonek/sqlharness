# Dashboard: settings, profiles, error details, SQL rendering, theme

Date: 2026-10-07
Status: approved design, awaiting spec review
Extends: `2026-10-06-activity-dashboard-design.md`

## Goal

Make the operator dashboard usable as the single place to look at and tune local SQLHarness behavior:

1. Edit operator settings in `~/.sqlharness/config.json` (including `journal.storeSensitive`) from the dashboard.
2. View closed target profiles from `~/.sqlharness/targets.json`, read-only.
3. Show why an operation is `failed` or `rejected`: a tooltip on the status badge and a dialog on click.
4. Show SQL formatted and syntax-highlighted.
5. Let the operator choose the theme: system, light, or dark.

## Decisions

| Topic | Decision |
|---|---|
| Error message storage | Full message is stored only with `journal.storeSensitive: true`, like SQL text. Without it the UI shows `errorKind`, exit code, and a fixed description of that kind of error. |
| Editable settings | All `journal.*` fields, `dashboard.autoStart`, and `dashboard.idleShutdownHours`. `dashboard.port` is read-only in the UI and always preserved from the current file. |
| Enabling `storeSensitive` | Requires a confirmation dialog in the UI. Disabling does not. |
| Profiles | Read-only. Password values are never read; only the `passwordEnvVar` name is shown. No editing: profiles are the tool's safety boundary. |
| SQL rendering | `sql-formatter` (dialect by engine: `transactsql` / `postgresql`) and `highlight.js` core with only the `sql` language. |
| Theme | `system` / `light` / `dark`, stored per browser in `localStorage`. |

## Changes to the earlier dashboard design

- The earlier design says the API is read-only and has no write endpoints. This design adds exactly one write endpoint, `PUT /api/settings`. It writes only `config.json` and never writes the journal, `targets.json`, snapshots, or artifacts. The dashboard still never writes the activity journal.
- The earlier design says "no custom CSS and no theme changes". Syntax highlighting adds a small block of CSS that colors `highlight.js` token classes. The colors use the existing shadcn theme variables (or new `--sql-*` variables defined for `:root` and `.dark`). There are no other style changes.
- Dark mode no longer follows only the system preference; the operator can override it.

## 1. Error details

### Journal

- Schema migration `user_version` 3 → 4: `ALTER TABLE operations ADD COLUMN error_message TEXT`. Older binaries that see version 4 behave as today for a newer version (journal disabled, dashboard refuses to run).
- `OperationEnd` gets `string? ErrorMessage = null`.
- `OperationJournalDescriber.DescribeEnd` sets it from `outcome.MachineError.Message` (plus `Hint` on a new line when present) only when the status is `failed` or `rejected`. `Cancelled` and `Crashed` leave it null; their `error_kind` (`cancelled`, `unhandled_exception`) is self-explanatory.
- `ActivityJournal.Complete` writes `error_message` only when `_storeSensitive`; otherwise it writes `NULL`. The stored message is truncated to 4096 characters, with a trailing `…` when truncated.
- The describer's class comment ("never reads ... messages") is updated: it reads the error message for opt-in storage only.
- Retention needs no change; the column goes away with its row.

### API

- `OperationSummary` gets `string? ErrorMessage`. It is null unless the row has one, so list and detail responses carry it without an extra request.
- The live feed carries the same field because it reuses `OperationSummary`.

### UI

- New `lib/errors.ts`: `describeError(exitCode, errorKind)` returns a fixed, data-free sentence. It covers the machine error codes the journal stores (`safety_rejected`, `authentication_failed`, `target_mismatch`, `sql_execution_failed`, `local_storage_failed`, `operation_failed`, `cancelled`, `unhandled_exception`). An unknown kind falls back to a sentence for the exit code (2–6), then to a generic sentence. Finer safety reasons (for example `MutationNotAllowed`) appear only inside the stored message.
- `StatusBadge` gets optional `exitCode`, `errorKind`, and `errorMessage`. For `failed` and `rejected`:
  - hover or focus shows a `Tooltip` with the error kind, exit code, and the `describeError` sentence;
  - click or Enter opens a `Dialog` with the same header and the full message in a scrollable `<pre>`. When the message is null, the dialog says: "The full message is stored only when `journal.storeSensitive` is enabled in Settings." It links to `/settings`.
- The badge stays a plain badge for other statuses. Every place that renders an operation status (`OperationTable`, used by the live and session pages, and `OperationPage`) passes the whole operation.

## 2. Settings

### API

- `GET /api/settings` returns `{ status: "missing" | "valid" | "invalid", path, settings }`. `settings` is the effective configuration, which is the defaults when the file is missing or invalid. The response never contains the file's raw text.
- `PUT /api/settings` takes the full settings document (`journal`, `dashboard`) as JSON, at most 64 KiB.
  - The request is validated with the same strict rules as `SqlHarnessConfigLoader`: unknown fields, wrong types, and out-of-range values are rejected with `400` and a list of field errors.
  - `dashboard.port` from the request body is ignored and replaced with the port from the current file, or the default when the file is missing or invalid.
  - The file is written atomically: write a temp file in `~/.sqlharness`, then replace. On Unix the file is owner-only (`0600`), as other SQLHarness local files are.
  - When the current file is `invalid`, the PUT must carry the query parameter `?overwriteInvalid=true`, otherwise it gets `409`. (A query parameter keeps the body exactly the strict config document.) The UI asks for that confirmation explicitly.
  - The response is the same shape as `GET`.
- Effect: settings are read at process start. Running `mcp serve` processes and CLI calls already in flight keep their old settings. The dashboard's own retention sweep reloads the config before each hourly run. The dashboard's idle shutdown setting applies after a dashboard restart.

### Security

`DashboardSecurity` changes from "GET/HEAD only" to "GET/HEAD everywhere, plus PUT on `/api/settings` only". A PUT must pass every existing check (exact loopback `Host`, token cookie) and in addition:

- header `X-SqlHarness-Dashboard: 1` is present (a cross-site form cannot set it, and a cross-site `fetch` with it needs a CORS preflight that the dashboard never approves);
- `Origin`, when present, equals `http://127.0.0.1:<port>` or `http://localhost:<port>`;
- `Content-Type` is `application/json`.

A failed check returns `403`. Any other non-GET method or route keeps returning `405` with `Allow: GET, HEAD`. A PUT never accepts the `?t=` token exchange.

### UI

- New page `/settings` in the navigation: switches for booleans, number inputs for numbers, `port` shown as read-only text, and the config file path.
- Turning `storeSensitive` on opens a confirmation dialog: "From now on the journal will store SQL text, full plans (which can include parameter values), and error messages for new operations. Existing rows are not changed." Turning it off saves without asking.
- After a save, a banner says: "Saved. New processes use these settings; restart running `mcp serve` sessions to apply them."
- Field errors from a `400` are shown next to the fields. An invalid file shows an alert with a "Replace with these settings" action that sends `?overwriteInvalid=true`.

## 3. Profiles (read-only)

- `GET /api/profiles` loads `targets.json` with the same loader the CLI uses and returns, per profile: name, engine, server and database templates (placeholders such as `{tenant}` kept as written), auth mode, `sqlUser` when present, the `passwordEnvVar` name, `sslMode`, `trustServerCertificate`, `rootCertificate` path when present, and the variables the profile needs.
- It never reads environment variables and never reports whether a password variable is set.
- A missing file returns `{ status: "missing", profiles: [] }`. An invalid file returns `{ status: "invalid", profiles: [] }` with a fixed message, without the loader's error text or file contents.
- New page `/profiles`: a table plus empty and invalid states. There are no edit controls.

## 4. Theme

- `useSystemTheme` is replaced by `useTheme()`, which returns `{ mode, setMode }` for `system | light | dark`. `system` keeps the current `matchMedia` behavior. The choice is stored in `localStorage` under `sqlharness.theme`, and every read and write is wrapped in `try/catch` with `system` as the fallback.
- A theme menu (sun / moon / monitor icons) goes into the `AppLayout` header.
- To avoid a light flash on load, `index.html` gets no inline script (CSP `script-src 'self'`). The theme is applied in `main.tsx` before the first render.

## 5. SQL rendering

- New component `SqlBlock({ sql, engine })`:
  - formats with `sql-formatter`, using `transactsql` for SQL Server or unknown and `postgresql` for Postgres; when formatting throws, it shows the original text;
  - highlights with `highlight.js/lib/core` and `highlight.js/lib/languages/sql`, rendered from `hljs.highlight(...).value`, which escapes input;
  - has a toggle, "Formatted / Original" (formatted by default), and a copy button.
- Used for baseline and candidate SQL on the operation page and for statement text in `PlanTree`.
- Both libraries are bundled by Vite; nothing loads at runtime from outside the assembly, and the CSP is unchanged.

## shadcn components

The shadcn CLI is not a project dependency. The new primitives (`dialog`, `tooltip`, `switch`, `input`, `label`, `dropdown-menu`) are added with `npx shadcn@latest add <component>` from `ui/`, as the README describes, and stay unchanged. If the CLI re-adds `@import "shadcn/tailwind.css"` to `src/index.css`, it is pointed back at the vendored `./styles/shadcn-tailwind.css`.

## Error handling

| Case | Behavior |
|---|---|
| `config.json` missing | GET returns defaults with `status: "missing"`; PUT creates the file |
| `config.json` invalid | GET returns defaults with `status: "invalid"`; PUT returns `409` without `?overwriteInvalid=true` |
| PUT body invalid | `400` with field errors; the file is unchanged |
| Write fails (permissions, disk) | `500` with a fixed message; the original file is unchanged because the replace never happened |
| `targets.json` missing or invalid | empty profile list with a status; no file content in the response |
| Formatter throws | original SQL is shown, still highlighted |
| `localStorage` unavailable | theme falls back to `system` and the choice is not remembered |

## Testing

- **Core journal** (real SQLite in a temporary `SQLHARNESS_HOME`): migration v3 → v4 keeps existing rows; `error_message` is written only with `storeSensitive`; truncation at 4096 characters; a cancelled operation stores no message.
- **Describer**: rejected and failed outcomes carry the message into `OperationEnd`; succeeded outcomes do not.
- **Dashboard API** (in-memory `TestServer`):
  - GET settings for missing, valid, and invalid files;
  - PUT without the header, with a foreign `Origin`, or with a wrong content type returns `403`;
  - PUT on any other route, and POST/DELETE anywhere, return `405`;
  - an invalid body returns `400` and leaves the file unchanged;
  - `port` in the body is ignored;
  - an invalid file requires `?overwriteInvalid=true`;
  - the route-table test changes from "GET only" to "GET only, plus one PUT on `/api/settings`";
  - profiles return no password values and never read environment variables (a test sets the named variable and checks that its value is not in the response).
- **UI (Vitest)**: `describeError` mapping; badge tooltip and dialog with and without a message; `SqlBlock` formatting, fallback on formatter error, and toggle; theme mode persistence and `localStorage` failure; settings confirmation dialog only on enabling `storeSensitive`; profiles empty and invalid states.
- **Gates**: `pwsh ./scripts/verify.ps1` and `pwsh ./scripts/verify-linux.ps1` both green.

## Documentation

- `AGENTS.md`: the dashboard bullet says the dashboard can edit operator settings in `config.json` and shows profiles read-only; the journal bullet says error messages are stored only with `storeSensitive`.
- `2026-10-06-activity-dashboard-design.md`: a short note pointing to this design for the changed read-only and styling rules.

## Out of scope

- Editing, adding, or deleting profiles.
- Applying settings to running processes (hot reload).
- Showing error messages that were recorded before this change; those rows only have `error_kind`.
