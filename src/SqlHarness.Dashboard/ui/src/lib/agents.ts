const labels: Record<string, string> = {
  claude: "Claude Code",
  codex: "Codex",
  copilot: "Copilot",
  opencode: "opencode",
  grok: "Grok",
  unknown: "Unknown",
}

const knownOrder = ["claude", "codex", "copilot", "opencode", "grok"]

export function agentLabel(kind: string): string {
  return labels[kind] ?? kind
}

/** "All" plus the kinds present: known kinds first, then other client names alphabetically, then "unknown". */
export function agentTabs(kinds: string[]): { value: string; label: string }[] {
  const present = new Set(kinds)
  const rank = (kind: string) =>
    knownOrder.includes(kind) ? knownOrder.indexOf(kind) : kind === "unknown" ? 1001 : 1000
  const ordered = [...present].sort((a, b) => rank(a) - rank(b) || a.localeCompare(b))
  return [{ value: "all", label: "All" }, ...ordered.map(kind => ({ value: kind, label: agentLabel(kind) }))]
}
