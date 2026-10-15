import type { AgentConversation } from "@/api/types"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import { agentLabel } from "@/lib/agents"

export function SessionConversations({ conversations }: { conversations: AgentConversation[] }) {
  return (
    <Card>
      <CardHeader><CardTitle>Conversations</CardTitle></CardHeader>
      <CardContent className="grid gap-3">
        {conversations.map(conversation => (
          <div key={`${conversation.agentKind}:${conversation.conversationId}`} className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
            <div>
              <div>{conversation.title ?? "Untitled"}</div>
              <div className="font-mono text-sm text-muted-foreground">{conversation.conversationId}</div>
            </div>
            <div className="text-sm text-muted-foreground">{agentLabel(conversation.agentKind)} · {conversation.operations} operations</div>
          </div>
        ))}
      </CardContent>
    </Card>
  )
}
