import { screen, within } from "@testing-library/react"
import { expect, test } from "vitest"
import { operation, operationDetail } from "@/test/fixtures"
import { renderApp, stubFetch } from "@/test/render"

test("failed compares retain recorded diagnostics when no optimization pair is eligible", async () => {
  stubFetch({
    "/api/operations/10": operationDetail({ operation: operation({ id: 10, operation: "compare", status: "failed", errorKind: "sql_execution_failed", errorMessage: "Retained database error for failed compare" }) }),
    "/api/operations/10/plans": { operationId: 10, state: "plans not stored", occurrences: [] },
    "/api/operations/10/repro": { text: null, completeness: "Unavailable", label: "No stored script" },
    "/api/operations/10/optimization": () => new Response(JSON.stringify({ error: "Comparison unavailable" }), { status: 404 }),
  })
  renderApp("/operations/10")
  await screen.findByText(/No eligible successful comparison evidence is retained/)
  expect(await screen.findByText("Retained database error for failed compare")).toBeVisible()
  expect(await screen.findByRole("heading", { level: 1 })).toBeInTheDocument()
  const partial = screen.getByRole("region", { name: "Retained partial measurements" })
  expect(within(partial).getByText("Logical reads (median)")).toBeInTheDocument()
  const metrics = within(partial).getByRole("region", { name: "Key metrics" })
  expect(within(metrics).getByText("50")).toBeInTheDocument()
})
