import { useState } from "react"
import { Archive, ArchiveRestore, Pencil, Star } from "lucide-react"
import type { SessionSummary } from "@/api/types"
import { useUpdateSessionAnnotation } from "@/api/queries"
import { Alert, AlertDescription } from "@/components/ui/alert"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { sessionDisplayName } from "@/lib/sessionName"

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
      <div className="flex flex-wrap items-center gap-2">
        {draft === null ? (
          <>
            <h1 className="cursor-text" onClick={() => setDraft(session.title ?? "")}>{sessionDisplayName(session)}</h1>
            <Button variant="ghost" size="icon" aria-label="Edit title" onClick={() => setDraft(session.title ?? "")}>
              <Pencil />
            </Button>
          </>
        ) : (
          <Input
            autoFocus aria-label="Session title" maxLength={200} value={draft} placeholder={session.sessionKey}
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
          <Star className={session.starred ? "fill-current" : undefined} />
        </Button>
        <Button variant="outline" size="sm" disabled={update.isPending}
          onClick={() => update.mutate({ archived: !session.archived })}>
          {session.archived ? <ArchiveRestore /> : <Archive />}
          {session.archived ? "Unarchive" : "Archive"}
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
