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

export type OptimizationView = "changed" | "whole" | "statements"
export type SessionSearch = { subject?: string; db?: string; group?: string; scope?: string; a?: string; b?: string; case?: string; agg?: string; view?: OptimizationView; unassigned?: boolean }
export type OperationDetailSearch = { case?: string; agg?: string; view?: OptimizationView; planCase?: string; planVariant?: string; planRepetition?: number; planDocument?: number; planStatement?: number; returnSession?: number; returnSubject?: string; returnDb?: string; returnGroup?: string; returnScope?: string; returnA?: string; returnB?: string; returnCase?: string; returnAgg?: string; returnView?: OptimizationView; returnUnassigned?: boolean }

const rootRoute = createRootRoute({ component: AppLayout, notFoundComponent: NotFoundPage })

const liveRoute = createRoute({ getParentRoute: () => rootRoute, path: "/", component: LivePage })
const sessionsRoute = createRoute({ getParentRoute: () => rootRoute, path: "/sessions", component: SessionsPage })
const sessionRoute = createRoute({ getParentRoute: () => rootRoute, path: "/sessions/$id", component: SessionPage,
  validateSearch: (search: Record<string, unknown>): SessionSearch => ({
    subject: typeof search.subject === "string" && search.subject.length <= 64 ? search.subject : undefined,
    db: typeof search.db === "string" && search.db.length <= 128 ? search.db : undefined,
    group: typeof search.group === "string" && search.group.length <= 256 ? search.group : undefined,
    scope: typeof search.scope === "string" && search.scope.length <= 64 ? search.scope : undefined,
    a: typeof search.a === "string" && search.a.length <= 64 ? search.a : undefined,
    b: typeof search.b === "string" && search.b.length <= 64 ? search.b : undefined,
    case: typeof search.case === "string" && search.case.length <= 256 ? search.case
      : typeof search.case === "number" && Number.isSafeInteger(search.case) ? String(search.case) : undefined,
    agg: search.agg === "mean" ? "mean" : search.agg === "sum" ? "sum" : undefined,
    view: search.view === "whole" || search.view === "statements" ? search.view : search.view === "changed" ? "changed" : undefined,
    unassigned: search.unassigned === true || search.unassigned === "true",
  }) })
const operationRoute = createRoute({ getParentRoute: () => rootRoute, path: "/operations/$id", component: OperationPage,
  validateSearch: (search: Record<string, unknown>): OperationDetailSearch => ({
    case: typeof search.case === "string" && search.case.length <= 256 ? search.case
      : typeof search.case === "number" && Number.isSafeInteger(search.case) ? String(search.case) : undefined,
    agg: search.agg === "mean" ? "mean" : search.agg === "sum" ? "sum" : undefined,
    view: search.view === "whole" || search.view === "statements" ? search.view : search.view === "changed" ? "changed" : undefined,
    planCase: typeof search.planCase === "string" && search.planCase.length <= 256 ? search.planCase : undefined,
    planVariant: typeof search.planVariant === "string" && search.planVariant.length <= 64 ? search.planVariant : undefined,
    planRepetition: typeof search.planRepetition === "number" && Number.isSafeInteger(search.planRepetition) && search.planRepetition >= 0 ? search.planRepetition : undefined,
    planDocument: typeof search.planDocument === "number" && Number.isSafeInteger(search.planDocument) && search.planDocument >= 0 ? search.planDocument : undefined,
    planStatement: typeof search.planStatement === "number" && Number.isSafeInteger(search.planStatement) && search.planStatement >= 0 ? search.planStatement : undefined,
    returnSession: typeof search.returnSession === "number" && Number.isSafeInteger(search.returnSession) && search.returnSession > 0 ? search.returnSession
      : typeof search.returnSession === "string" && /^\d+$/.test(search.returnSession) && Number.isSafeInteger(Number(search.returnSession)) ? Number(search.returnSession) : undefined,
    returnSubject: typeof search.returnSubject === "string" && search.returnSubject.length <= 64 ? search.returnSubject : undefined,
    returnDb: typeof search.returnDb === "string" && search.returnDb.length <= 128 ? search.returnDb : undefined,
    returnGroup: typeof search.returnGroup === "string" && search.returnGroup.length <= 256 ? search.returnGroup : undefined,
    returnScope: typeof search.returnScope === "string" && search.returnScope.length <= 64 ? search.returnScope : undefined,
    returnA: typeof search.returnA === "string" && search.returnA.length <= 64 ? search.returnA : undefined,
    returnB: typeof search.returnB === "string" && search.returnB.length <= 64 ? search.returnB : undefined,
    returnCase: typeof search.returnCase === "string" && search.returnCase.length <= 256 ? search.returnCase : undefined,
    returnAgg: search.returnAgg === "mean" ? "mean" : search.returnAgg === "sum" ? "sum" : undefined,
    returnView: search.returnView === "whole" || search.returnView === "statements" ? search.returnView : search.returnView === "changed" ? "changed" : undefined,
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
