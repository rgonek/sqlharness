import React from "react"
import { createRoot } from "react-dom/client"
import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import "../../../src/SqlHarness.Dashboard/ui/src/index.css"
import { PlanWorkbench } from "../../../src/SqlHarness.Dashboard/ui/src/components/plan/PlanWorkbench"
import { variant } from "../../../src/SqlHarness.Dashboard/ui/src/test/fixtures"

const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
createRoot(document.getElementById("app")!).render(
  <QueryClientProvider client={client}>
    <main className="mx-auto max-w-5xl space-y-4 p-6">
      <h1 className="font-heading text-xl font-semibold">Plan picker long-label and viewport fixture</h1>
      <PlanWorkbench operationId={10} variant={variant({
        variant: "measure", caseId: "case-a",
        caseLabel: "Case Alpha with a deliberately long retained display label for the plan picker on a narrow phone screen",
      })} engine="sqlserver" />
    </main>
  </QueryClientProvider>,
)
