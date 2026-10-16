import { createRootRoute, createRoute, createRouter, type RouterHistory } from "@tanstack/react-router"
import { AppLayout } from "@/components/AppLayout"
import { LivePage } from "@/pages/LivePage"
import { NotFoundPage } from "@/pages/NotFoundPage"
import { MutationsPage } from "@/pages/MutationsPage"
import { OperationPage } from "@/pages/OperationPage"
import { OperationsPage, type OperationListSearch } from "@/pages/OperationsPage"
import { ProfilesPage } from "@/pages/ProfilesPage"
import { SessionPage } from "@/pages/SessionPage"
import { SessionsPage } from "@/pages/SessionsPage"
import { SettingsPage } from "@/pages/SettingsPage"
import { StatsPage } from "@/pages/StatsPage"

export type SessionSearch = { subject?: string; scope?: string; unassigned?: boolean }
export type OperationDetailSearch = { returnSession?: number; returnSubject?: string; returnScope?: string; returnUnassigned?: boolean }

const rootRoute = createRootRoute({ component: AppLayout, notFoundComponent: NotFoundPage })

const liveRoute = createRoute({ getParentRoute: () => rootRoute, path: "/", component: LivePage })
const sessionsRoute = createRoute({ getParentRoute: () => rootRoute, path: "/sessions", component: SessionsPage })
const sessionRoute = createRoute({ getParentRoute: () => rootRoute, path: "/sessions/$id", component: SessionPage,
  validateSearch: (search: Record<string, unknown>): SessionSearch => ({
    subject: typeof search.subject === "string" && search.subject.length <= 64 ? search.subject : undefined,
    scope: typeof search.scope === "string" && search.scope.length <= 64 ? search.scope : undefined,
    unassigned: search.unassigned === true || search.unassigned === "true",
  }) })
const operationRoute = createRoute({ getParentRoute: () => rootRoute, path: "/operations/$id", component: OperationPage,
  validateSearch: (search: Record<string, unknown>): OperationDetailSearch => ({
    returnSession: typeof search.returnSession === "number" && Number.isSafeInteger(search.returnSession) && search.returnSession > 0 ? search.returnSession
      : typeof search.returnSession === "string" && /^\d+$/.test(search.returnSession) && Number.isSafeInteger(Number(search.returnSession)) ? Number(search.returnSession) : undefined,
    returnSubject: typeof search.returnSubject === "string" && search.returnSubject.length <= 64 ? search.returnSubject : undefined,
    returnScope: typeof search.returnScope === "string" && search.returnScope.length <= 64 ? search.returnScope : undefined,
    returnUnassigned: search.returnUnassigned === true || search.returnUnassigned === "true",
  }) })
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
const mutationsRoute = createRoute({ getParentRoute: () => rootRoute, path: "/mutations", component: MutationsPage })
const settingsRoute = createRoute({ getParentRoute: () => rootRoute, path: "/settings", component: SettingsPage })

export const routeTree = rootRoute.addChildren([liveRoute, sessionsRoute, sessionRoute, operationRoute, operationsRoute, statsRoute, profilesRoute, mutationsRoute, settingsRoute])

/** Browser history in the app; tests pass a memory history. */
export function createAppRouter(history?: RouterHistory) {
  return createRouter({ routeTree, history, defaultPreload: false })
}

declare module "@tanstack/react-router" {
  interface Register {
    router: ReturnType<typeof createAppRouter>
  }
}
