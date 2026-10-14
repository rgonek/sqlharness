import { useState } from "react"
import { useUpdateSessionAnnotation } from "@/api/queries"
import { Alert, AlertDescription } from "@/components/ui/alert"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"

const maximumNoteLength = 10_000

export function SessionNoteCard({ sessionId, note }: { sessionId: number; note: string | null }) {
  const update = useUpdateSessionAnnotation(sessionId)
  const [draft, setDraft] = useState<string | null>(null)

  if (note === null && draft === null)
    return <Button variant="outline" onClick={() => { update.reset(); setDraft("") }}>Add note</Button>

  return (
    <Card>
      <CardHeader><CardTitle><h2>Note</h2></CardTitle></CardHeader>
      <CardContent className="space-y-2">
        {draft === null ? (
          <>
            <p className="whitespace-pre-wrap break-words">{note}</p>
            <div className="flex items-center gap-2">
              <Button variant="outline" size="sm" onClick={() => { update.reset(); setDraft(note ?? "") }}>Edit note</Button>
              {update.isSuccess && <span className="text-sm text-muted-foreground">Saved</span>}
            </div>
          </>
        ) : (
          <>
            <textarea
              aria-label="Session note" maxLength={maximumNoteLength} value={draft} rows={6}
              onChange={event => setDraft(event.target.value)}
              className="w-full min-w-0 rounded-lg border border-input bg-transparent p-2.5 text-base outline-none transition-colors placeholder:text-muted-foreground focus-visible:border-ring focus-visible:ring-3 focus-visible:ring-ring/50 md:text-sm dark:bg-input/30"
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
      </CardContent>
    </Card>
  )
}
