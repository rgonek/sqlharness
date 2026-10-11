import { useConnectionState } from "@/api/connection"

export function ConnectionBanner() {
  if (useConnectionState() === "online") return null
  return (
    <div role="status" className="border-b bg-muted px-4 py-2 text-sm">
      Dashboard unavailable — reconnecting. If it stays unavailable, start it with <code>sqlharness dashboard</code>{" "}
      or open a new <code>mcp serve</code> session with dashboard autostart.
    </div>
  )
}
