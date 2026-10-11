# Model Prices, Settings Autosave, Persistent Dashboard Token and `gain` Removal — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show estimated token savings in USD in the dashboard from a models.dev price snapshot refreshable in Settings; autosave Settings safely; make the dashboard bookmarkable with tabs that recover after restarts; remove `gain` from the CLI and MCP.

**Architecture:** Core gains a `Pricing` unit (embedded snapshot, optional refreshed file, lookup). The dashboard reader values paired token footprints with it and exposes one stats field; a new guarded `POST /api/prices/refresh` rewrites the refreshed file. The dashboard token becomes a persistent owner-only file with a long-lived cookie and a same-origin hand-off page; the SPA retries network failures with a reconnect banner. Settings save per committed change through the existing `PUT`. `gain` is deleted end to end; tests use `sqlharness_plan` with the fixture plan as their journaled offline call.

**Tech Stack:** .NET (net8/net10), ASP.NET Core minimal APIs, xUnit, Microsoft.Data.Sqlite, React + TypeScript + TanStack Query + vitest, PowerShell 7.

**Spec:** `docs/superpowers/specs/2026-10-11-model-prices-settings-design.md`

**Sequencing:** start only after `feat/mcp-session-metadata` has merged (it adds `operations.agent_model`, agent kinds, and changes the acceptance script and MCP tests this plan edits). Branch from current `main`.

## Global Constraints

- Price source `https://models.dev/api.json`; snapshot path `src/SqlHarness.Core/Pricing/model-prices.json` (embedded resource); refreshed copy `<SQLHARNESS_HOME>/data/model-prices.json`, owner-only, atomic write.
- Valuation: (`raw_tokens` − `emitted_tokens`) × input USD per 1M / 1e6, current prices only, paired footprints only; negative savings count as negative.
- Lookup order: `provider/model` id → agent-kind provider (`claude`→`anthropic`, `codex`→`openai`, `grok`→`xai`) → primary providers `anthropic`, `openai`, `google`, `xai`; never a reseller by fallback.
- Refresh: only via `POST /api/prices/refresh` with the same protections as `PUT /api/settings`; 30 s timeout; 20 MB response cap; fixed content-free error text; no CLI command; no automatic network access.
- Dashboard token: `<SQLHARNESS_HOME>/dashboard-token`, owner-only, reused across starts; cookie `HttpOnly`, `SameSite=Strict`, `Path=/`, `Max-Age` 400 days; `?t=` with a valid token answers 200 with a hand-off page (meta refresh, no inline script — CSP is `script-src 'self'`).
- Loopback binding, `Host` check, write header + `Origin` checks, browser launch through the owner-only local file, and printing the token URL on start stay unchanged.
- Settings: toggles save on change; numbers on blur/Enter when valid; confirmation for enabling retention or lowering `maxAgeDays`/`maxSizeMb` while retention is on; saves serialised; no Save button.
- CLI keeps agent commands plus `dashboard`; `gain` is removed from CLI and MCP (MCP lists 10 tools).
- Gates, one after the other: `pwsh ./scripts/verify.ps1`, then `pwsh ./scripts/verify-linux.ps1`.

## Review Focus

1. A refreshed price file whose `fetchedAt` is older than the embedded snapshot (a new release shipped newer prices) — expected: embedded snapshot wins (Task 3 `Newer_embedded_snapshot_wins_over_older_refresh`).
2. Two rapid toggle changes where the first request is slower — expected: the file ends with the second value (Task 6 `Queued_saves_apply_in_order`).
3. Typing `300` into Maximum age — expected: no request until blur/Enter, then one request with 300 (Task 6 `Number_field_saves_only_on_commit`).
4. Dashboard token file deleted while a tab is open — expected: the tab shows the "open with sqlharness dashboard" message instead of retrying forever (Task 2 `Unauthorized_stops_retrying`).
5. A model id present only at a reseller (for example `azure/gpt-x`) — expected: unpriced, never valued at the reseller's price (Task 3 `Reseller_only_model_is_unpriced`).

---

### Task 1: Persistent dashboard token, long-lived cookie, same-origin hand-off

**Files:**
- Create: `src/SqlHarness.Dashboard/DashboardToken.cs`
- Modify: `src/SqlHarness.Dashboard/DashboardServer.cs` (`DashboardServerOptions.Token`, `StartAsync`)
- Modify: `src/SqlHarness.Dashboard/DashboardHost.cs` (load the token before `StartAsync`)
- Modify: `src/SqlHarness.Dashboard/DashboardSecurity.cs` (cookie options, hand-off response)
- Test: `tests/SqlHarness.Tests/Dashboard/DashboardServerTests.cs`, `tests/SqlHarness.Tests/Dashboard/DashboardHostTests.cs`

**Interfaces:**
- Produces: `internal static class DashboardToken { string LoadOrCreate(string home); const string FileName = "dashboard-token"; }`; `DashboardServerOptions.Token : string?` (null → fresh random token, kept for tests); `DashboardSecurity.CookieMaxAge = TimeSpan.FromDays(400)`.

- [ ] **Step 1: Write the failing tests**

`DashboardServerTests` — replace `Token_exchange_sets_a_strict_http_only_cookie_and_redirects_without_the_token` with:

```csharp
    [Fact]
    public async Task Token_exchange_answers_a_same_origin_hand_off_page_with_a_long_lived_cookie()
    {
        using var home = new TempHome();
        await using var server = await DashboardServer.StartAsync(new DashboardServerOptions(home.DatabasePath, 0, new FakeProcesses()), CancellationToken.None);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = server.BaseUri };

        var response = await client.GetAsync($"/sessions?t={server.Token}&x=1");

        // Not a redirect: a redirect would continue the browser's cross-site navigation from the
        // file:// launch page, and SameSite=Strict would withhold the cookie on it (401 today).
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("http-equiv=\"refresh\"", body);
        Assert.Contains("url=/sessions?x=1", body);
        Assert.DoesNotContain(server.Token, body);
        Assert.DoesNotContain("<script", body, StringComparison.OrdinalIgnoreCase);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.Contains(DashboardSecurity.CookieName + "=", cookie);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max-age=34560000", cookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Supplied_token_is_used_and_survives_a_restart()
    {
        using var home = new TempHome();
        var options = new DashboardServerOptions(home.DatabasePath, 0, new FakeProcesses()) { Token = "fixed-token-value-123" };
        var cookies = new CookieContainer();
        await using (var server = await DashboardServer.StartAsync(options, CancellationToken.None))
        {
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = cookies }) { BaseAddress = server.BaseUri };
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/?t=fixed-token-value-123")).StatusCode);
        }

        await using (var restarted = await DashboardServer.StartAsync(options, CancellationToken.None))
        {
            // The cookie from the first run authenticates against the restarted server (new port, same token).
            foreach (Cookie cookie in cookies.GetAllCookies())
                cookies.Add(new Cookie(cookie.Name, cookie.Value, "/", restarted.BaseUri.Host));
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, CookieContainer = cookies }) { BaseAddress = restarted.BaseUri };
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/sessions")).StatusCode);
        }
    }
```

Update `StartAuthenticated` to expect `HttpStatusCode.OK` instead of `Redirect`, and update every other test in the file that asserted the `302` from the token exchange.

`DashboardHostTests` — add (use the file's existing host-start helper and temp home):

```csharp
    [Fact]
    public void Token_file_is_created_once_and_reused()
    {
        using var home = new TempHome();

        var first = DashboardToken.LoadOrCreate(home.Path);
        var second = DashboardToken.LoadOrCreate(home.Path);

        Assert.Equal(first, second);
        Assert.True(first.Length >= 40);
        Assert.Equal(first, File.ReadAllText(Path.Combine(home.Path, DashboardToken.FileName)).Trim());
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(home.Path, DashboardToken.FileName)));
    }

    [Fact]
    public void Deleting_the_token_file_rotates_the_token()
    {
        using var home = new TempHome();
        var first = DashboardToken.LoadOrCreate(home.Path);
        File.Delete(Path.Combine(home.Path, DashboardToken.FileName));

        Assert.NotEqual(first, DashboardToken.LoadOrCreate(home.Path));
    }
```

(If `TempHome` exposes the home directory under a different property name than `Path`, use that name.)

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Dashboard"`
Expected: compile errors (`DashboardToken`, `DashboardServerOptions.Token`), then the hand-off test fails on `302`.

- [ ] **Step 3: Implement**

`src/SqlHarness.Dashboard/DashboardToken.cs`:

```csharp
namespace SqlHarness.Dashboard;

/// <summary>
/// The dashboard token persists in the SQLHarness home (owner-only), so a cookie from an
/// earlier start keeps working after restarts and autostarts. Deleting the file rotates it.
/// It protects the journal from other local OS users; Host/Origin/header checks protect
/// against web pages.
/// </summary>
internal static class DashboardToken
{
    internal const string FileName = "dashboard-token";

    internal static string LoadOrCreate(string home)
    {
        ArgumentException.ThrowIfNullOrEmpty(home);
        var path = Path.Combine(home, FileName);
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (existing.Length >= 40)
                return existing;
        }

        var token = DashboardSecurity.NewToken();
        DashboardLock.WriteOwnerOnly(path, token);
        return token;
    }
}
```

`DashboardServerOptions`: add

```csharp
    /// <summary>The persistent token; null generates a fresh one (tests).</summary>
    public string? Token { get; init; }
```

and in `StartAsync` use `var token = options.Token ?? DashboardSecurity.NewToken();`.

`DashboardHost.RunCoreAsync`: before `DashboardServer.StartAsync`, read the token inside the existing local-storage error handling:

```csharp
        string token;
        try
        {
            token = DashboardToken.LoadOrCreate(options.Home);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            options.Error.WriteLine("sqlharness: the dashboard could not read or create its token in the SQLHarness home.");
            return (int)SqlHarnessExitCode.LocalStorage;
        }
```

and pass `Token = token` in the `DashboardServerOptions` initializer.

`DashboardSecurity`: add `internal static readonly TimeSpan CookieMaxAge = TimeSpan.FromDays(400);`, set `MaxAge = CookieMaxAge` in the cookie options, and replace the redirect after a valid `?t=` with a hand-off page:

```csharp
            var target = new PathString(path).ToUriComponent() + query.ToQueryString();
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html; charset=utf-8";
            var encoded = System.Net.WebUtility.HtmlEncode(target);
            // A same-origin navigation from this page sends the Strict cookie; a redirect would
            // continue the cross-site navigation that started on the file:// launch page.
            await context.Response.WriteAsync($"""
                <!doctype html><html><head><meta charset="utf-8"><meta name="referrer" content="no-referrer">
                <meta http-equiv="refresh" content="0;url={encoded}"><title>SQLHarness dashboard</title></head>
                <body><p><a href="{encoded}">Open the SQLHarness dashboard</a></p></body></html>
                """);
            return;
```

Update the class summary ("one-time ?t= token exchange" → "persistent ?t= token exchanged into a long-lived HttpOnly SameSite=Strict cookie through a same-origin hand-off page").

- [ ] **Step 4: Run the dashboard tests**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Dashboard"`
Expected: PASS.

- [ ] **Step 5: Manual browser check (the SameSite behaviour cannot be unit-tested)**

Publish a worktree build, run `<build> dashboard`, and confirm the auto-opened browser lands on the dashboard (not on "Open the dashboard with: sqlharness dashboard"). Stop it, start it again, and confirm the already open tab and a bookmark of `http://127.0.0.1:<port>/` work without the token URL (after Task 2 the tab also recovers without a reload). Record the result in the PR.

- [ ] **Step 6: Commit**

```bash
git add src/SqlHarness.Dashboard tests/SqlHarness.Tests/Dashboard
git commit -m "fix(dashboard): persistent token, long-lived cookie and same-origin hand-off"
```

---

### Task 2: Tabs recover after a dashboard restart

**Files:**
- Modify: `src/SqlHarness.Dashboard/ui/src/api/client.ts` (`NetworkError`)
- Modify: `src/SqlHarness.Dashboard/ui/src/api/queries.ts` (retry policy)
- Create: `src/SqlHarness.Dashboard/ui/src/api/connection.ts`, `src/SqlHarness.Dashboard/ui/src/api/connection.test.ts`
- Create: `src/SqlHarness.Dashboard/ui/src/components/ConnectionBanner.tsx`, `components/ConnectionBanner.test.tsx`
- Modify: the app layout component that renders the navigation (search for the component that renders the page `<Outlet />` / routes) to include `<ConnectionBanner />`
- Modify: `src/SqlHarness.Dashboard/ui/src/live/useLiveFeed.ts` (report connection state)

**Interfaces:**
- Produces: `class NetworkError extends Error`; `connection` store with `reportFailure()`, `reportSuccess()`, `useConnectionState(): "online" | "reconnecting"`; `retryDelay(attempt) = min(1000 * 2 ** attempt, 30000)`.

- [ ] **Step 1: Write the failing tests**

`api/connection.test.ts`:

```ts
import { expect, test } from "vitest"
import { NetworkError, UnauthorizedError } from "@/api/client"
import { retryDelay, shouldRetry } from "@/api/queries"

test("network errors retry without limit, others as before", () => {
  expect(shouldRetry(10, new NetworkError("down"))).toBe(true)
  expect(shouldRetry(0, new UnauthorizedError())).toBe(false)
  expect(shouldRetry(1, new Error("500"))).toBe(true)
  expect(shouldRetry(2, new Error("500"))).toBe(false)
})

test("retry delay grows and caps at 30 s", () => {
  expect(retryDelay(0)).toBe(1000)
  expect(retryDelay(3)).toBe(8000)
  expect(retryDelay(10)).toBe(30000)
})
```

`components/ConnectionBanner.test.tsx` (follow the file conventions of other component tests):

```tsx
import { act, render, screen } from "@testing-library/react"
import { expect, test } from "vitest"
import { connection } from "@/api/connection"
import { ConnectionBanner } from "@/components/ConnectionBanner"

test("shows a reconnecting banner on failure and hides it on success", () => {
  render(<ConnectionBanner />)
  expect(screen.queryByRole("status")).not.toBeInTheDocument()
  act(() => connection.reportFailure())
  expect(screen.getByRole("status")).toHaveTextContent("Dashboard unavailable — reconnecting")
  expect(screen.getByRole("status")).toHaveTextContent("sqlharness dashboard")
  act(() => connection.reportSuccess())
  expect(screen.queryByRole("status")).not.toBeInTheDocument()
})
```

Review Focus item 4 (`Unauthorized_stops_retrying`) is covered by the first test (`shouldRetry(0, new UnauthorizedError()) === false`) — `ErrorState` already renders the "open with sqlharness dashboard" message for `UnauthorizedError`.

- [ ] **Step 2: Run to verify failure**

Run: `npm run check --prefix src/SqlHarness.Dashboard/ui`
Expected: missing exports.

- [ ] **Step 3: Implement**

`client.ts`: add `export class NetworkError extends Error {}` and wrap every `fetch(...)` call in `getJson`/`putJson` (and any other fetch helper in the file):

```ts
async function send(url: string, init?: RequestInit): Promise<Response> {
  try {
    const response = await fetch(url, init)
    connection.reportSuccess()
    return response
  } catch {
    connection.reportFailure()
    throw new NetworkError(`Dashboard unreachable: ${url}`)
  }
}
```

(import `connection` from `@/api/connection`; replace the direct `fetch` calls with `send`).

`api/connection.ts`:

```ts
import { useSyncExternalStore } from "react"

type State = "online" | "reconnecting"
let state: State = "online"
const listeners = new Set<() => void>()
const set = (next: State) => { if (state !== next) { state = next; listeners.forEach(l => l()) } }

export const connection = {
  reportFailure: () => set("reconnecting"),
  reportSuccess: () => set("online"),
  subscribe: (listener: () => void) => { listeners.add(listener); return () => listeners.delete(listener) },
  get: () => state,
}

export function useConnectionState(): State {
  return useSyncExternalStore(connection.subscribe, connection.get, connection.get)
}
```

`queries.ts`:

```ts
export const shouldRetry = (count: number, error: unknown) =>
  error instanceof NetworkError ? true
  : !(error instanceof UnauthorizedError || error instanceof NotFoundError) && count < 2

export const retryDelay = (attempt: number) => Math.min(1000 * 2 ** attempt, 30_000)
```

and use them in `defaultOptions.queries` (`retry: shouldRetry, retryDelay`). Mutations keep `retry: false` (settings saves surface their own error).

`components/ConnectionBanner.tsx`:

```tsx
import { useConnectionState } from "@/api/connection"

export function ConnectionBanner() {
  if (useConnectionState() === "online") return null
  return (
    <div role="status" className="border-b bg-muted px-4 py-2 text-sm">
      Dashboard unavailable — reconnecting. If it stays unavailable, start it with <code>sqlharness dashboard</code>{" "}
      or open a new <code>mcp serve</code> session with dashboard autostart.
    </div>
  )
}
```

Render it at the top of the app layout.

`useLiveFeed.ts`: call `connection.reportSuccess()` in `onopen` and `connection.reportFailure()` in `onerror` when `readyState !== EventSource.OPEN`. EventSource already reconnects by itself on network errors; with the persistent token the reconnect succeeds after a restart. Update the comment that mentions "a 401 after the dashboard restarted with a new token" to say the token is persistent and a 401 now means it was rotated.

- [ ] **Step 4: Run the UI checks**

Run: `npm run check --prefix src/SqlHarness.Dashboard/ui`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SqlHarness.Dashboard/ui/src
git commit -m "feat(dashboard-ui): retry through restarts with a reconnect banner"
```

---

### Task 3: Model price table in Core

**Files:**
- Create: `src/SqlHarness.Core/Pricing/ModelPrices.cs`
- Create: `src/SqlHarness.Core/Pricing/model-prices.json` (generated by Step 4)
- Create: `scripts/update-model-prices.ps1`
- Modify: `src/SqlHarness.Core/SqlHarness.Core.csproj` (embed the JSON)
- Test: `tests/SqlHarness.Tests/Pricing/ModelPricesTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record ModelPrice(string Provider, string Model, decimal InputPerMillion);`
  - `public sealed record ModelPriceTable(string Source, DateTimeOffset FetchedAt, IReadOnlyDictionary<string, IReadOnlyDictionary<string, decimal>> InputPerMillion)` with `public ModelPrice? Find(string? agentKind, string? model)`.
  - `public static class ModelPrices { ModelPriceTable Load(string refreshedPath); ModelPriceTable Embedded { get; } ModelPriceTable Parse(Stream json); string Extract(Stream modelsDevApiJson, DateTimeOffset fetchedAt); }`
  - `ModelPrices.PrimaryProviders = ["anthropic", "openai", "google", "xai"]`.

- [ ] **Step 1: Write the failing tests**

`tests/SqlHarness.Tests/Pricing/ModelPricesTests.cs`:

```csharp
using System.Text;

using SqlHarness.Core;

namespace SqlHarness.Tests.Pricing;

public sealed class ModelPricesTests
{
    private static ModelPriceTable Table(string providersJson, string fetchedAt = "2026-10-11T00:00:00Z") =>
        ModelPrices.Parse(new MemoryStream(Encoding.UTF8.GetBytes(
            $$"""{"source":"models.dev","fetchedAt":"{{fetchedAt}}","providers":{{providersJson}}}""")));

    private static readonly ModelPriceTable Sample = Table("""
        {"anthropic":{"claude-haiku-5-5":{"input":0.1,"output":0.5}},
         "openai":{"gpt-6-sol":{"input":2,"output":10}},
         "xai":{"grok-4.7":{"input":2,"output":6}},
         "google":{"gemini-3.8-flash":{"input":0.75,"output":3.75}},
         "opencode":{"step-5-preview-free":{"input":0,"output":0},"claude-opus-5-5":{"input":4,"output":20}},
         "azure":{"gpt-x":{"input":9,"output":9}},
         "amazon-bedrock":{"claude-haiku-5-5":{"input":0.11,"output":0.55}}}
        """);

    [Theory]
    [InlineData("codex", "gpt-6-sol", "openai", 2)]
    [InlineData("claude", "claude-haiku-5-5", "anthropic", 0.1)]
    [InlineData("grok", "grok-4.7", "xai", 2)]
    [InlineData("opencode", "opencode/step-5-preview-free", "opencode", 0)]
    [InlineData("opencode", "opencode/claude-opus-5-5", "opencode", 4)]
    [InlineData("cursor", "gemini-3.8-flash", "google", 0.75)]
    [InlineData(null, "claude-haiku-5-5", "anthropic", 0.1)]
    public void Lookup_follows_the_documented_order(string? kind, string model, string provider, double input)
    {
        var price = Sample.Find(kind, model);

        Assert.NotNull(price);
        Assert.Equal(provider, price.Provider);
        Assert.Equal((decimal)input, price.InputPerMillion);
    }

    [Fact]
    public void Reseller_only_model_is_unpriced() => Assert.Null(Sample.Find("cursor", "gpt-x"));

    [Fact]
    public void Unknown_or_missing_model_is_unpriced()
    {
        Assert.Null(Sample.Find("codex", "gpt-nope"));
        Assert.Null(Sample.Find("codex", null));
        Assert.Null(Sample.Find("codex", " "));
    }

    [Fact]
    public void Embedded_snapshot_parses_and_prices_known_models()
    {
        var embedded = ModelPrices.Embedded;

        Assert.Equal("models.dev", embedded.Source);
        Assert.NotNull(embedded.Find("claude", "claude-haiku-5-5"));
        Assert.NotNull(embedded.Find("codex", "gpt-6-sol"));
    }

    [Fact]
    public void Newer_embedded_snapshot_wins_over_older_refresh()
    {
        using var temp = new Journal.JournalTempDirectory();
        var path = Path.Combine(temp.Path, "model-prices.json");
        File.WriteAllText(path, """{"source":"models.dev","fetchedAt":"2000-01-01T00:00:00Z","providers":{}}""");

        Assert.Equal(ModelPrices.Embedded.FetchedAt, ModelPrices.Load(path).FetchedAt);
    }

    [Fact]
    public void Newer_valid_refresh_wins_and_malformed_refresh_falls_back()
    {
        using var temp = new Journal.JournalTempDirectory();
        var path = Path.Combine(temp.Path, "model-prices.json");
        File.WriteAllText(path, """{"source":"models.dev","fetchedAt":"2999-01-01T00:00:00Z","providers":{"openai":{"gpt-6-sol":{"input":1}}}}""");
        Assert.Equal(1m, ModelPrices.Load(path).Find("codex", "gpt-6-sol")!.InputPerMillion);

        File.WriteAllText(path, "{ not json");
        Assert.Equal(ModelPrices.Embedded.FetchedAt, ModelPrices.Load(path).FetchedAt);
    }

    [Fact]
    public void Extract_keeps_only_cost_fields_and_models_with_cost()
    {
        var api = """
            {"openai":{"id":"openai","name":"OpenAI","models":{
               "gpt-6-sol":{"id":"gpt-6-sol","name":"x","cost":{"input":2,"output":10,"cache_read":0.2}},
               "tts":{"id":"tts","name":"y"}}}}
            """;

        var json = ModelPrices.Extract(new MemoryStream(Encoding.UTF8.GetBytes(api)), DateTimeOffset.Parse("2026-10-11T00:00:00Z"));
        var table = ModelPrices.Parse(new MemoryStream(Encoding.UTF8.GetBytes(json)));

        Assert.Equal(2m, table.Find("codex", "gpt-6-sol")!.InputPerMillion);
        Assert.Null(table.Find("codex", "tts"));
        Assert.DoesNotContain("\"name\"", json);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~ModelPricesTests"`
Expected: compile errors.

- [ ] **Step 3: Implement `ModelPrices.cs`**

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SqlHarness.Core;

public sealed record ModelPrice(string Provider, string Model, decimal InputPerMillion);

/// <summary>Input prices in USD per 1M tokens by provider and model (models.dev ids).</summary>
public sealed record ModelPriceTable(
    string Source,
    DateTimeOffset FetchedAt,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, decimal>> InputPerMillion)
{
    public ModelPrice? Find(string? agentKind, string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return null;
        var slash = model.IndexOf('/');
        if (slash > 0 && Get(model[..slash], model[(slash + 1)..]) is { } qualified)
            return qualified;
        if (ModelPrices.ProviderForAgent(agentKind) is { } provider && Get(provider, model) is { } byAgent)
            return byAgent;
        foreach (var primary in ModelPrices.PrimaryProviders)
        {
            if (Get(primary, model) is { } found)
                return found;
        }

        return null;
    }

    private ModelPrice? Get(string provider, string model) =>
        InputPerMillion.TryGetValue(provider, out var models) && models.TryGetValue(model, out var input)
            ? new ModelPrice(provider, model, input)
            : null;
}

public static class ModelPrices
{
    public static readonly IReadOnlyList<string> PrimaryProviders = ["anthropic", "openai", "google", "xai"];

    private static readonly Lazy<ModelPriceTable> EmbeddedTable = new(() =>
    {
        using var stream = typeof(ModelPrices).Assembly.GetManifestResourceStream("SqlHarness.Core.Pricing.model-prices.json")
            ?? throw new InvalidOperationException("The embedded model price snapshot is missing.");
        return Parse(stream);
    });

    public static ModelPriceTable Embedded => EmbeddedTable.Value;

    internal static string? ProviderForAgent(string? agentKind) => agentKind switch
    {
        "claude" => "anthropic",
        "codex" => "openai",
        "grok" => "xai",
        _ => null,
    };

    /// <summary>The refreshed file when it parses and is newer than the embedded snapshot; otherwise the snapshot.</summary>
    public static ModelPriceTable Load(string refreshedPath)
    {
        try
        {
            if (File.Exists(refreshedPath))
            {
                using var stream = File.OpenRead(refreshedPath);
                var refreshed = Parse(stream);
                if (refreshed.FetchedAt > Embedded.FetchedAt)
                    return refreshed;
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException)
        {
        }

        return Embedded;
    }

    public static ModelPriceTable Parse(Stream json)
    {
        var root = JsonNode.Parse(json)?.AsObject() ?? throw new JsonException("Empty price table.");
        var source = root["source"]?.GetValue<string>() ?? throw new JsonException("Missing source.");
        var fetchedAt = DateTimeOffset.Parse(root["fetchedAt"]?.GetValue<string>() ?? throw new JsonException("Missing fetchedAt."), System.Globalization.CultureInfo.InvariantCulture);
        var providers = new Dictionary<string, IReadOnlyDictionary<string, decimal>>(StringComparer.Ordinal);
        foreach (var (provider, modelsNode) in root["providers"]?.AsObject() ?? throw new JsonException("Missing providers."))
        {
            var models = new Dictionary<string, decimal>(StringComparer.Ordinal);
            foreach (var (model, cost) in modelsNode?.AsObject() ?? [])
            {
                if (cost?["input"] is JsonValue input && input.TryGetValue<decimal>(out var value) && value >= 0)
                    models[model] = value;
            }

            providers[provider] = models;
        }

        return new ModelPriceTable(source, fetchedAt, providers);
    }

    /// <summary>Reduces models.dev api.json to the snapshot shape: cost fields of models that have a cost.</summary>
    public static string Extract(Stream modelsDevApiJson, DateTimeOffset fetchedAt)
    {
        var api = JsonNode.Parse(modelsDevApiJson)?.AsObject() ?? throw new JsonException("Empty models.dev document.");
        var providers = new JsonObject();
        foreach (var (provider, node) in api)
        {
            var models = new JsonObject();
            foreach (var (model, info) in node?["models"]?.AsObject() ?? [])
            {
                if (info?["cost"] is JsonObject cost)
                    models[model] = cost.DeepClone();
            }

            if (models.Count > 0)
                providers[provider] = models;
        }

        var snapshot = new JsonObject
        {
            ["source"] = "models.dev",
            ["fetchedAt"] = fetchedAt.ToUniversalTime().ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["providers"] = providers,
        };
        return snapshot.ToJsonString();
    }
}
```

`SqlHarness.Core.csproj`:

```xml
  <ItemGroup>
    <EmbeddedResource Include="Pricing/model-prices.json" LogicalName="SqlHarness.Core.Pricing.model-prices.json" />
  </ItemGroup>
```

- [ ] **Step 4: Generate the snapshot**

`scripts/update-model-prices.ps1` (same reduction as `ModelPrices.Extract`; the embedded-snapshot test validates the shape):

```powershell
#Requires -Version 7.0
<#
.SYNOPSIS
    Regenerates src/SqlHarness.Core/Pricing/model-prices.json from models.dev before a release.
.DESCRIPTION
    Keeps only each model's cost object; review the diff like any other change. Builds never download prices.
#>
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$api = Invoke-RestMethod -Uri 'https://models.dev/api.json' -TimeoutSec 60
$providers = [ordered]@{}
foreach ($provider in ($api.PSObject.Properties | Sort-Object Name)) {
    $models = [ordered]@{}
    foreach ($model in ($provider.Value.models.PSObject.Properties | Sort-Object Name)) {
        if ($null -ne $model.Value.cost) { $models[$model.Name] = $model.Value.cost }
    }
    if ($models.Count -gt 0) { $providers[$provider.Name] = $models }
}
$snapshot = [ordered]@{ source = 'models.dev'; fetchedAt = [DateTimeOffset]::UtcNow.ToString('O'); providers = $providers }
$path = Join-Path $repo 'src/SqlHarness.Core/Pricing/model-prices.json'
$snapshot | ConvertTo-Json -Depth 12 -Compress | Set-Content -LiteralPath $path -Encoding utf8NoBOM
Write-Host "Wrote $path"
```

Run: `pwsh ./scripts/update-model-prices.ps1` — expected: a file of roughly 0.6 MB.

- [ ] **Step 5: Run the tests**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~ModelPricesTests"`
Expected: PASS. If `Embedded_snapshot_parses_and_prices_known_models` fails because a model id changed upstream, pick ids that exist in the generated snapshot and keep the test's intent (one Anthropic and one OpenAI model priced).

- [ ] **Step 6: Commit**

```bash
git add src/SqlHarness.Core scripts/update-model-prices.ps1 tests/SqlHarness.Tests/Pricing
git commit -m "feat(core): model price snapshot from models.dev with provider-aware lookup"
```

---

### Task 4: Savings in USD on the Statistics page

**Files:**
- Modify: `src/SqlHarness.Dashboard/DashboardModels.cs` (`SavingsStat`, `ModelSaving`, `DashboardStats.Savings`)
- Modify: `src/SqlHarness.Dashboard/JournalReader.cs` (`Stats` computes savings; constructor gets the price table provider)
- Modify: `src/SqlHarness.Dashboard/DashboardServer.cs` (pass `() => ModelPrices.Load(options.PricesPath)` to the reader; `DashboardServerOptions.PricesPath`)
- Modify: `src/SqlHarness.Dashboard/DashboardHost.cs` (`PricesPath = Path.Combine(options.Home, "data", "model-prices.json")`)
- Modify: `src/SqlHarness.Dashboard/ui/src/api/types.ts`, `ui/src/test/fixtures.ts`, `ui/src/pages/StatsPage.tsx`
- Test: `tests/SqlHarness.Tests/Dashboard/JournalReaderTests.cs`, `DashboardContractTests.cs`, `ui/src/pages/StatsPage.test.tsx`

**Interfaces:**
- Consumes: `ModelPrices`, `ModelPriceTable.Find` (Task 3); `operations.agent_model` and `sessions.agent_kind` (session-metadata plan).
- Produces: `public sealed record ModelSaving(string Provider, string Model, int Operations, long SavedTokens, decimal Usd);` `public sealed record SavingsStat(string PriceSource, DateTimeOffset PricesFetchedAt, decimal TotalUsd, IReadOnlyList<ModelSaving> ByModel, int WithoutModel, int Unpriced);` `DashboardStats(..., SavingsStat Savings)` appended last; JSON `savings`.

- [ ] **Step 1: Write the failing tests**

`JournalReaderTests` — seed paired footprints (the seed's `SeedTokens.Both` writes raw 400 / emitted 40, i.e. 360 saved) on sessions of different kinds; set `agent_model` through the start record the session-metadata plan added (`OperationStart.Agent = new AgentCallMetadata(...)`) — extend `JournalSeed.Operation` with an optional `AgentCallMetadata? agent = null` passed into `OperationStart` if it does not have one yet:

```csharp
    [Fact]
    public void Savings_value_paired_tokens_at_current_input_prices()
    {
        using var home = new TempHome();
        var seed = new JournalSeed(home.DatabasePath);
        var codex = JournalSeed.Session("mcp:codex", "codex");
        seed.Operation(codex, agent: new AgentCallMetadata("c1", "gpt-6-sol", null, null, null, null, null));
        seed.Operation(codex, agent: new AgentCallMetadata("c2", "gpt-6-sol", null, null, null, null, null));
        seed.Operation(JournalSeed.Session("mcp:claude", "claude"));                                  // no model
        seed.Operation(codex, agent: new AgentCallMetadata("c3", "gpt-unknown", null, null, null, null, null)); // unpriced
        var prices = new ModelPriceTable("models.dev", DateTimeOffset.Parse("2026-10-11T00:00:00Z"),
            new Dictionary<string, IReadOnlyDictionary<string, decimal>> { ["openai"] = new Dictionary<string, decimal> { ["gpt-6-sol"] = 2m } });

        var savings = new JournalReader(home.DatabasePath, new FakeProcesses(), () => prices).Stats(new StatsQuery()).Savings;

        var row = Assert.Single(savings.ByModel);
        Assert.Equal(("openai", "gpt-6-sol", 2, 720L), (row.Provider, row.Model, row.Operations, row.SavedTokens));
        Assert.Equal(720m * 2m / 1_000_000m, row.Usd);
        Assert.Equal(row.Usd, savings.TotalUsd);
        Assert.Equal(1, savings.WithoutModel);
        Assert.Equal(1, savings.Unpriced);
        Assert.Equal("models.dev", savings.PriceSource);
    }
```

`DashboardContractTests`: append the `savings` name to the `DashboardStats` expected names and construct it with a `SavingsStat` value in the constructor call.

`StatsPage.test.tsx`: render stats with `savings: { priceSource: "models.dev", pricesFetchedAt: "2026-10-11T00:00:00Z", totalUsd: 0.00144, byModel: [{ provider: "openai", model: "gpt-6-sol", operations: 2, savedTokens: 720, usd: 0.00144 }], withoutModel: 1, unpriced: 1 }` and assert the card shows `gpt-6-sol`, a USD total, "1 operation without a known model", "1 operation with an unpriced model", and "at current prices (models.dev, 2026-10-11)".

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Dashboard"`
Expected: compile errors.

- [ ] **Step 3: Implement**

`JournalReader`: add an optional constructor parameter `Func<ModelPriceTable>? prices = null` (default `() => ModelPrices.Embedded`). In `Stats`, after the token query and with the same time window:

```csharp
        var table = Try(prices) ?? ModelPrices.Embedded;
        var groups = Query(connection, $"""
            SELECT s.agent_kind, o.agent_model, COUNT(*), SUM(o.raw_tokens - o.emitted_tokens)
            FROM operations o JOIN sessions s ON s.id = o.session_id
            WHERE {inWindow} AND o.raw_tokens IS NOT NULL AND o.emitted_tokens IS NOT NULL
            GROUP BY s.agent_kind, o.agent_model;
            """, window, r => (Kind: r.GetString(0), Model: NullableString(r, 1), Count: r.GetInt32(2), Saved: r.GetInt64(3)));
        var byModel = new Dictionary<(string, string), (int Ops, long Saved, decimal Usd)>();
        int withoutModel = 0, unpriced = 0;
        foreach (var g in groups)
        {
            if (string.IsNullOrWhiteSpace(g.Model)) { withoutModel += g.Count; continue; }
            if (table.Find(g.Kind, g.Model) is not { } price) { unpriced += g.Count; continue; }
            var key = (price.Provider, price.Model);
            var current = byModel.GetValueOrDefault(key);
            byModel[key] = (current.Ops + g.Count, current.Saved + g.Saved, current.Usd + g.Saved * price.InputPerMillion / 1_000_000m);
        }
        var savings = new SavingsStat(table.Source, table.FetchedAt, byModel.Values.Sum(v => v.Usd),
            byModel.Select(p => new ModelSaving(p.Key.Item1, p.Key.Item2, p.Value.Ops, p.Value.Saved, p.Value.Usd))
                .OrderByDescending(m => m.Usd).ToArray(),
            withoutModel, unpriced);
```

(`Try` is a local `try { return f(); } catch { return null; }` helper; reuse an existing one if the reader has it.) Pass `savings` as the new last `DashboardStats` argument, including in the empty-database early return (use `new SavingsStat(ModelPrices.Embedded.Source, ModelPrices.Embedded.FetchedAt, 0, [], 0, 0)`).

`DashboardServerOptions`: `public string PricesPath { get; init; } = Path.Combine(SqlHarnessPaths.DataDirectory, "model-prices.json");` (use the existing paths helper's data directory; check `SqlHarnessPaths` for its name). Construct the reader with `() => ModelPrices.Load(options.PricesPath)`.

UI: add the types (`SavingsStat`, `ModelSaving`) to `types.ts`, a fixture default in `fixtures.ts`, and a "Savings (USD)" card to `StatsPage.tsx` using the page's existing card/table components: total formatted with `Intl.NumberFormat("en-US", { style: "currency", currency: "USD", maximumFractionDigits: 4 })`, a per-model table (model, provider, operations, saved tokens, USD), the two counts, and the footnote `at current prices (<source>, <yyyy-mm-dd>)`.

- [ ] **Step 4: Run the suites**

Run: `dotnet test tests/SqlHarness.Tests` and `npm run check --prefix src/SqlHarness.Dashboard/ui`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat(dashboard): estimated savings in USD at current model prices"
```

---

### Task 5: Refresh model prices from Settings

**Files:**
- Create: `src/SqlHarness.Dashboard/DashboardPrices.cs`
- Modify: `src/SqlHarness.Dashboard/DashboardSecurity.cs` (second guarded write)
- Modify: `src/SqlHarness.Dashboard/DashboardServer.cs` (map `GET /api/prices`, `POST /api/prices/refresh`; `DashboardServerOptions.PricesFetcher`)
- Modify: `src/SqlHarness.Dashboard/ui/src/api/client.ts` (`postJson`), `api/queries.ts` (`usePrices`, `useRefreshPrices`), `pages/SettingsPage.tsx`
- Test: `tests/SqlHarness.Tests/Dashboard/DashboardServerTests.cs`, `ui/src/pages/SettingsPage.test.tsx`

**Interfaces:**
- Produces: `GET /api/prices` → `{ source, fetchedAt, origin: "embedded" | "refreshed" }`; `POST /api/prices/refresh` → same shape on success, `502 { error: "Model prices could not be refreshed." }` on failure; `DashboardServerOptions.PricesFetcher : Func<CancellationToken, Task<Stream>>?` (null → HTTPS GET of models.dev); `DashboardSecurity.PricesRefreshPath = "/api/prices/refresh"`.

- [ ] **Step 1: Write the failing tests**

`DashboardServerTests` (reuse `StartAuthenticated`, extended to accept options overrides if needed):

```csharp
    [Fact]
    public async Task Price_refresh_requires_the_write_protections()
    {
        using var home = new TempHome();
        await using var dashboard = await StartAuthenticated(home);
        var (server, client) = dashboard;

        using var noHeader = new HttpRequestMessage(HttpMethod.Post, "/api/prices/refresh") { Content = JsonContent.Create(new { }) };
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(noHeader)).StatusCode);

        using var foreign = new HttpRequestMessage(HttpMethod.Post, "/api/prices/refresh") { Content = JsonContent.Create(new { }) };
        foreign.Headers.Add(DashboardSecurity.WriteHeader, "1");
        foreign.Headers.Add("Origin", "http://attacker.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(foreign)).StatusCode);

        using var anonymous = new HttpClient { BaseAddress = server.BaseUri };
        using var unauthenticated = new HttpRequestMessage(HttpMethod.Post, "/api/prices/refresh") { Content = JsonContent.Create(new { }) };
        unauthenticated.Headers.Add(DashboardSecurity.WriteHeader, "1");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.SendAsync(unauthenticated)).StatusCode);
    }

    [Fact]
    public async Task Price_refresh_writes_the_reduced_table_and_failure_keeps_it()
    {
        using var home = new TempHome();
        var pricesPath = Path.Combine(home.Path, "data", "model-prices.json");
        var answer = """{"openai":{"models":{"gpt-6-sol":{"name":"n","cost":{"input":1.5,"output":9}}}}}""";
        var fail = false;
        await using var dashboard = await StartAuthenticated(home, options => options with
        {
            PricesPath = pricesPath,
            PricesFetcher = _ => fail ? throw new HttpRequestException("down") : Task.FromResult<Stream>(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(answer))),
        });
        var (_, client) = dashboard;

        Assert.Equal(HttpStatusCode.OK, (await Refresh(client)).StatusCode);
        var written = File.ReadAllText(pricesPath);
        Assert.Contains("\"gpt-6-sol\"", written);
        Assert.DoesNotContain("\"name\"", written);

        fail = true;
        var failed = await Refresh(client);
        Assert.Equal(HttpStatusCode.BadGateway, failed.StatusCode);
        Assert.Equal(written, File.ReadAllText(pricesPath));
        Assert.DoesNotContain("down", await failed.Content.ReadAsStringAsync());

        static Task<HttpResponseMessage> Refresh(HttpClient client)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/prices/refresh") { Content = JsonContent.Create(new { }) };
            request.Headers.Add(DashboardSecurity.WriteHeader, "1");
            return client.SendAsync(request);
        }
    }
```

If `StartAuthenticated` cannot take an options transform, add an overload `StartAuthenticated(TempHome home, Func<DashboardServerOptions, DashboardServerOptions> configure)` used by these tests.

`SettingsPage.test.tsx`: stub `GET /api/prices` → `{ source: "models.dev", fetchedAt: "2026-10-11T00:00:00Z", origin: "embedded" }`; assert "Model prices: models.dev, 2026-10-11 (built in)"; click "Refresh model prices", stub the POST answer with `origin: "refreshed"` and a newer date, assert the text updates; a failing POST shows "Model prices could not be refreshed."

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~DashboardServerTests"`
Expected: 405 / 404 instead of the asserted statuses.

- [ ] **Step 3: Implement**

`DashboardSecurity`: add `internal const string PricesRefreshPath = "/api/prices/refresh";` and treat `POST` on that exact path like the settings `PUT`:

```csharp
        var isPricesRefresh = HttpMethods.IsPost(context.Request.Method)
            && string.Equals(context.Request.Path.Value, PricesRefreshPath, StringComparison.Ordinal);
        var isWrite = isSettingsWrite || isPricesRefresh;
        if (!isRead && !isWrite) { /* existing 405, Allow header unchanged */ }
        if (isWrite && !IsTrustedWrite(context.Request, allowedPort)) { /* existing 403 */ }
```

(rename the uses of `isSettingsWrite` that gate the trust check to `isWrite`; update the class summary to "two guarded writes").

`src/SqlHarness.Dashboard/DashboardPrices.cs`:

```csharp
using Microsoft.AspNetCore.Http;

using SqlHarness.Core;

namespace SqlHarness.Dashboard;

internal static class DashboardPrices
{
    internal const long MaximumResponseBytes = 20L * 1024 * 1024;
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    private const string FailureMessage = "Model prices could not be refreshed.";
    private static readonly HttpClient Http = new() { Timeout = Timeout };

    internal static object Describe(string pricesPath)
    {
        var table = ModelPrices.Load(pricesPath);
        var origin = table.FetchedAt == ModelPrices.Embedded.FetchedAt ? "embedded" : "refreshed";
        return new { source = table.Source, fetchedAt = table.FetchedAt, origin };
    }

    internal static async Task<Stream> FetchModelsDevAsync(CancellationToken ct)
    {
        using var response = await Http.GetAsync("https://models.dev/api.json", HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new InvalidDataException("Response too large.");
        var buffer = new MemoryStream();
        await using var body = await response.Content.ReadAsStreamAsync(ct);
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaximumResponseBytes)
                throw new InvalidDataException("Response too large.");
            buffer.Write(chunk, 0, read);
        }

        buffer.Position = 0;
        return buffer;
    }

    internal static async Task<IResult> RefreshAsync(
        string pricesPath, Func<CancellationToken, Task<Stream>> fetch, System.Text.Json.JsonSerializerOptions json, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            await using var source = await fetch(timeout.Token);
            var reduced = ModelPrices.Extract(source, DateTimeOffset.UtcNow);
            _ = ModelPrices.Parse(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(reduced)));
            Directory.CreateDirectory(Path.GetDirectoryName(pricesPath)!);
            DashboardLock.WriteOwnerOnly(pricesPath, reduced);
            return Results.Json(Describe(pricesPath), json);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return Results.Json(new { error = FailureMessage }, json, statusCode: StatusCodes.Status502BadGateway);
        }
    }
}
```

`DashboardServer`: `DashboardServerOptions.PricesFetcher` (`Func<CancellationToken, Task<Stream>>?`); map

```csharp
        MapRead(api, "/prices", () => Results.Json(DashboardPrices.Describe(options.PricesPath), Json));
        api.MapMethods("/prices/refresh", [HttpMethods.Post], (Delegate)((HttpContext context) =>
            DashboardPrices.RefreshAsync(options.PricesPath, options.PricesFetcher ?? DashboardPrices.FetchModelsDevAsync, Json, context.RequestAborted)));
```

before the `"/{**rest}"` catch-all.

UI: `client.ts` gains `postJson<T>(path, body)` mirroring `putJson` (same `X-SqlHarness-Dashboard: 1` header and JSON content type; 502 → `throw new Error(body.error)`); `queries.ts` gains `usePrices()` (`GET /api/prices`, key `["prices"]`) and `useRefreshPrices()` (mutation; `onSuccess` sets the `["prices"]` data and invalidates `["stats"]`). `SettingsPage.tsx` gets a "Model prices" card: the text `Model prices: <source>, <yyyy-mm-dd> (built in|refreshed)`, a "Refresh model prices" button disabled while pending, and the error message on failure.

- [ ] **Step 4: Run the suites**

Run: `dotnet test tests/SqlHarness.Tests --filter "FullyQualifiedName~Dashboard"` and `npm run check --prefix src/SqlHarness.Dashboard/ui`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src tests
git commit -m "feat(dashboard): refresh model prices from Settings"
```

---

### Task 6: Settings autosave with safeguards

**Files:**
- Modify: `src/SqlHarness.Dashboard/ui/src/pages/SettingsPage.tsx`
- Create: `src/SqlHarness.Dashboard/ui/src/lib/saveQueue.ts`, `lib/saveQueue.test.ts`
- Test: `src/SqlHarness.Dashboard/ui/src/pages/SettingsPage.test.tsx`

**Interfaces:**
- Produces: `createSaveQueue<T>(save: (value: T) => Promise<unknown>): { enqueue(value: T): Promise<void> }` — runs saves strictly one after another; `isDestructive(previous: Settings, next: Settings): boolean`.

- [ ] **Step 1: Write the failing tests**

`lib/saveQueue.test.ts`:

```ts
import { expect, test } from "vitest"
import { createSaveQueue } from "@/lib/saveQueue"

test("Queued_saves_apply_in_order", async () => {
  const applied: number[] = []
  const queue = createSaveQueue<number>(async value => {
    await new Promise(r => setTimeout(r, value === 1 ? 30 : 0))
    applied.push(value)
  })
  await Promise.all([queue.enqueue(1), queue.enqueue(2)])
  expect(applied).toEqual([1, 2])
})

test("a failed save does not block later saves", async () => {
  const applied: number[] = []
  const queue = createSaveQueue<number>(async value => { if (value === 1) throw new Error("x"); applied.push(value) })
  await expect(queue.enqueue(1)).rejects.toThrow("x")
  await queue.enqueue(2)
  expect(applied).toEqual([2])
})
```

`SettingsPage.test.tsx` (use the file's `stubFetch` helper and capture `PUT /api/settings` bodies; the existing tests show how):

- `Toggle_saves_immediately`: toggle "Start with mcp serve" → exactly one PUT with `dashboard.autoStart` flipped; status "Saved" shown; no button named "Save".
- `Number_field_saves_only_on_commit`: type `300` into "Maximum age (days)" (retention disabled) → no PUT; blur → one PUT with `maxAgeDays: 300`.
- `Invalid_number_is_not_sent`: type `0` and press Enter → no PUT; the field shows an error.
- `Enabling_retention_asks_for_confirmation`: toggle "Retention enabled" → dialog; Cancel → no PUT and the switch is off; confirm → one PUT.
- `Lowering_limits_with_retention_on_asks_for_confirmation`: with retention enabled and `maxAgeDays: 30`, change to `7` and blur → dialog; confirm → PUT with 7; Cancel → value back to 30, no PUT.
- Existing "Store sensitive data" confirmation keeps working and now saves on confirm.

- [ ] **Step 2: Run to verify failure**

Run: `npm run check --prefix src/SqlHarness.Dashboard/ui`
Expected: failures (Save button still present, no immediate PUT).

- [ ] **Step 3: Implement**

`lib/saveQueue.ts`:

```ts
/** Runs saves one after another so a slower earlier save never lands after a later one. */
export function createSaveQueue<T>(save: (value: T) => Promise<unknown>) {
  let tail: Promise<unknown> = Promise.resolve()
  return {
    enqueue(value: T): Promise<void> {
      const run = tail.then(() => save(value))
      tail = run.catch(() => undefined)
      return run.then(() => undefined)
    },
  }
}
```

`SettingsPage.tsx`:
- Keep `draft` local state for the fields being edited; on each committed change build the next full `Settings` and call `commit(next)`.
- `commit(next)`: if `isDestructive(saved, next)` open a confirmation dialog (reuse the page's existing `Dialog` pattern used for "Store sensitive data") with the text "This change can delete activity journal data the next time retention runs (dashboard start, hourly, or the next mcp serve). Continue?"; Cancel restores the draft to the saved value; confirm (or non-destructive) → `queue.enqueue(next)` where the queue's save is `save.mutateAsync({ settings: next })`.
- `isDestructive(previous, next)`: `(!previous.journal.retention.enabled && next.journal.retention.enabled) || (next.journal.retention.enabled && (next.journal.retention.maxAgeDays < previous.journal.retention.maxAgeDays || next.journal.retention.maxSizeMb < previous.journal.retention.maxSizeMb))`.
- `Toggle` calls `commit` in `onChange`; `NumberField` keeps typing local and calls `commit` on `onBlur` and on Enter (`onKeyDown`), only when the value is an integer inside the server's documented range (`maxAgeDays` 1–3650, `maxSizeMb` 10–102400, `idleShutdownHours` 1–168, matching `SqlHarnessConfig` validation); otherwise show the field error and do not commit.
- Per-card status line: "Saving…" while the queue runs a save for that card, "Saved" after success, the server's field error or a generic error on failure. Keep the note "New processes use these settings; restart running mcp serve sessions to apply them."
- Remove the Save button. Keep the invalid-`config.json` alert and its "Replace with these settings" action (it still calls `save` with `overwriteInvalid`).

- [ ] **Step 4: Run the UI checks**

Run: `npm run check --prefix src/SqlHarness.Dashboard/ui`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SqlHarness.Dashboard/ui/src
git commit -m "feat(dashboard-ui): autosave Settings with confirmation for destructive changes"
```

---

### Task 7: Remove `gain` from the CLI and MCP

**Files:**
- Delete: `src/SqlHarness.Core/Journal/JournalGainStore.cs`, the CLI gain command class (find with `grep -rn "class GainCommand" src`), `tests/SqlHarness.Tests/Cli/GainRenderTests.cs`, `tests/SqlHarness.Tests/Journal/JournalGainStoreTests.cs`, `tests/SqlHarness.Mcp.Tests/McpGainTests.cs`
- Modify: `src/SqlHarness.Cli/SqlHarnessCli.cs` (registration), `src/SqlHarness.Cli/Commands/SqlHarnessCommands.cs`, `src/SqlHarness.Core/Contracts.cs`, `Capabilities.cs`, `OperationReportContract.cs`, `SqlHarnessModule.cs`, `Journal/OperationJournalDescriber.cs`, `src/SqlHarness.Mcp/Tools/McpToolCatalog.cs` (`GainAsync`, `ToolNames`), `Tools/McpRequestToolHandlers.cs`
- Modify (tests): every file `grep -rln "sqlharness_gain\|GainOperation\|\"gain\"" tests` reports, plus `McpToolSchemaTests` (tool list of 10) and the `tools/list` budget test
- Modify: `scripts/mcp-client-acceptance.ps1`

**Interfaces:**
- Produces: `McpToolCatalog.ToolNames` has 10 entries; no `SqlHarnessGainOperation`; journaled offline test call = `sqlharness_plan` with `content` = `tests/SqlHarness.Tests/Fixtures/distiller-sample.sqlplan`.

- [ ] **Step 1: Delete the feature**

Remove the files listed under Delete; remove `gain` from CLI registration, MCP catalog (`GainAsync`, the `"sqlharness_gain"` entry), request-scope handlers, `SqlHarnessGainOperation` and its module branch, describer/report-contract cases, and the `capabilities` command list. Build: `dotnet build SqlHarness.sln` and fix every compile error by deleting gain-only code (never by stubbing it).

- [ ] **Step 2: Switch tests to a journaled offline call**

Tests that called `sqlharness_gain` only to get a harmless, journaled call (`McpJournalTests`, `McpClientIdentityTests`, `McpRevisionSmokeTests`, `McpStderrLeakRegressionTests`, `McpRootsTests`, …) call `sqlharness_plan` instead:

```csharp
    private static readonly string PlanXml = File.ReadAllText(McpStdioProcessHarness.FindRepositoryFile(
        "tests", "SqlHarness.Tests", "Fixtures", "distiller-sample.sqlplan"));
    // ...
    var result = await client.CallToolAsync("sqlharness_plan", new Dictionary<string, object?> { ["content"] = PlanXml }, cancellationToken: ct);
```

and assert journal rows with `operation = 'plan'` where they asserted `'gain'`. Tests that only need a successful offline call without a journal row may use `sqlharness_capabilities`. Update tool-count assertions (`McpToolSchemaTests`, catalog tests) from 11 to 10 and the expected name lists; rerun the `tools/list` budget test and keep its limit (the list only shrank).

- [ ] **Step 3: Acceptance script**

In `scripts/mcp-client-acceptance.ps1`:
- Copy `tests/SqlHarness.Tests/Fixtures/distiller-sample.sqlplan` into a per-run input root under `$work` and start every server with `mcp serve $Profile --input-root <that directory>`.
- Prompt: `Do not change any configuration or trust settings. Call sqlharness_capabilities from the acceptance MCP server exactly once, then call sqlharness_plan exactly once with file "<absolute path of the copied plan>", then reply with one word: done.`
- Journal check: query the row with `o.operation = 'plan'` instead of `'gain'`.

- [ ] **Step 4: Run the suites**

Run: `dotnet test SqlHarness.sln --filter "FullyQualifiedName!~Integration"`
Expected: PASS; `grep -rn "sqlharness_gain\|GainCommand\|JournalGainStore" src tests scripts` finds nothing.

- [ ] **Step 5: Commit**

```bash
git add -A src tests scripts
git commit -m "refactor: remove gain from the CLI and MCP; tests use sqlharness_plan as the journaled offline call"
```

---

### Task 8: Documentation and gates

**Files:**
- Modify: `AGENTS.md`, `docs/mcp.md`, `README.md`

- [ ] **Step 1: `AGENTS.md`**

- Remove `sqlharness gain --json` from the examples.
- Journal bullet: drop `gain` from the list of recorded operations; drop the sentences that describe the `gain` command (`gain` and Statistics aggregate…, `gain.jsonl`…, "With `journal.enabled: false`, `gain` reports zeros…", "a gain storage failure…"); keep that the dashboard's Statistics aggregate operations with paired raw/emitted footprints.
- Dashboard bullet: replace "its one write endpoint (`PUT /api/settings`…)" with "two write endpoints (`PUT /api/settings` and `POST /api/prices/refresh`), each requiring the `X-SqlHarness-Dashboard` header and a same-origin `Origin`; the price refresh is the only network access SQLHarness makes besides database connections and happens only on that click". Replace "behind a per-start token cookie" with "behind a persistent owner-only token (`~/.sqlharness/dashboard-token`; delete it to rotate) exchanged for a long-lived cookie".
- MCP section: "Exactly 11 tools" → "Exactly 10 tools", remove `sqlharness_gain` from the list and from "Capabilities, plan, and gain stay target-free" (→ "Capabilities and plan stay target-free").

- [ ] **Step 2: `docs/mcp.md` and `README.md`**

Remove `sqlharness_gain` from the tool table and the `gain` paragraphs ("`sqlharness_gain` aggregates CLI operations…", "An operation name this process does not bucket…"); README: remove the `sqlharness gain --json` example. Add to the dashboard section of the README: savings in USD at current models.dev prices, refreshable in Settings; the persistent token and bookmarkable URL.

- [ ] **Step 3: Run the gates, one after the other**

Run: `pwsh ./scripts/verify.ps1`, then `pwsh ./scripts/verify-linux.ps1`.
Expected: all stages green. If the Linux gate cannot sync from the worktree, stop and ask the user before any workaround.

- [ ] **Step 4: Real-client acceptance**

Run: `pwsh ./scripts/mcp-client-acceptance.ps1 -Sqlharness <worktree build> -OpencodeModel opencode/step-5-preview-free` and paste the table into the PR.

- [ ] **Step 5: Commit**

```bash
git add AGENTS.md docs/mcp.md README.md
git commit -m "docs: model prices, Settings autosave, persistent dashboard token, gain removal"
```
