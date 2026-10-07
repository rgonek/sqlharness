import { createRootRoute, createRoute, createRouter, type RouterHistory } from "@tanstack/react-router"
import { AppLayout } from "@/components/AppLayout"
import { LivePage } from "@/pages/LivePage"
import { NotFoundPage } from "@/pages/NotFoundPage"
import { OperationPage } from "@/pages/OperationPage"
import { SessionPage } from "@/pages/SessionPage"
import { SessionsPage } from "@/pages/SessionsPage"

const rootRoute = createRootRoute({ component: AppLayout, notFoundComponent: NotFoundPage })

const liveRoute = createRoute({ getParentRoute: () => rootRoute, path: "/", component: LivePage })
const sessionsRoute = createRoute({ getParentRoute: () => rootRoute, path: "/sessions", component: SessionsPage })
const sessionRoute = createRoute({ getParentRoute: () => rootRoute, path: "/sessions/$id", component: SessionPage })
const operationRoute = createRoute({ getParentRoute: () => rootRoute, path: "/operations/$id", component: OperationPage })
const statsRoute = createRoute({ getParentRoute: () => rootRoute, path: "/stats", component: NotFoundPage })

export const routeTree = rootRoute.addChildren([liveRoute, sessionsRoute, sessionRoute, operationRoute, statsRoute])

/** Browser history in the app; tests pass a memory history. */
export function createAppRouter(history?: RouterHistory) {
  return createRouter({ routeTree, history, defaultPreload: false })
}

declare module "@tanstack/react-router" {
  interface Register {
    router: ReturnType<typeof createAppRouter>
  }
}
