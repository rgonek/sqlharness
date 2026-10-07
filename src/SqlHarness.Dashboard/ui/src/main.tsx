import { QueryClientProvider } from "@tanstack/react-query"
import { RouterProvider } from "@tanstack/react-router"
import { StrictMode } from "react"
import { createRoot } from "react-dom/client"
import { createQueryClient } from "./api/queries"
import "./index.css"
import { useSystemTheme } from "./lib/theme"
import { createAppRouter } from "./router"

const client = createQueryClient()
const router = createAppRouter()

function Root() {
  useSystemTheme()
  return (
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  )
}

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <Root />
  </StrictMode>,
)
