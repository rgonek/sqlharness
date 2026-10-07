const dash = "—"
const numberFormat = new Intl.NumberFormat("en-US")

export function formatDuration(ms: number | null | undefined): string {
  if (ms === null || ms === undefined) return dash
  if (ms < 1000) return `${Math.round(ms)} ms`
  if (ms < 60_000) return `${(Math.floor(ms / 100) / 10).toFixed(1)} s`
  const minutes = Math.floor(ms / 60_000)
  const seconds = Math.round((ms % 60_000) / 1000)
  return `${minutes} min ${seconds} s`
}

export function formatNumber(value: number | null | undefined): string {
  return value === null || value === undefined ? dash : numberFormat.format(value)
}

export function formatKb(kb: number | null | undefined): string {
  if (kb === null || kb === undefined) return dash
  if (kb < 1024) return `${kb} KB`
  if (kb < 1024 * 1024) return `${(kb / 1024).toFixed(1)} MB`
  return `${(kb / (1024 * 1024)).toFixed(1)} GB`
}

const pad = (value: number) => String(value).padStart(2, "0")

export function formatTimestamp(iso: string | null | undefined): string {
  if (!iso) return dash
  const date = new Date(iso)
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`
}

export function formatAge(iso: string, now: number): string {
  const seconds = Math.max(0, Math.round((now - Date.parse(iso)) / 1000))
  if (seconds < 60) return `${seconds} s ago`
  if (seconds < 3600) return `${Math.floor(seconds / 60)} min ago`
  if (seconds < 86_400) return `${Math.floor(seconds / 3600)} h ago`
  return `${Math.floor(seconds / 86_400)} d ago`
}

export function shortHash(hash: string | null | undefined): string {
  return hash ? hash.replace(/^sha256:/, "").slice(0, 12) : dash
}

export function formatPercent(ratio: number | null | undefined): string {
  return ratio === null || ratio === undefined || Number.isNaN(ratio) ? dash : `${Math.round(ratio * 100)}%`
}
