import { useState } from "react"
import { Archive, ArchiveRestore, Pencil, Star } from "lucide-react"
import type { SessionSummary } from "@/api/types"
import { useUpdateSessionAnnotation } from "@/api/queries"
import { Alert, AlertDescription } from "@/components/ui/alert"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { agentTitleSource, sessionDisplayName } from "@/lib/sessionName"

export function SessionAnnotationHeader({ session }: { session: SessionSummary }) {
  const update = useUpdateSessionAnnotation(session.id)
  const [draft, setDraft] = useState<string | null>(null)

  const save = () => {
    const title = draft?.trim() ?? ""
    setDraft(null)
    if ((title || null) !== session.title) update.mutate({ title: title || null })
  }

  return (
    <div className="space-y-2">
      <div className="session-title-row flex flex-wrap items-center gap-2">
        {draft === null ? (
          <>
            <h1 className="min-w-0 flex-1 cursor-text" onClick={() => setDraft(session.title ?? "")}>{sessionDisplayName(session)}</h1>
            {!session.title && session.agentTitle && <span className="text-sm font-normal text-muted-foreground">{agentTitleSource(session.agentKind)}</span>}
            <Button className="session-edit-title" variant="ghost" size="icon" aria-label="Edit title" onClick={() => setDraft(session.title ?? "")}>
              <Pencil />
            </Button>
          </>
        ) : (
          <Input
            autoFocus aria-label="Session title" maxLength={200} value={draft} placeholder={session.agentTitle ?? session.sessionKey}
            onChange={event => setDraft(event.target.value)}
            onKeyDown={event => {
              if (event.key === "Enter") save()
              if (event.key === "Escape") setDraft(null)
            }}
            onBlur={() => setDraft(null)}
            className="max-w-xl"
          />
        )}
        <Button variant="ghost" size="icon" aria-label={session.starred ? "Unstar session" : "Star session"}
          aria-pressed={session.starred} disabled={update.isPending}
          onClick={() => update.mutate({ starred: !session.starred })}>
          <Star className={session.starred ? "fill-amber-400 text-amber-500" : undefined} />
        </Button>
        <Button variant="ghost" size="icon" aria-label={session.archived ? "Unarchive" : "Archive"} disabled={update.isPending}
          onClick={() => update.mutate({ archived: !session.archived })}>
          {session.archived ? <ArchiveRestore /> : <Archive />}
        </Button>
        {session.archived && <Badge variant="outline">Archived</Badge>}
      </div>
      {update.error && (
        <Alert variant="destructive">
          <AlertDescription>{update.error.message}</AlertDescription>
        </Alert>
      )}
    </div>
  )
}
