import { useState } from "react"
import { useAgentKinds, useSessions } from "@/api/queries"
import { ErrorState } from "@/components/ErrorState"
import { SessionTable } from "@/components/SessionTable"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import { Skeleton } from "@/components/ui/skeleton"
import { Tabs, TabsList, TabsTrigger } from "@/components/ui/tabs"
import { agentTabs } from "@/lib/agents"
import { useNow } from "@/lib/useNow"

export function SessionsPage() {
  const [agent, setAgent] = useState("all")
  const now = useNow(30_000)
  const agentKinds = useAgentKinds()
  const tabs = agentTabs((agentKinds.data ?? []).map(item => item.key))
  const selected = tabs.some(tab => tab.value === agent) ? agent : "all"
  const sessions = useSessions(selected === "all" ? {} : { agent: selected })

  return (
    <div className="space-y-4">
      <h1>Sessions</h1>
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
          <CardTitle>Agent sessions</CardTitle>
        </CardHeader>
        <CardContent className="space-y-4">
          {sessions.error ? (
            <ErrorState error={sessions.error} />
          ) : sessions.isPending ? (
            <Skeleton className="h-32 w-full" />
          ) : (
            <SessionTable sessions={sessions.data.pages.flatMap(page => page.items)} now={now} />
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
