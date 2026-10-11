import { act, render, screen } from "@testing-library/react"
import { afterEach, expect, test } from "vitest"
import { connection } from "@/api/connection"
import { ConnectionBanner } from "@/components/ConnectionBanner"

afterEach(() => connection.reportSuccess())

test("shows a reconnecting banner on failure and hides it on success", () => {
  render(<ConnectionBanner />)
  expect(screen.queryByRole("status")).not.toBeInTheDocument()
  act(() => connection.reportFailure())
  expect(screen.getByRole("status")).toHaveTextContent("Dashboard unavailable — reconnecting")
  expect(screen.getByRole("status")).toHaveTextContent("sqlharness dashboard")
  act(() => connection.reportSuccess())
  expect(screen.queryByRole("status")).not.toBeInTheDocument()
})
