import type { SessionSummary } from "@/api/types"

/** Local path for a file:// URI; any other or malformed URI is shown as received. */
export function rootLabel(uri: string): string {
  if (!uri.startsWith("file://")) return uri
  try {
    const path = decodeURIComponent(new URL(uri).pathname)
    return /^\/[A-Za-z]:/.test(path) ? path.slice(1) : path
  } catch {
    return uri
  }
}

export function clientLabel(session: Pick<SessionSummary, "clientTitle" | "clientName" | "clientVersion">): string | null {
  const name = session.clientTitle ?? session.clientName
  return name ? `${name} ${session.clientVersion ?? ""}`.trim() : null
}
