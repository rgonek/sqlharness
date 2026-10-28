import { useState } from "react"
import { useUpdateSessionAnnotation } from "@/api/queries"
import { Alert, AlertDescription } from "@/components/ui/alert"
import { Button } from "@/components/ui/button"
import { Textarea } from "@/components/ui/textarea"

const maximumNoteLength = 10_000

export function SessionNoteCard({ sessionId, note }: { sessionId: number; note: string | null }) {
  const update = useUpdateSessionAnnotation(sessionId)
  const [draft, setDraft] = useState<string | null>(null)

  if (note === null && draft === null)
    return <Button variant="outline" onClick={() => { update.reset(); setDraft("") }}>Add note</Button>

  return (
    <section aria-label="Session note" className="flex flex-col gap-2">
        {draft === null ? (
          <>
            <Button variant="secondary" className="session-note-display" title="Edit note" onClick={() => { update.reset(); setDraft(note ?? "") }}>{note}</Button>
            {update.isSuccess && <span role="status" className="sr-only">Saved</span>}
          </>
        ) : (
          <>
            <Textarea
              aria-label="Session note" placeholder="Note for this session" maxLength={maximumNoteLength} value={draft} rows={3}
              onChange={event => setDraft(event.target.value)}
            />
            <div className="flex items-center gap-2">
              <Button size="sm" aria-label="Save note" disabled={update.isPending}
                onClick={() => update.mutate({ note: draft.trim() ? draft : null }, { onSuccess: () => setDraft(null) })}>
                Save
              </Button>
              <Button variant="ghost" size="sm" onClick={() => setDraft(null)}>Cancel</Button>
              <span className="ml-auto text-sm text-muted-foreground">{draft.length} / {maximumNoteLength}</span>
            </div>
          </>
        )}
        {update.error && (
          <Alert variant="destructive"><AlertDescription>{update.error.message}</AlertDescription></Alert>
        )}
    </section>
  )
}
