import React from "react"
import { createRoot } from "react-dom/client"
import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import "./src/index.css"
import { SessionVersionDiff } from "./src/components/SessionVersionDiff"

const options = [
  { version: "v1", hash: "sha256:1111111111111111111111111111111111111111111111111111111111111111", label: "v1 · baseline" },
  { version: "v2", hash: "sha256:2222222222222222222222222222222222222222222222222222222222222222", label: "v2 · feature branch with a deliberately long retained label for narrow screens" },
  { version: "v3", hash: "sha256:3333333333333333333333333333333333333333333333333333333333333333", label: "v3 · release" },
]
const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
createRoot(document.getElementById("app")!).render(
  <QueryClientProvider client={client}>
    <main className="mx-auto max-w-5xl space-y-4 p-6">
      <h1 className="font-heading text-xl font-semibold">Select keyboard and viewport fixture</h1>
      <SessionVersionDiff sessionId={5} engine="postgres" options={options} />
    </main>
  </QueryClientProvider>,
)
