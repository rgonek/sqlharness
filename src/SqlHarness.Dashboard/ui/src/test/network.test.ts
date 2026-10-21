import { screen, waitFor } from "@testing-library/react"
import type { QueryClient } from "@tanstack/react-query"
import { expect, test } from "vitest"
import { assertNoUnexpectedRequests } from "./network"
import { renderApp, stubFetch } from "./render"

test("reports an unexpected request from a rendered API query", async () => {
  stubFetch({})
  renderApp("/sessions/981")

  expect(await screen.findByText("Dashboard unreachable: /api/sessions/981")).toBeInTheDocument()
  const testName = expect.getState().currentTestName
  expect(() => assertNoUnexpectedRequests()).toThrow(
    `Unexpected dashboard test request in "${testName}": GET /api/sessions/981`,
  )
})

test("rejects an unexpected request with its method and path", async () => {
  stubFetch({})

  const testName = expect.getState().currentTestName
  await expect(fetch("/api/unexpected?cursor=501", { method: "POST" }))
    .rejects.toThrow(`Unexpected dashboard test request in "${testName}": POST /api/unexpected?cursor=501`)
  expect(() => assertNoUnexpectedRequests()).toThrow(
    `Unexpected dashboard test request in "${testName}": POST /api/unexpected?cursor=501`,
  )
})

test("does not retain a previous test's request handlers", async () => {
  const testName = expect.getState().currentTestName
  await expect(fetch("/api/profiles"))
    .rejects.toThrow(`Unexpected dashboard test request in "${testName}": GET /api/profiles`)
  expect(() => assertNoUnexpectedRequests()).toThrow(
    `Unexpected dashboard test request in "${testName}": GET /api/profiles`,
  )
})

let previousQueryClient: QueryClient | undefined
let finishPendingRequest: ((response: Response) => void) | undefined

test("starts an App API query that remains pending until renderApp teardown", async () => {
  stubFetch({
    "/api/sessions/982": () => new Promise<Response>(resolve => { finishPendingRequest = resolve }),
  })
  previousQueryClient = renderApp("/sessions/982").client

  await waitFor(() => {
    expect(previousQueryClient?.getQueryCache().find({ queryKey: ["session", 982] })?.state.fetchStatus).toBe("fetching")
  })
})

test("teardown cancels the pending renderApp query before restoring fetch", async () => {
  const query = previousQueryClient?.getQueryCache().find({ queryKey: ["session", 982] })
  expect(query?.state.fetchStatus).toBe("idle")
  expect(finishPendingRequest).toBeDefined()

  finishPendingRequest?.(new Response("{}", { status: 200, headers: { "Content-Type": "application/json" } }))
  await Promise.resolve()
  expect(query?.state.fetchStatus).toBe("idle")
})
