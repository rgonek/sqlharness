import { useState } from "react"
import type { SessionSummary } from "@/api/types"
import { useUpdateSessionAnnotation } from "@/api/queries"
import { Alert, AlertDescription } from "@/components/ui/alert"
import { Badge } from "@/components/ui/badge"
import { Button } from "@/components/ui/button"
import { Toggle } from "@/components/ui/toggle"
import { Input } from "@/components/ui/input"
import { agentTitleSource, sessionDisplayName } from "@/lib/sessionName"
import { HugeiconsIcon } from "@hugeicons/react";
import { Archive02Icon, EditIcon, StarIcon, ArchiveRestoreIcon } from "@hugeicons/core-free-icons";

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
            <h1 title={sessionDisplayName(session)} className="min-w-0 flex-1 truncate cursor-text" onClick={() => setDraft(session.title ?? "")}>{sessionDisplayName(session)}</h1>
            {!session.title && session.agentTitle && <span className="text-sm font-normal text-muted-foreground">{agentTitleSource(session.agentKind)}</span>}
            <Button className="session-edit-title shrink-0" variant="ghost" size="icon" aria-label="Edit title" onClick={() => setDraft(session.title ?? "")}>
              <HugeiconsIcon icon={EditIcon} strokeWidth={2} />
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
            className="min-w-0 flex-1"
          />
        )}
        {draft !== null && <Button variant="ghost" size="icon" className="invisible shrink-0" tabIndex={-1} disabled aria-hidden="true" />}
        <Toggle className="session-star" aria-label={session.starred ? "Unstar session" : "Star session"}
          pressed={session.starred} disabled={update.isPending}
          onPressedChange={starred => update.mutate({ starred })}>
          <HugeiconsIcon icon={StarIcon} strokeWidth={2} className={session.starred ? "fill-amber-400 text-amber-500" : undefined} />
        </Toggle>
        <Button className="shrink-0" variant="ghost" size="icon" aria-label={session.archived ? "Unarchive" : "Archive"} disabled={update.isPending}
          onClick={() => update.mutate({ archived: !session.archived })}>
          {session.archived ? <HugeiconsIcon icon={ArchiveRestoreIcon} strokeWidth={2} /> : <HugeiconsIcon icon={Archive02Icon} strokeWidth={2} />}
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
