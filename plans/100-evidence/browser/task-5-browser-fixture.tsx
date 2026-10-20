import React from "react"
import { createRoot } from "react-dom/client"
import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import "./src/index.css"
import { ProfileDimensionsPanel } from "./src/pages/ProfileDimensionsPanel"

const values = [
  { name: "component", value: "api", isUnknown: false, source: "recorded", operations: 1, percentage: 100, totalDurationMs: 20, durationAvailableOperations: 1, durationUnavailableOperations: 0, failed: 0, rejected: 0 },
  { name: "project", value: "alpha", isUnknown: false, source: "recorded", operations: 1, percentage: 100, totalDurationMs: 20, durationAvailableOperations: 1, durationUnavailableOperations: 0, failed: 0, rejected: 0 },
  { name: "region", value: "", isUnknown: false, source: "recorded", operations: 1, percentage: 100, totalDurationMs: 20, durationAvailableOperations: 1, durationUnavailableOperations: 0, failed: 0, rejected: 0 },
  { name: "region", value: "", isUnknown: true, source: "recorded", operations: 1, percentage: 100, totalDurationMs: 20, durationAvailableOperations: 1, durationUnavailableOperations: 0, failed: 0, rejected: 0 },
]
const dimensions = ["component", "project", "region"].map(name => ({ name, values: values.filter(value => value.name === name) }))
const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
createRoot(document.getElementById("app")!).render(<QueryClientProvider client={client}>
  <main className="mx-auto max-w-5xl p-4">
    <h1 className="mb-4 text-xl font-semibold">Dimension controls browser fixture</h1>
    <ProfileDimensionsPanel range="7d" window={{ from: "2026-10-01T00:00:00.000Z", to: "2026-10-08T00:00:00.000Z" }}
      unprofiled={false} profile="app" profileName="app" databaseTemplate="{project}.{component}" profileDefinitionAvailable
      dimensions={dimensions} targets={[]} dimensionFilters={{}} onDimensionFiltersChange={() => {}} axisChoice={null} onAxisChoiceChange={() => {}} />
  </main>
</QueryClientProvider>)
