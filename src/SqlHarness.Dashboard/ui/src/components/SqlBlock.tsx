import { useMemo, useState } from "react"
import { Button } from "@/components/ui/button"
import { formatSql, highlightSql } from "@/lib/sql"

export function SqlBlock({ sql, engine, label }: { sql: string; engine: string | null | undefined; label?: string }) {
  const [formatted, setFormatted] = useState(true)
  const text = useMemo(() => (formatted ? formatSql(sql, engine) : sql), [formatted, sql, engine])
  const html = useMemo(() => highlightSql(text), [text])
  return (
    <div className="space-y-1">
      <div className="flex items-center gap-2">
        {label && <span className="text-sm text-muted-foreground">{label}</span>}
        <Button variant="ghost" size="sm" onClick={() => setFormatted(value => !value)}>
          {formatted ? "Show original" : "Show formatted"}
        </Button>
        <Button variant="ghost" size="sm" onClick={() => void navigator.clipboard?.writeText(text)}>
          Copy
        </Button>
      </div>
      <pre className="sql-block overflow-x-auto whitespace-pre-wrap font-mono text-sm">
        {/* highlightSql escapes its input; see lib/sql.ts. */}
        <code data-testid="sql-code" dangerouslySetInnerHTML={{ __html: html }} />
      </pre>
    </div>
  )
}
