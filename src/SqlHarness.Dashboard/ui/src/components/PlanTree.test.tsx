import { render, screen } from "@testing-library/react"
import { afterEach, expect, test, vi } from "vitest"
import { PlanTree } from "./PlanTree"

afterEach(() => vi.restoreAllMocks())

test("duplicate operator warnings render twice without key errors", () => {
  const error = vi.spyOn(console, "error").mockImplementation(() => {})
  render(<PlanTree plan={{ statements: [{ root: { physicalOp: "Hash Match", warnings: ["Spill", "Spill"] } }] }} />)
  expect(screen.getAllByText("Spill")).toHaveLength(2)
  expect(error).not.toHaveBeenCalled()
})
