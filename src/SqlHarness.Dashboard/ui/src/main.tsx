import { QueryClientProvider } from "@tanstack/react-query"
import { RouterProvider } from "@tanstack/react-router"
import { StrictMode } from "react"
import { createRoot } from "react-dom/client"
import { createQueryClient } from "./api/queries"
import "./index.css"
import { applyTheme, readThemeMode } from "./lib/theme"
import { createAppRouter } from "./router"

const client = createQueryClient()
const router = createAppRouter()

function Root() {
  return (
    <QueryClientProvider client={client}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  )
}

// Apply the remembered theme before the first paint; the CSP forbids an inline script in index.html.
applyTheme(readThemeMode())

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <Root />
  </StrictMode>,
)
