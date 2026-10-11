import type { Settings } from "@/api/types"

/** True when the change can make retention delete journal data: turning it on, or tightening a limit while it is on. */
export function isDestructive(previous: Settings, next: Settings): boolean {
  const before = previous.journal.retention
  const after = next.journal.retention
  return (!before.enabled && after.enabled) || (after.enabled && (after.maxAgeDays < before.maxAgeDays || after.maxSizeMb < before.maxSizeMb))
}
