export type ActivityFilters = {
  text: string; time: string; agent: string[]; cwd: string[]; target: string[];
  status: string[]; kind: string[]; starred: boolean; archived: boolean; named: boolean
}
export type ActivitySearch = Partial<ActivityFilters>
export const ACTIVITY_FILTER_KEY = "sqlharness.activity.filters"
export const ACTIVITY_VIEW_KEY = "sqlharness.activity.view"
export const defaultActivityFilters = (): ActivityFilters => ({
  text: "", time: "604800000", agent: [], cwd: [], target: [], status: [], kind: [],
  starred: false, archived: false, named: false,
})

/** Bound shareable state; never retain arbitrary query keys or unbounded facet collections. */
export function validateActivitySearch(input: Record<string, unknown>): ActivitySearch {
  const result: ActivitySearch = {}
  if (typeof input.text === "string") result.text = input.text.slice(0, 256)
  if (["all", "604800000", "86400000", "3600000"].includes(String(input.time))) result.time = String(input.time)
  for (const key of ["agent", "cwd", "target", "status", "kind"] as const) {
    if (!(key in input)) continue
    const value = input[key]
    const values = typeof value === "string" ? [value] : Array.isArray(value) ? value : []
    result[key] = [...new Set(values.filter((item): item is string => typeof item === "string" && item.length > 0 && item.length <= 512))].slice(0, 20)
  }
  if (result.status) result.status = result.status.filter(value => ["running", "failed", "rejected", "succeeded", "abandoned", "cancelled"].includes(value))
  for (const key of ["starred", "archived", "named"] as const) {
    if (input[key] === true || input[key] === "true") result[key] = true
    else if (input[key] === false || input[key] === "false") result[key] = false
  }
  return result
}

export function compactActivitySearch(filters: ActivityFilters): ActivitySearch {
  const result: ActivitySearch = { time: filters.time }
  if (filters.text) result.text = filters.text
  for (const key of ["agent", "cwd", "target", "status", "kind"] as const) if (filters[key].length) result[key] = filters[key]
  for (const key of ["starred", "archived", "named"] as const) if (filters[key]) result[key] = true
  return result
}

export function readActivityFilters(): ActivitySearch {
  try {
    const raw = sessionStorage.getItem(ACTIVITY_FILTER_KEY)
    if (!raw || raw.length > 65_536) return {}
    const parsed: unknown = JSON.parse(raw)
    return parsed && typeof parsed === "object" && !Array.isArray(parsed) ? validateActivitySearch(parsed as Record<string, unknown>) : {}
  } catch { return {} }
}

export function rememberActivityFilters(filters: ActivityFilters) {
  try { sessionStorage.setItem(ACTIVITY_FILTER_KEY, JSON.stringify(compactActivitySearch(filters))) } catch { /* Browsing remains usable when storage is unavailable. */ }
}

export type ActivityExpansionEntries = [number, boolean][]
export function readActivityExpansionEntries(): ActivityExpansionEntries {
  try {
    const raw = sessionStorage.getItem(ACTIVITY_VIEW_KEY)
    if (!raw || raw.length > 8192) return []
    const parsed: unknown = JSON.parse(raw)
    if (!parsed || typeof parsed !== "object") return []
    const entries = Array.isArray(parsed) ? parsed : Object.entries(parsed)
    return entries.filter((entry): entry is [number | string, boolean] => Array.isArray(entry) && entry.length === 2 && Number.isSafeInteger(Number(entry[0])) && Number(entry[0]) > 0 && typeof entry[1] === "boolean")
      .map(([key, value]): [number, boolean] => [Number(key), value]).slice(-200)
  } catch { return [] }
}

export function boundActivityExpansion(previous: ActivityExpansionEntries, next: Record<number, boolean>): ActivityExpansionEntries {
  const retained = new Map(previous.filter(([id]) => Object.hasOwn(next, id)))
  for (const [key, value] of Object.entries(next)) {
    const id = Number(key)
    if (!Number.isSafeInteger(id) || id <= 0 || typeof value !== "boolean") continue
    if (!retained.has(id) || retained.get(id) !== value) {
      retained.delete(id)
      retained.set(id, value)
    }
  }
  return [...retained].slice(-200)
}
export function readActivityExpansion(): Record<number, boolean> {
  return Object.fromEntries(readActivityExpansionEntries())
}
export function rememberActivityExpansion(expansion: Record<number, boolean>) {
  const entries = boundActivityExpansion(readActivityExpansionEntries(), expansion)
  try { sessionStorage.setItem(ACTIVITY_VIEW_KEY, JSON.stringify(entries)) } catch { /* Optional view memory. */ }
}
