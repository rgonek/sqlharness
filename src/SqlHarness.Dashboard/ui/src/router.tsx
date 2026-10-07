import { createRootRoute, createRoute, createRouter, type RouterHistory } from "@tanstack/react-router"
import { AppLayout } from "@/components/AppLayout"
import { NotFoundPage } from "@/pages/NotFoundPage"

const rootRoute = createRootRoute({ component: AppLayout, notFoundComponent: NotFoundPage })

const liveRoute = createRoute({ getParentRoute: () => rootRoute, path: "/", component: NotFoundPage })
const sessionsRoute = createRoute({ getParentRoute: () => rootRoute, path: "/sessions", component: NotFoundPage })
const sessionRoute = createRoute({ getParentRoute: () => rootRoute, path: "/sessions/$id", component: NotFoundPage })
const operationRoute = createRoute({ getParentRoute: () => rootRoute, path: "/operations/$id", component: NotFoundPage })
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
