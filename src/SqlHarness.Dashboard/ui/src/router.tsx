import { createRootRoute, createRoute, createRouter, type RouterHistory } from "@tanstack/react-router"
import { AppLayout } from "@/components/AppLayout"
import { LivePage } from "@/pages/LivePage"
import { NotFoundPage } from "@/pages/NotFoundPage"
import { OperationPage } from "@/pages/OperationPage"
import { OperationsPage, type OperationListSearch } from "@/pages/OperationsPage"
import { ProfilesPage } from "@/pages/ProfilesPage"
import { SessionPage } from "@/pages/SessionPage"
import { SessionsPage } from "@/pages/SessionsPage"
import { SettingsPage } from "@/pages/SettingsPage"
import { StatsPage } from "@/pages/StatsPage"

const rootRoute = createRootRoute({ component: AppLayout, notFoundComponent: NotFoundPage })

const liveRoute = createRoute({ getParentRoute: () => rootRoute, path: "/", component: LivePage })
const sessionsRoute = createRoute({ getParentRoute: () => rootRoute, path: "/sessions", component: SessionsPage })
const sessionRoute = createRoute({ getParentRoute: () => rootRoute, path: "/sessions/$id", component: SessionPage })
const operationRoute = createRoute({ getParentRoute: () => rootRoute, path: "/operations/$id", component: OperationPage })
const operationsRoute = createRoute({ getParentRoute: () => rootRoute, path: "/operations", component: OperationsPage,
  validateSearch: (search: Record<string, unknown>): OperationListSearch => ({
    from: typeof search.from === "string" ? search.from : undefined,
    to: typeof search.to === "string" ? search.to : undefined,
    profile: typeof search.profile === "string" ? search.profile : undefined,
    unprofiled: search.unprofiled === true || search.unprofiled === "true",
    dimensions: typeof search.dimensions === "string" ? search.dimensions
      : search.dimensions !== null && typeof search.dimensions === "object" && !Array.isArray(search.dimensions)
        ? JSON.stringify(search.dimensions) : undefined,
  }) })
const statsRoute = createRoute({ getParentRoute: () => rootRoute, path: "/stats", component: StatsPage })
const profilesRoute = createRoute({ getParentRoute: () => rootRoute, path: "/profiles", component: ProfilesPage })
const settingsRoute = createRoute({ getParentRoute: () => rootRoute, path: "/settings", component: SettingsPage })

export const routeTree = rootRoute.addChildren([liveRoute, sessionsRoute, sessionRoute, operationRoute, operationsRoute, statsRoute, profilesRoute, settingsRoute])

/** Browser history in the app; tests pass a memory history. */
export function createAppRouter(history?: RouterHistory) {
  return createRouter({ routeTree, history, defaultPreload: false })
}

declare module "@tanstack/react-router" {
  interface Register {
    router: ReturnType<typeof createAppRouter>
  }
}
