import React from "react"
import { createRoot } from "react-dom/client"
import { createMemoryHistory, createRootRoute, createRoute, createRouter, RouterProvider } from "@tanstack/react-router"
import "../../../src/SqlHarness.Dashboard/ui/src/index.css"
import { VersionTable } from "../../../src/SqlHarness.Dashboard/ui/src/components/optimization/VersionTable"
import type { OptimizationVersion } from "../../../src/SqlHarness.Dashboard/ui/src/api/types"
import type { SessionSearch } from "../../../src/SqlHarness.Dashboard/ui/src/router"

const versions: OptimizationVersion[] = ["v0", "v1", "v2"].map((version, index) => ({ version, hash: `${version}-hash`, logicalReadsSum: 100 - index, logicalReadsMean: 100 - index,
  scansSum: 1, elapsedMedianMs: 10 + index, caseOutcome: "Measured", measuredIn: [10], latestOperationId: 10, verdictBasis: "same conditions" }))
const rootRoute = createRootRoute()
const fixtureRoute = createRoute({ getParentRoute: () => rootRoute, path: "/fixture", component: () => <VersionTable subject="subject" sessionId={1} sessionSearch={{} as SessionSearch}
  versions={versions} selectedA="v0" selectedB="v2" onSelect={() => {}} /> })
const operationRoute = createRoute({ getParentRoute: () => rootRoute, path: "/operations/$id", component: () => null })
const router = createRouter({ routeTree: rootRoute.addChildren([fixtureRoute, operationRoute]), history: createMemoryHistory({ initialEntries: ["/fixture"] }) })
createRoot(document.getElementById("app")!).render(<RouterProvider router={router} />)
