import { useMemo, useState } from "react"
import { Button } from "@/components/ui/button"
import { CopyButton } from "@/components/CopyButton"
import { formatSql, highlightSql } from "@/lib/sql"

export function SqlBlock({ sql, engine, label, initiallyFormatted = true }: { sql: string; engine: string | null | undefined; label?: string; initiallyFormatted?: boolean }) {
  const [formatted, setFormatted] = useState(initiallyFormatted)
  const text = useMemo(() => (formatted ? formatSql(sql, engine) : sql), [formatted, sql, engine])
  const html = useMemo(() => highlightSql(text), [text])
  return (
    <div className="flex min-w-0 flex-col gap-1">
      <div className="flex flex-wrap items-center gap-2">
        {label && <span className="text-sm text-muted-foreground">{label}</span>}
        <Button variant="ghost" size="sm" onClick={() => setFormatted(value => !value)}>
          {formatted ? "Show original" : "Show formatted"}
        </Button>
        <CopyButton label={`Copy ${label ?? "SQL"}`} scopeKey={sql} showLabel getText={() => sql} />
      </div>
      <pre className="sql-block max-h-[32rem] resize-y overflow-auto whitespace-pre-wrap font-mono text-sm">
        {/* highlightSql escapes its input; see lib/sql.ts. */}
        <code data-testid="sql-code" dangerouslySetInnerHTML={{ __html: html }} />
      </pre>
    </div>
  )
}
