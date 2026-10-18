import { format } from "sql-formatter"

export const MAX_DIFF_BYTES = 256 * 1024
export const MAX_DIFF_LINES = 4000
export const PREVIEW_CHARS = 8 * 1024

export type SqlDialect = "postgres" | "tsql"
export type DiffMode = "side-by-side" | "unified"
export type SqlTab = "baseline" | "candidate" | "diff"

export interface SqlViewerProps {
  sql: string
  dialect: SqlDialect
}

export type SqlSourceSpan = { start: number; length: number }
export type SqlSourceSelection = { start: number; end: number } | null
export interface PlanSqlViewerProps {
  sql: string
  dialect: SqlDialect
  highlightedSpans: SqlSourceSpan[]
  revealSpan: SqlSourceSpan | null
  onSelectionChange: (selection: SqlSourceSelection) => void
}

export interface SqlMergeViewProps {
  baseline: string
  candidate: string
  dialect: SqlDialect
  mode: DiffMode
}

export function boundedPreview(value: string): string {
  return value.length <= PREVIEW_CHARS ? value : `${value.slice(0, PREVIEW_CHARS)}\n… Preview limited to 8 KiB.`
}

export function utf8BytesWithin(value: string, limit: number): number | null {
  let bytes = 0
  for (let index = 0; index < value.length; index++) {
    const unit = value.charCodeAt(index)
    if (unit <= 0x7f) bytes++
    else if (unit <= 0x7ff) bytes += 2
    else if (unit >= 0xd800 && unit <= 0xdbff && index + 1 < value.length && value.charCodeAt(index + 1) >= 0xdc00 && value.charCodeAt(index + 1) <= 0xdfff) {
      bytes += 4
      index++
    } else bytes += 3
    if (bytes > limit) return null
  }
  return bytes
}

export function lineLimitExceeded(first: string, second: string): boolean {
  let lines = 2
  for (const value of [first, second]) {
    for (let index = 0; index < value.length; index++) {
      if (value.charCodeAt(index) === 10 && ++lines > MAX_DIFF_LINES) return true
    }
  }
  return false
}

export function diffTooLarge(baseline: string, candidate: string): boolean {
  const baselineBytes = utf8BytesWithin(baseline, MAX_DIFF_BYTES)
  return baselineBytes === null ||
    utf8BytesWithin(candidate, MAX_DIFF_BYTES - baselineBytes) === null ||
    lineLimitExceeded(baseline, candidate)
}

export function dialectOf(engine: string | null | undefined): SqlDialect {
  return engine === "postgres" ? "postgres" : "tsql"
}

export function normalizeForDiff(
  sql: string,
  engine: string | null | undefined,
  ignoreFormatting: boolean,
): { text: string; formatFailed: boolean } {
  const normalized = sql.replace(/\r\n?/g, "\n")
  if (!ignoreFormatting) return { text: normalized, formatFailed: false }
  try {
    return {
      text: format(normalized, {
        language: dialectOf(engine) === "postgres" ? "postgresql" : "transactsql",
        keywordCase: "preserve",
      }).replace(/\r\n?/g, "\n"),
      formatFailed: false,
    }
  } catch {
    return { text: normalized, formatFailed: true }
  }
}

export function readPreference<T extends string>(key: string, values: readonly T[], fallback: T): T {
  try {
    const value = localStorage.getItem(key)
    return value !== null && (values as readonly string[]).includes(value) ? value as T : fallback
  } catch {
    return fallback
  }
}

export function writePreference(key: string, value: string): void {
  try {
    localStorage.setItem(key, value)
  } catch {
    // The preference is optional when storage is blocked.
  }
}
