import { useState } from "react"
import { useNamedOperations } from "@/api/queries"
import { OperationTable } from "@/components/OperationTable"
import { ErrorState } from "@/components/ErrorState"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Skeleton } from "@/components/ui/skeleton"

export function NamedOperationsPage() {
  const [search, setSearch] = useState("")
  const query = useNamedOperations(search.trim() || undefined)
  const operations = query.data?.pages.flatMap(page => page.items) ?? []
  if (query.error && !query.data) return <ErrorState error={query.error} />
  return <section className="space-y-4">
    <header><h1 className="font-heading text-xl font-semibold">Named operations</h1><p className="text-sm text-muted-foreground">Operations with an agent label, agent keep marker, or owner star.</p></header>
    <div className="grid max-w-lg gap-1 text-sm">
      <Label htmlFor="named-operations-search">Search named operations</Label>
      <Input id="named-operations-search" type="search" value={search} onChange={event => setSearch(event.currentTarget.value)} />
    </div>
    {query.isPending ? <Skeleton className="h-32 w-full" /> : operations.length ? <OperationTable operations={operations} showInternal /> : <p className="text-sm text-muted-foreground">No named operations found.</p>}
    {query.isFetchNextPageError && <div role="alert" className="flex items-center gap-3 rounded-md border border-destructive/40 p-3 text-sm">
      <span>Could not load more operations: {query.error instanceof Error ? query.error.message : String(query.error)}</span>
      <button className="rounded-md border px-3 py-1.5" onClick={() => void query.fetchNextPage()}>Retry loading more</button>
    </div>}
    {query.hasNextPage && <button className="rounded-md border px-3 py-2 text-sm" disabled={query.isFetchingNextPage} onClick={() => void query.fetchNextPage()}>
      {query.isFetchingNextPage ? "Loading…" : "Load more operations"}
    </button>}
  </section>
}
