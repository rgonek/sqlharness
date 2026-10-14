import { useEffect, useState } from "react"
import { useAgentKinds, useSessions } from "@/api/queries"
import { ErrorState } from "@/components/ErrorState"
import { SessionTable } from "@/components/SessionTable"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import { Input } from "@/components/ui/input"
import { Skeleton } from "@/components/ui/skeleton"
import { Tabs, TabsList, TabsTrigger } from "@/components/ui/tabs"
import { agentTabs } from "@/lib/agents"
import { useNow } from "@/lib/useNow"

function useDebounced<T>(value: T, ms: number): T {
  const [debounced, setDebounced] = useState(value)
  useEffect(() => {
    const timer = setTimeout(() => setDebounced(value), ms)
    return () => clearTimeout(timer)
  }, [value, ms])
  return debounced
}

export function SessionsPage() {
  const [agent, setAgent] = useState("all")
  const [view, setView] = useState<"active" | "archived">("active")
  const [search, setSearch] = useState("")
  const q = useDebounced(search.trim(), 300)
  const now = useNow(30_000)
  const agentKinds = useAgentKinds()
  const tabs = agentTabs((agentKinds.data ?? []).map(item => item.key))
  const selected = tabs.some(tab => tab.value === agent) ? agent : "all"
  const base = { ...(selected === "all" ? {} : { agent: selected }), archived: view === "archived", q: q || undefined }
  const pinned = useSessions({ ...base, starred: true })
  const sessions = useSessions({ ...base, starred: false })
  const failure = pinned.error ?? sessions.error
  const pinnedItems = pinned.data?.pages.flatMap(page => page.items) ?? []
  const items = sessions.data?.pages.flatMap(page => page.items) ?? []

  return (
    <div className="space-y-4">
      <h1>Sessions</h1>
      <div className="flex flex-wrap items-center gap-2">
        <Input type="search" aria-label="Search sessions" placeholder="Search title, note, directory, key"
          value={search} onChange={e => setSearch(e.target.value)} className="max-w-sm" />
        <Tabs value={view} onValueChange={v => setView(v as "active" | "archived")}>
          <TabsList>
            <TabsTrigger value="active">Active</TabsTrigger>
            <TabsTrigger value="archived">Archived</TabsTrigger>
          </TabsList>
        </Tabs>
      </div>
      <Tabs value={selected} onValueChange={value => setAgent(String(value))}>
        <TabsList>
          {tabs.map(item => (
            <TabsTrigger key={item.value} value={item.value}>
              {item.label}
            </TabsTrigger>
          ))}
        </TabsList>
      </Tabs>
      <Card>
        <CardHeader>
          <CardTitle>{view === "archived" ? "Archived sessions" : "Agent sessions"}</CardTitle>
        </CardHeader>
        <CardContent className="space-y-4">
          {failure ? (
            <ErrorState error={failure} />
          ) : pinned.isPending || sessions.isPending ? (
            <Skeleton className="h-32 w-full" />
          ) : pinnedItems.length === 0 && items.length === 0 ? (
            <p className="text-muted-foreground">{q ? "No sessions match the search" : "No sessions"}</p>
          ) : (
            <SessionTable pinned={pinnedItems} sessions={items} now={now} />
          )}
          {pinned.hasNextPage && (
            <Button variant="outline" onClick={() => void pinned.fetchNextPage()} disabled={pinned.isFetchingNextPage}>
              Load more pinned
            </Button>
          )}
          {sessions.hasNextPage && (
            <Button variant="outline" onClick={() => void sessions.fetchNextPage()} disabled={sessions.isFetchingNextPage}>
              Load more
            </Button>
          )}
        </CardContent>
      </Card>
    </div>
  )
}
