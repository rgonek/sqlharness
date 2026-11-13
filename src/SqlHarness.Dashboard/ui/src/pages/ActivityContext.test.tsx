import { act, screen, waitFor } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { beforeEach, expect, test } from "vitest"
import { renderApp, stubFetch } from "@/test/render"
import { operation, session } from "@/test/fixtures"

beforeEach(() => sessionStorage.clear())

function fixture() {
  const now = new Date().toISOString()
  stubFetch({
    "/api/operations": { items: [operation({ id: 11, sessionId: 1, agentLabel: "Baseline orders", status: "failed", startedAt: now })], nextCursor: null },
    "/api/sessions": { items: [session({ id: 1, title: "Orders investigation", lastSeen: now })], nextCursor: null },
  })
}

test("Activity restores search, status and expansion after a detail round trip", async () => {
  fixture()
  const user = userEvent.setup()
  const { router } = renderApp("/")
  const search = await screen.findByRole("textbox", { name: "Search activity" })
  await user.type(search, "Baseline")
  await user.click(screen.getByRole("button", { name: /^Failed/ }))
  await user.click(screen.getByRole("button", { name: "Collapse Orders investigation" }))
  await act(() => router.navigate({ to: "/nowhere" as never }))
  await screen.findByText("Not found")
  await user.click(screen.getByRole("link", { name: "Activity" }))
  expect(await screen.findByRole("textbox", { name: "Search activity" })).toHaveValue("Baseline")
  expect(screen.getByRole("button", { name: /^Failed/ })).toHaveAttribute("aria-pressed", "true")
  expect(screen.getByRole("button", { name: "Expand Orders investigation" })).toHaveAttribute("aria-expanded", "false")
})

test("Activity accepts shareable search and selected facets from the address", async () => {
  fixture()
  renderApp("/?text=Baseline&status=failed&archived=true&time=all")
  expect(await screen.findByRole("textbox", { name: "Search activity" })).toHaveValue("Baseline")
  expect(screen.getByRole("button", { name: /^Failed/ })).toHaveAttribute("aria-pressed", "true")
  expect(screen.getByRole("button", { name: "Include archived" })).toHaveAttribute("aria-pressed", "true")
  await userEvent.click(screen.getByRole("button", { name: "Reset filters" }))
  await waitFor(() => expect(screen.getByRole("textbox", { name: "Search activity" })).toHaveValue(""))
  expect(screen.getByRole("button", { name: /^Failed/ })).toHaveAttribute("aria-pressed", "false")
})

test("Activity restores the explicit previous address on browser back", async () => {
  fixture()
  const { router } = renderApp("/?text=Baseline")
  await screen.findByRole("textbox", { name: "Search activity" })
  await act(() => router.navigate({ to: "/", search: { text: "Orders" } as never }))
  await waitFor(() => expect(screen.getByRole("textbox", { name: "Search activity" })).toHaveValue("Orders"))
  act(() => router.history.back())
  await waitFor(() => expect(screen.getByRole("textbox", { name: "Search activity" })).toHaveValue("Baseline"))
})

test("Activity offers an explicit retry after a failed read", async () => {
  let failed = true
  let calls = 0
  stubFetch({
    "/api/operations": () => {
      calls++
      return failed
        ? new Response(JSON.stringify({ error: "Temporary read failure" }), { status: 503 })
        : new Response(JSON.stringify({ items: [], nextCursor: null }))
    },
    "/api/sessions": { items: [], nextCursor: null },
  })
  renderApp("/")
  await screen.findByText("Request failed")
  const before = calls
  failed = false
  await userEvent.click(screen.getByRole("button", { name: "Try again" }))
  expect(await screen.findByRole("region", { name: "Activity feed" })).toBeInTheDocument()
  expect(calls).toBeGreaterThan(before)
})

test("Activity records resolved default filters in its history entry", async () => {
  fixture()
  const { router } = renderApp("/")
  await screen.findByRole("textbox", { name: "Search activity" })
  await waitFor(() => expect(router.state.location.search).toMatchObject({ time: "604800000" }))
})

test("a remembered facet absent from the loaded page remains readable and removable", async () => {
  fixture()
  renderApp("/?agent=retired-agent")
  const agent = await screen.findByRole("button", { name: "Agent" })
  expect(agent).toHaveTextContent("retired-agent")
  await userEvent.click(agent)
  expect(await screen.findByRole("checkbox", { name: "retired-agent" })).toBeChecked()
})
