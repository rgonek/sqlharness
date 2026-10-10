import { useSearch } from "@tanstack/react-router"
import { useOperations } from "@/api/queries"
import { ErrorState } from "@/components/ErrorState"
import { OperationTable } from "@/components/OperationTable"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import { Skeleton } from "@/components/ui/skeleton"

export type OperationListSearch = {
  from?: string; to?: string; profile?: string; unprofiled?: boolean; dimensions?: string
}

export function OperationsPage() {
  const search = useSearch({ from: "/operations" }) as OperationListSearch
  let dimensions: Record<string, string | null> = {}
  try {
    const value: unknown = search.dimensions ? JSON.parse(search.dimensions) : {}
    if (value !== null && typeof value === "object" && !Array.isArray(value)) {
      dimensions = Object.fromEntries(Object.entries(value).filter((entry): entry is [string, string | null] =>
        typeof entry[0] === "string" && (typeof entry[1] === "string" || entry[1] === null)))
    }
  } catch { /* malformed links fall back to the bounded unfiltered list */ }

  const filters = { from: search.from, to: search.to, profile: search.profile, unprofiled: search.unprofiled, dimensions }
  const query = useOperations(filters)
  if (query.error) return <ErrorState error={query.error} />

  const operations = query.data?.pages.flatMap(page => page.items) ?? []
  return <div className="space-y-4">
    <h1>Operations</h1>
    <Card>
      <CardHeader><CardTitle>Filtered operation history</CardTitle></CardHeader>
      <CardContent className="space-y-3">
        <p className="text-sm text-muted-foreground">
          {search.profile ? `Profile: ${search.profile}` : search.unprofiled ? "Profile: No profile" : "All profiles"}
          {Object.entries(dimensions).map(([name, value]) => ` · ${name} = ${value ?? "Unknown (missing)"}`)}
          {search.from && ` · From ${search.from}`}{search.to && ` · Before ${search.to}`}
        </p>
        {query.isPending ? <Skeleton className="h-32 w-full" /> : operations.length === 0
          ? <p className="text-sm text-muted-foreground">No operations match this scope.</p>
          : <OperationTable operations={operations} />}
        {query.hasNextPage && <Button type="button" variant="outline" disabled={query.isFetchingNextPage}
          onClick={() => void query.fetchNextPage()}>
          {query.isFetchingNextPage ? "Loading…" : "Load more operations"}
        </Button>}
      </CardContent>
    </Card>
  </div>
}
