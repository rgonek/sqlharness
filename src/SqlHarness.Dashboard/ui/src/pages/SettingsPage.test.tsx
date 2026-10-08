import { screen, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test } from "vitest"
import type { SettingsResponse } from "@/api/types"
import { renderApp, stubFetch } from "@/test/render"

const response = (over: Partial<SettingsResponse> = {}): SettingsResponse => ({
  status: "valid",
  path: "C:/Users/me/.sqlharness/config.json",
  settings: {
    journal: { enabled: true, storeSensitive: false, retention: { enabled: false, maxAgeDays: 30, maxSizeMb: 500 } },
    dashboard: { autoStart: false, port: 47800, idleShutdownHours: 8 },
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

test("enabling storeSensitive asks for confirmation before saving", async () => {
  const puts = stubSettings(response())
  renderApp("/settings")

  await userEvent.click(await screen.findByRole("switch", { name: "Store sensitive data" }))
  const dialog = await screen.findByRole("dialog")
  expect(dialog).toHaveTextContent(/SQL text, full plans/)
  expect(puts).toHaveLength(0)

  await userEvent.click(within(dialog).getByRole("button", { name: "Enable" }))
  await userEvent.click(screen.getByRole("button", { name: "Save" }))
  expect(puts).toHaveLength(1)
  expect((puts[0].body as { journal: { storeSensitive: boolean } }).journal.storeSensitive).toBe(true)
  expect(await screen.findByText(/restart running/)).toBeInTheDocument()
})

test("disabling storeSensitive needs no confirmation", async () => {
  stubSettings(response({ settings: { ...response().settings, journal: { ...response().settings.journal, storeSensitive: true } } }))
  renderApp("/settings")

  await userEvent.click(await screen.findByRole("switch", { name: "Store sensitive data" }))
  expect(screen.queryByRole("dialog")).not.toBeInTheDocument()
})

test("port is read-only and an invalid file needs explicit replacement", async () => {
  const puts = stubSettings(response({ status: "invalid" }))
  renderApp("/settings")

  expect(await screen.findByText("47800")).toBeInTheDocument()
  expect(screen.queryByRole("spinbutton", { name: "Port" })).not.toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "Replace with these settings" }))
  expect(puts[0].search).toBe("?overwriteInvalid=true")
})
