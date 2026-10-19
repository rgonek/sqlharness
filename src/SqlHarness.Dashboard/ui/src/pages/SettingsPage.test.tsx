import { screen, waitFor, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test } from "vitest"
import type { SettingsResponse } from "@/api/types"
import { renderApp, stubFetch } from "@/test/render"

const response = (over: Partial<SettingsResponse> = {}): SettingsResponse => ({
  status: "valid",
  path: "C:/Users/me/.sqlharness/config.json",
  settings: {
    journal: { enabled: true, storeSensitive: false, retention: { enabled: false, maxAgeDays: 30, maxSizeMb: 500 } },
    dashboard: { autoStart: false, port: 47800, idleShutdownHours: 8, safetyNotifications: false, agentTranscripts: { enabled: false } },
    compare: { requireContext: true },
    diagnostics: { enabled: false },
    mutations: { defaultMode: "never", verification: "click", approvalTimeoutSeconds: 120 },
    connection: { timeoutSeconds: 30 },
  },
  ...over,
})

function stubSettings(initial: SettingsResponse) {
  const puts: { body: unknown; search: string }[] = []
  stubFetch({
    "/api/settings": (url: URL, init?: RequestInit) => {
      if (init?.method === "PUT") {
        const body = JSON.parse(String(init.body))
        puts.push({ body, search: url.search })
        return new Response(JSON.stringify({ ...initial, status: "valid", settings: body }), { status: 200 })
      }
      return new Response(JSON.stringify(initial), { status: 200 })
    },
  })
  return puts
}

type Put = { journal: { storeSensitive: boolean; retention: { enabled: boolean; maxAgeDays: number } }; dashboard: { autoStart: boolean } }

test("compare context is on by default and disabling it preserves other settings", async () => {
  const puts = stubSettings(response())
  renderApp("/settings")

  const toggle = await screen.findByRole("switch", { name: "Require compare subject and scope" })
  expect(toggle).toBeChecked()
  const explanation = screen.getByText((_, node) => node?.tagName === "P" && Boolean(node.textContent?.startsWith("When enabled, CLI compare needs both")))
  expect(explanation).toHaveTextContent(/CLI compare needs both.*subject.*scope.*MCP compare needs both/i)
  expect(screen.getByText(/CLI applies changes on the next invocation.*Restart running MCP servers and reconnect/i)).toBeInTheDocument()
  await userEvent.click(toggle)
  await waitFor(() => expect(puts).toHaveLength(1))
  expect((puts[0].body as Put & { compare: { requireContext: boolean } }).compare.requireContext).toBe(false)
  expect((puts[0].body as Put).journal).toEqual(response().settings.journal)
  expect((puts[0].body as Put).dashboard).toEqual(response().settings.dashboard)
})

test("connection diagnostics saves without confirmation and preserves other settings", async () => {
  const puts = stubSettings(response())
  renderApp("/settings")

  const toggle = await screen.findByRole("switch", { name: "Connection diagnostics" })
  expect(toggle).not.toBeChecked()
  await userEvent.click(toggle)
  await waitFor(() => expect(puts).toHaveLength(1))
  expect(screen.queryByRole("dialog")).not.toBeInTheDocument()
  expect((puts[0].body as { diagnostics: { enabled: boolean } }).diagnostics.enabled).toBe(true)
  expect((puts[0].body as { journal: unknown }).journal).toEqual(response().settings.journal)
  expect((puts[0].body as { compare: unknown }).compare).toEqual(response().settings.compare)
})

test("connection timeout saves within range and preserves unrelated settings", async () => {
  const puts = stubSettings(response())
  renderApp("/settings")

  const field = await screen.findByRole("spinbutton", { name: "Connection timeout (seconds)" })
  expect(field).toHaveValue(30)
  await userEvent.clear(field)
  await userEvent.type(field, "75")
  await userEvent.tab()
  await waitFor(() => expect(puts).toHaveLength(1))
  expect((puts[0].body as { connection: { timeoutSeconds: number } }).connection.timeoutSeconds).toBe(75)
  expect((puts[0].body as { dashboard: unknown }).dashboard).toEqual(response().settings.dashboard)
  expect((puts[0].body as { compare: unknown }).compare).toEqual(response().settings.compare)
})

test("safety notifications are opt-in and save with dashboard settings", async () => {
  const puts = stubSettings(response())
  renderApp("/settings")

  const toggle = await screen.findByRole("switch", { name: "Safety notifications" })
  expect(toggle).not.toBeChecked()
  await userEvent.click(toggle)
  await waitFor(() => expect(puts).toHaveLength(1))
  expect((puts[0].body as Put & { dashboard: { safetyNotifications: boolean } }).dashboard.safetyNotifications).toBe(true)
})

test("enabling storeSensitive asks for confirmation, then saves", async () => {
  const puts = stubSettings(response())
  renderApp("/settings")

  await userEvent.click(await screen.findByRole("switch", { name: "Store sensitive data" }))
  const dialog = await screen.findByRole("dialog")
  expect(dialog).toHaveTextContent(/SQL text, full plans/)
  expect(puts).toHaveLength(0)

  await userEvent.click(within(dialog).getByRole("button", { name: "Enable" }))
  await waitFor(() => expect(puts).toHaveLength(1))
  expect((puts[0].body as Put).journal.storeSensitive).toBe(true)
  expect(await screen.findByText(/restart running/)).toBeInTheDocument()
})

test("disabling storeSensitive needs no confirmation", async () => {
  stubSettings(response({ settings: { ...response().settings, journal: { ...response().settings.journal, storeSensitive: true } } }))
  renderApp("/settings")

  await userEvent.click(await screen.findByRole("switch", { name: "Store sensitive data" }))
  expect(screen.queryByRole("dialog")).not.toBeInTheDocument()
})

test("Toggle_saves_immediately", async () => {
  const puts = stubSettings(response())
  renderApp("/settings")

  await userEvent.click(await screen.findByRole("switch", { name: "Start with mcp serve" }))
  await waitFor(() => expect(puts).toHaveLength(1))
  expect((puts[0].body as Put).dashboard.autoStart).toBe(true)
  expect((puts[0].body as { mutations: unknown }).mutations).toEqual(response().settings.mutations)
  expect(await screen.findByText("Saved")).toBeInTheDocument()
  expect(screen.queryByRole("button", { name: "Save" })).not.toBeInTheDocument()
})

test("Number_field_saves_only_on_commit", async () => {
  const puts = stubSettings(response())
  renderApp("/settings")

  const field = await screen.findByRole("spinbutton", { name: "Maximum age (days)" })
  await userEvent.clear(field)
  await userEvent.type(field, "300")
  expect(puts).toHaveLength(0)
  await userEvent.tab()
  await waitFor(() => expect(puts).toHaveLength(1))
  expect((puts[0].body as Put).journal.retention.maxAgeDays).toBe(300)
})

test("Invalid_number_is_not_sent", async () => {
  const puts = stubSettings(response())
  renderApp("/settings")

  const field = await screen.findByRole("spinbutton", { name: "Maximum age (days)" })
  await userEvent.clear(field)
  await userEvent.type(field, "0{Enter}")
  expect(puts).toHaveLength(0)
  expect(await screen.findByText(/Enter a whole number from 1 to 3650/)).toBeInTheDocument()
})

test("Enabling_retention_asks_for_confirmation", async () => {
  const puts = stubSettings(response())
  renderApp("/settings")

  const toggle = await screen.findByRole("switch", { name: "Retention enabled" })
  await userEvent.click(toggle)
  let dialog = await screen.findByRole("dialog")
  expect(dialog).toHaveTextContent(/can delete activity journal data/)
  await userEvent.click(within(dialog).getByRole("button", { name: "Cancel" }))
  expect(puts).toHaveLength(0)
  expect(toggle).not.toBeChecked()

  await userEvent.click(toggle)
  dialog = await screen.findByRole("dialog")
  await userEvent.click(within(dialog).getByRole("button", { name: "Continue" }))
  await waitFor(() => expect(puts).toHaveLength(1))
  expect((puts[0].body as Put).journal.retention.enabled).toBe(true)
})

test("Lowering_limits_with_retention_on_asks_for_confirmation", async () => {
  const on = response({ settings: { ...response().settings, journal: { ...response().settings.journal, retention: { enabled: true, maxAgeDays: 30, maxSizeMb: 500 } } } })
  const puts = stubSettings(on)
  renderApp("/settings")

  const field = await screen.findByRole("spinbutton", { name: "Maximum age (days)" })
  await userEvent.clear(field)
  await userEvent.type(field, "7")
  await userEvent.tab()
  let dialog = await screen.findByRole("dialog")
  await userEvent.click(within(dialog).getByRole("button", { name: "Cancel" }))
  expect(puts).toHaveLength(0)
  expect(field).toHaveValue(30)

  await userEvent.clear(field)
  await userEvent.type(field, "7")
  await userEvent.tab()
  dialog = await screen.findByRole("dialog")
  await userEvent.click(within(dialog).getByRole("button", { name: "Continue" }))
  await waitFor(() => expect(puts).toHaveLength(1))
  expect((puts[0].body as Put).journal.retention.maxAgeDays).toBe(7)
})

test("a failed save shows the error and keeps the server value", async () => {
  stubFetch({
    "/api/settings": (_url: URL, init?: RequestInit) =>
      init?.method === "PUT"
        ? new Response(JSON.stringify({ errors: [{ field: "dashboard.idleShutdownHours", message: "Must be between 1 and 168." }] }), { status: 400 })
        : new Response(JSON.stringify(response()), { status: 200 }),
  })
  renderApp("/settings")

  const field = await screen.findByRole("spinbutton", { name: "Idle shutdown (hours)" })
  await userEvent.clear(field)
  await userEvent.type(field, "12{Enter}")
  expect(await screen.findByText("Must be between 1 and 168.")).toBeInTheDocument()
  expect(field).toHaveValue(8)
})

test("a conflict on save refetches and shows the invalid-file alert", async () => {
  let invalid = false
  stubFetch({
    "/api/settings": (_url: URL, init?: RequestInit) => {
      if (init?.method === "PUT") {
        invalid = true
        return new Response(JSON.stringify({ error: "config.json is invalid" }), { status: 409 })
      }
      return new Response(JSON.stringify(response({ status: invalid ? "invalid" : "valid" })), { status: 200 })
    },
  })
  renderApp("/settings")

  await userEvent.click(await screen.findByRole("switch", { name: "Start with mcp serve" }))
  expect(await screen.findByRole("button", { name: "Replace with these settings" })).toBeInTheDocument()
})

test("port is read-only and an invalid file needs explicit replacement", async () => {
  const puts = stubSettings(response({ status: "invalid" }))
  renderApp("/settings")

  expect(await screen.findByText("47800")).toBeInTheDocument()
  expect(screen.queryByRole("spinbutton", { name: "Port" })).not.toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "Replace with these settings" }))
  expect(puts[0].search).toBe("?overwriteInvalid=true")
})

test("refreshing model prices updates the shown snapshot and reports failures", async () => {
  let fail = false
  stubFetch({
    "/api/settings": response(),
    "/api/prices": { source: "models.dev", fetchedAt: "2026-10-11T00:00:00Z", origin: "embedded" },
    "/api/prices/refresh": () =>
      fail
        ? new Response(JSON.stringify({ error: "Model prices could not be refreshed." }), { status: 502 })
        : new Response(JSON.stringify({ source: "models.dev", fetchedAt: "2026-10-20T08:00:00Z", origin: "refreshed" }), { status: 200 }),
  })
  renderApp("/settings")

  expect(await screen.findByText("Model prices: models.dev, 2026-10-11 (built in)")).toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "Refresh model prices" }))
  expect(await screen.findByText("Model prices: models.dev, 2026-10-20 (refreshed)")).toBeInTheDocument()

  fail = true
  await userEvent.click(screen.getByRole("button", { name: "Refresh model prices" }))
  expect(await screen.findByText("Model prices could not be refreshed.")).toBeInTheDocument()
})

test("enables transcript reading, shows status, and confirms clearing matched data", async () => {
  const puts: { body: unknown; search: string }[] = []
  const posts: { body: unknown; header: string | null }[] = []
  stubFetch({
    "/api/settings": (url: URL, init?: RequestInit) => {
      if (init?.method === "PUT") {
        const body = JSON.parse(String(init.body))
        puts.push({ body, search: url.search })
        return new Response(JSON.stringify({ ...response(), settings: body }), { status: 200 })
      }
      return new Response(JSON.stringify(response()), { status: 200 })
    },
    "/api/transcripts": { enabled: false, matched: 3, unmatched: 1, readErrors: 0, lastCycleAt: null },
    "/api/transcripts/clear": (_url: URL, init?: RequestInit) => {
      posts.push({ body: JSON.parse(String(init?.body)), header: new Headers(init?.headers).get("X-SqlHarness-Dashboard") })
      return new Response(JSON.stringify({ enabled: false, matched: 0, unmatched: 0, readErrors: 0, lastCycleAt: null }), { status: 200 })
    },
  })
  renderApp("/settings")
  await userEvent.click(await screen.findByRole("switch", { name: "Read agent transcripts" }))
  await waitFor(() => expect(puts).toHaveLength(1))
  expect((puts[0].body as { dashboard: { agentTranscripts: { enabled: boolean } } }).dashboard.agentTranscripts.enabled).toBe(true)
  expect(await screen.findByText("Matched 3 · Unmatched 1 · Read errors 0")).toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "Clear matched data" }))
  const dialog = await screen.findByRole("dialog")
  expect(dialog).toHaveTextContent("Removes matched titles, models, conversations and frozen version labels. Session and agent-session annotations are kept.")
  expect(posts).toHaveLength(0)
  await userEvent.click(within(dialog).getByRole("button", { name: "Clear matched data" }))
  await waitFor(() => expect(posts).toEqual([{ body: {}, header: "1" }]))
})
