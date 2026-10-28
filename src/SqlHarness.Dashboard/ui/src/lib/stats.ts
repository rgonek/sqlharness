import type { DayAgentCount } from "@/api/types"

export function pivotPerDay(rows: DayAgentCount[]): { rows: Record<string, string | number>[]; agents: string[] } {
  const agents = [...new Set(rows.map(row => row.agentKind))].sort()
  const days = [...new Set(rows.map(row => row.day))].sort()
  return {
    agents,
    rows: days.map(day => {
      const entry: Record<string, string | number> = { day }
      for (const agent of agents) entry[agent] = rows.find(row => row.day === day && row.agentKind === agent)?.count ?? 0
      return entry
    }),
  }
}

export function failureRate(statuses: { key: string; count: number }[], operations: number): number | null {
  return operations === 0 ? null : statuses.filter(row => row.key === "failed" || row.key === "rejected").reduce((sum, row) => sum + row.count, 0) / operations * 100
}
