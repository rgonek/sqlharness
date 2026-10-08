import hljs from "highlight.js/lib/core"
import sqlLanguage from "highlight.js/lib/languages/sql"
import { format } from "sql-formatter"

hljs.registerLanguage("sql", sqlLanguage)

/** Formats with the engine's dialect; SQL the formatter cannot parse is shown as written. */
export function formatSql(sql: string, engine: string | null | undefined): string {
  try {
    return format(sql, { language: engine === "postgres" ? "postgresql" : "transactsql", keywordCase: "preserve" })
  } catch {
    return sql
  }
}

/** Highlighted HTML; highlight.js escapes the input, so the result is safe to inject. */
export function highlightSql(sql: string): string {
  return hljs.highlight(sql, { language: "sql", ignoreIllegals: true }).value
}
