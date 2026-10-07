import { QueryClientProvider, useQuery } from "@tanstack/react-query"
import { render, screen, waitFor } from "@testing-library/react"
import { expect, test } from "vitest"
import { getJson, NotFoundError, UnauthorizedError } from "@/api/client"
import { createQueryClient } from "@/api/queries"
import { stubFetch } from "@/test/render"
import { ErrorState } from "./ErrorState"

test("unauthorized explains how to reopen the dashboard", () => {
  render(<ErrorState error={new UnauthorizedError()} />)
  expect(screen.getByText(/sqlharness dashboard/)).toBeInTheDocument()
})

test("not found and other errors", () => {
  const { rerender } = render(<ErrorState error={new NotFoundError("/api/x")} />)
  expect(screen.getByText("Not found")).toBeInTheDocument()
  rerender(<ErrorState error={new Error("boom")} />)
  expect(screen.getByText("Request failed")).toBeInTheDocument()
})

function Probe() {
  const query = useQuery({ queryKey: ["probe"], queryFn: () => getJson("/api/sessions") })
  return query.error ? <ErrorState error={query.error} /> : <span>loading</span>
}

test("Unauthorized_shows_the_reopen_message_and_does_not_retry", async () => {
  const calls = stubFetch({ "/api/sessions": () => new Response("no", { status: 401 }) })
  render(
    <QueryClientProvider client={createQueryClient()}>
      <Probe />
    </QueryClientProvider>,
  )
  await waitFor(() => expect(screen.getByText(/sqlharness dashboard/)).toBeInTheDocument())
  expect(calls).toEqual(["/api/sessions"])
})
