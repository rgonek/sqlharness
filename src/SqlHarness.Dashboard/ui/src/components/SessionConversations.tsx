import type { AgentConversation } from "@/api/types"
import { agentLabel } from "@/lib/agents"

export function SessionConversations({ conversations }: { conversations: AgentConversation[] }) {
  return (
    <section aria-label="Conversations" className="flex min-w-0 flex-col gap-2">
      <h3 className="text-sm font-medium">Conversations</h3>
      <div className="grid gap-3">
        {conversations.map(conversation => (
          <div key={`${conversation.agentKind}:${conversation.conversationId}`} className="flex flex-wrap items-baseline justify-between gap-x-4 gap-y-1">
            <div className="min-w-0 break-words">{conversation.title ?? "Untitled"}</div>
            <div className="min-w-0 break-all font-mono text-xs text-muted-foreground">{conversation.conversationId}</div>
            <div className="ml-auto text-sm text-muted-foreground">{agentLabel(conversation.agentKind)} · {conversation.operations} operations</div>
          </div>
        ))}
      </div>
    </section>
  )
}
