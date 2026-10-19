import { useEffect, useState } from "react"
import { useAgentKinds, useAgentSessions, useSessions, useSettings } from "@/api/queries"
import { ErrorState } from "@/components/ErrorState"
import { AgentSessionTable, SessionTable } from "@/components/SessionTable"
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
  const [hasOptimization, setHasOptimization] = useState(false)
  const q = useDebounced(search.trim(), 300)
  const now = useNow(30_000)
  const agentKinds = useAgentKinds()
  const settings = useSettings()
  const tabs = agentTabs((agentKinds.data ?? []).map(item => item.key))
  const selected = tabs.some(tab => tab.value === agent) ? agent : "all"
  const base = { ...(selected === "all" ? {} : { agent: selected }), archived: view === "archived", q: q || undefined, hasCompare: hasOptimization || undefined }
  const pinned = useSessions({ ...base, starred: true })
  const sessions = useSessions({ ...base, starred: false })
  const apiMatching = pinned.data?.pages[0]?.matching ?? sessions.data?.pages[0]?.matching ?? false
  const matching = settings.data ? settings.data.settings.dashboard.agentTranscripts.enabled && apiMatching : apiMatching
  const agentFilters = { archived: view === "archived", agent: selected === "all" ? undefined : selected, q: q || undefined, hasCompare: hasOptimization || undefined }
  const agentPinned = useAgentSessions({ ...agentFilters, starred: true }, matching)
  const agentSessions = useAgentSessions({ ...agentFilters, starred: false }, matching)
  const failure = pinned.error ?? sessions.error ?? (matching ? agentPinned.error ?? agentSessions.error : null)
  const pinnedItems = pinned.data?.pages.flatMap(page => page.items) ?? []
  const items = sessions.data?.pages.flatMap(page => page.items) ?? []
  const agentPinnedItems = agentPinned.data?.pages.flatMap(page => page.items) ?? []
  const agentItems = agentSessions.data?.pages.flatMap(page => page.items) ?? []
  const agentFilter = (item: typeof agentItems[number]) =>
    (selected === "all" || item.key.agentKind === selected) &&
    (!q || `${item.title ?? ""} ${item.key.conversationId} ${item.notePreview ?? ""} ${item.children.map(child => `${child.session.sessionKey} ${child.session.cwd ?? ""}`).join(" ")}`.toLocaleLowerCase().includes(q.toLocaleLowerCase())) &&
    (!hasOptimization || item.children.some(child => child.session.hasCompare))
  const visiblePinned = agentPinnedItems.filter(agentFilter)
  const pinnedKeys = new Set(visiblePinned.map(item => `${item.key.agentKind}:${item.key.conversationId}`))
  const visibleAgents = agentItems.filter(item => agentFilter(item) && !pinnedKeys.has(`${item.key.agentKind}:${item.key.conversationId}`))
  const linkedIds = new Set([...agentPinnedItems, ...agentItems].flatMap(item => item.children.map(child => child.session.id)))
  const unmatched = [...new Map([...pinnedItems, ...items].map(item => [item.id, item])).values()].filter(item => !linkedIds.has(item.id) && (selected === "all" || item.agentKind === selected))

  return (
    <div className="space-y-4">
      <h1>Sessions</h1>
      <div className="flex flex-wrap items-center gap-2">
        <Input type="search" aria-label="Search sessions" placeholder="Search title, note, directory, key"
          value={search} onChange={e => setSearch(e.target.value)} className="max-w-sm" />
        <label className="flex items-center gap-2 text-sm"><input type="checkbox" checked={hasOptimization} onChange={e => setHasOptimization(e.target.checked)} />Contains optimization</label>
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
          ) : pinned.isPending || sessions.isPending || (matching && (agentPinned.isPending || agentSessions.isPending)) ? (
            <Skeleton className="h-32 w-full" />
          ) : matching ? visiblePinned.length === 0 && visibleAgents.length === 0 && unmatched.length === 0 ? (
            <p className="text-muted-foreground">{q ? "No sessions match the search" : "No sessions"}</p>
          ) : (
            <AgentSessionTable pinned={visiblePinned} sessions={visibleAgents} unmatched={unmatched} now={now} />
          ) : pinnedItems.length === 0 && items.length === 0 ? (
            <p className="text-muted-foreground">{q ? "No sessions match the search" : "No sessions"}</p>
          ) : (
            <SessionTable pinned={pinnedItems} sessions={items} now={now} />
          )}
          {matching ? agentPinned.hasNextPage && (
            <Button variant="outline" onClick={() => void agentPinned.fetchNextPage()} disabled={agentPinned.isFetchingNextPage}>Load more pinned</Button>
          ) : pinned.hasNextPage && (
            <Button variant="outline" onClick={() => void pinned.fetchNextPage()} disabled={pinned.isFetchingNextPage}>
              Load more pinned
            </Button>
          )}
          {matching ? agentSessions.hasNextPage && (
            <Button variant="outline" onClick={() => void agentSessions.fetchNextPage()} disabled={agentSessions.isFetchingNextPage}>Load more</Button>
          ) : sessions.hasNextPage && (
            <Button variant="outline" onClick={() => void sessions.fetchNextPage()} disabled={sessions.isFetchingNextPage}>
              Load more
            </Button>
          )}
        </CardContent>
      </Card>
    </div>
  )
}
