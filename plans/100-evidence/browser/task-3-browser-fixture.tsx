import React from "react"
import { createRoot } from "react-dom/client"
import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import "./src/index.css"
import { CompareDetail } from "./src/components/CompareDetail"
import { operation, operationDetail, variant } from "./src/test/fixtures"

const detail = operationDetail({
  operation: operation({ id: 10, operation: "compare", engine: "SqlServer" }),
  sqlText: "SELECT id, total FROM Orders WHERE tenant = @tenant",
  candidateSqlText: "SELECT id, total FROM Orders WHERE tenant = @tenant AND total > 0",
  variants: [
    variant({ ordinal: 0, variant: "baseline", caseId: "0" }),
    variant({ ordinal: 1, variant: "candidate", caseId: "0" }),
  ],
})
const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
createRoot(document.getElementById("app")!).render(
  <QueryClientProvider client={client}>
    <main className="mx-auto max-w-5xl space-y-4 p-6">
      <h1 className="font-heading text-xl font-semibold">Compare detail browser fixture</h1>
      <CompareDetail detail={detail} />
    </main>
  </QueryClientProvider>,
)
