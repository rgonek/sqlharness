import type { SessionSummary } from "@/api/types"

/** Operator title, else the session key. Spec C inserts the agent's conversation title between the two. */
export function sessionDisplayName(session: Pick<SessionSummary, "title" | "sessionKey">): string {
  return session.title ?? session.sessionKey
}
