import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { render, screen } from "@testing-library/react"
import { expect, test, vi } from "vitest"
import type { OptimizationActionReference } from "@/api/types"
import { operationDetail, variant } from "@/test/fixtures"
import { stubFetch } from "@/test/render"
import { trackQueryClient } from "@/test/network"
import { VersionPlan } from "./VersionPlan"

vi.mock("@/components/plan/PlanWorkbench", () => ({ PlanWorkbench: ({ variant }: { variant: { caseId: string } }) => <div>Selected plan case: {variant.caseId}</div> }))

test.each(["missing mapping", "missing variant"])("plan does not substitute a different case with %s", async reason => {
  stubFetch({ "/api/operations/10": operationDetail({ variants: [variant({ variant: "candidate", caseId: "other-case" })] }) })
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  trackQueryClient(client)
  const action: OptimizationActionReference = { operationId: 10, side: "candidate" as const, caseIds: ["other-case"], caseIdsByFingerprint: reason === "missing variant" ? { selected: "missing-case" } : {} }
  render(<QueryClientProvider client={client}><VersionPlan a="v0" b="v1" before={null} after={action} engine="SqlServer" selectedCase="selected" /></QueryClientProvider>)
  expect(await screen.findByText("No retained plan variant for this selection.")).toBeInTheDocument()
  expect(screen.queryByText("Selected plan case: other-case")).not.toBeInTheDocument()
})

test("plan opens the exact mapped case and side", async () => {
  stubFetch({ "/api/operations/10": operationDetail({ variants: [variant({ variant: "baseline", caseId: "right-case" }), variant({ variant: "candidate", caseId: "other-case" }), variant({ variant: "candidate", caseId: "right-case" })] }) })
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  trackQueryClient(client)
  const action: OptimizationActionReference = { operationId: 10, side: "candidate" as const, caseIds: ["other-case", "right-case"], caseIdsByFingerprint: { selected: "right-case" } }
  render(<QueryClientProvider client={client}><VersionPlan a="v0" b="v1" before={null} after={action} engine="SqlServer" selectedCase="selected" /></QueryClientProvider>)
  expect(await screen.findByText("Selected plan case: right-case")).toBeInTheDocument()
})
