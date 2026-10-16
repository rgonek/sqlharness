import { useMutationHistory } from "@/api/queries"
import { ErrorState } from "@/components/ErrorState"
import { Button } from "@/components/ui/button"
import { Skeleton } from "@/components/ui/skeleton"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"

function ranWithoutProductionApproval(mode: string, verification: string | null, environment: string | null): boolean {
  return mode.toLowerCase() === "yolo" && verification === null && /^(prod|production|staging)$/i.test(environment ?? "")
}

export function MutationsPage() {
  const query = useMutationHistory()
  if (query.error && !query.data) return <ErrorState error={query.error} />
  if (query.isPending) return <Skeleton className="h-64 w-full" />
  const items = query.data.pages.flatMap(page => page.items)
  return (
    <div className="space-y-4">
      <h1>Mutation history</h1>
      <p className="text-sm text-muted-foreground">Recent mutation requests from the dashboard journal. SQL appears only when sensitive journal storage was enabled.</p>
      {items.length === 0 ? <p className="text-sm text-muted-foreground">No mutation requests recorded.</p> : (
        <div className="overflow-x-auto">
          <Table>
            <TableHeader><TableRow>
              <TableHead>Time</TableHead><TableHead>Profile</TableHead><TableHead>Database</TableHead><TableHead>Tier</TableHead>
              <TableHead>Mode</TableHead><TableHead>Status</TableHead><TableHead>Verification</TableHead><TableHead>Environment snapshot</TableHead><TableHead>SQL</TableHead>
            </TableRow></TableHeader>
            <TableBody>
              {items.map(item => (
                <TableRow key={item.id}>
                  <TableCell>{new Date(item.createdAt).toLocaleString()}</TableCell>
                  <TableCell>{item.profile}</TableCell>
                  <TableCell>{item.database}</TableCell>
                  <TableCell>{item.tier}</TableCell>
                  <TableCell>{item.mode}</TableCell>
                  <TableCell>
                    <div>{item.status ?? item.state}</div>
                    {ranWithoutProductionApproval(item.mode, item.verification, item.environment) &&
                      <span className="mt-1 inline-block rounded bg-destructive/10 px-2 py-1 text-xs font-semibold text-destructive">ran without approval on production</span>}
                  </TableCell>
                  <TableCell>{item.verification ?? "—"}</TableCell>
                  <TableCell>{item.environment ?? "—"}</TableCell>
                  <TableCell>{item.sql === null ? "Not stored" : <pre className="max-w-xl whitespace-pre-wrap break-all">{item.sql}</pre>}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}
      {query.hasNextPage && <Button variant="outline" disabled={query.isFetchingNextPage} onClick={() => void query.fetchNextPage()}>
        {query.isFetchingNextPage ? "Loading…" : "Load more"}
      </Button>}
      {query.isFetchNextPageError && <p role="alert" className="text-sm text-destructive">Could not load the next page of mutation history. Try again.</p>}
    </div>
  )
}
