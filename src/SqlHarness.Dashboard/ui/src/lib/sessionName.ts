import type { SessionSummary } from "@/api/types"

export function sessionDisplayName(session: Pick<SessionSummary, "title" | "agentTitle" | "sessionKey">): string {
  return session.title ?? session.agentTitle ?? session.sessionKey
}

export function agentTitleSource(kind: string): string {
  return kind === "claude" ? "from Claude" : kind === "codex" ? "from Codex" : "from agent"
}
