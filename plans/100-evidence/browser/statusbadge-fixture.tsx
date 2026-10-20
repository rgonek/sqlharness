import React from "react"
import { createRoot } from "react-dom/client"
import "../../../src/SqlHarness.Dashboard/ui/src/index.css"
import { StatusBadge } from "../../../src/SqlHarness.Dashboard/ui/src/components/StatusBadge"
import { operation } from "../../../src/SqlHarness.Dashboard/ui/src/test/fixtures"

createRoot(document.getElementById("app")!).render(
  <main className="space-y-4 p-6">
    <StatusBadge operation={operation({ status: "failed", exitCode: 5, errorKind: "sql_execution_failed", errorMessage: "First failure details." })} />
    <StatusBadge operation={operation({ status: "rejected", exitCode: 2, errorKind: "safety_rejected", errorMessage: "Second rejection details." })} />
  </main>,
)
