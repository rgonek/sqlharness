import { useRef, useState } from "react"
import { FieldErrorsError } from "@/api/client"
import { useClearTranscripts, usePrices, useRefreshPrices, useSaveSettings, useSettings, useTranscriptStatus } from "@/api/queries"
import type { Settings } from "@/api/types"
import { ErrorState } from "@/components/ErrorState"
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"
import { Button } from "@/components/ui/button"
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Skeleton } from "@/components/ui/skeleton"
import { Switch } from "@/components/ui/switch"
import { createSaveQueue } from "@/lib/saveQueue"
import { isDestructive } from "@/lib/settingsRules"

function Toggle({ id, label, checked, onChange }: { id: string; label: string; checked: boolean; onChange: (value: boolean) => void }) {
  return (
    <div className="flex items-center gap-2">
      <Switch id={id} checked={checked} onCheckedChange={onChange} aria-label={label} />
      <Label htmlFor={id}>{label}</Label>
    </div>
  )
}

/** Typing stays local; a valid integer in range is committed on blur or Enter, anything else shows an error and is not sent. */
function NumberField({ id, label, value, min, max, error, onCommit }: { id: string; label: string; value: number; min: number; max: number; error?: string; onCommit: (value: number) => void }) {
  const [text, setText] = useState<string | null>(null)
  const [localError, setLocalError] = useState<string>()
  const commit = () => {
    if (text === null) return
    const parsed = Number(text)
    if (text.trim() === "" || !Number.isInteger(parsed) || parsed < min || parsed > max) {
      setLocalError(`Enter a whole number from ${min} to ${max}.`)
      return
    }
    setLocalError(undefined)
    setText(null)
    if (parsed !== value) onCommit(parsed)
  }
  const shownError = localError ?? error
  return (
    <div className="grid gap-1">
      <Label htmlFor={id}>{label}</Label>
      <Input
        id={id}
        type="number"
        value={text ?? String(value)}
        aria-invalid={shownError ? true : undefined}
        onChange={event => { setText(event.target.value); setLocalError(undefined) }}
        onBlur={commit}
        onKeyDown={event => { if (event.key === "Enter") commit() }}
        className="w-40"
      />
      {shownError && <p className="text-sm text-destructive">{shownError}</p>}
    </div>
  )
}

type CardKey = "journal" | "dashboard" | "compare"
type Status = { state: "saving" } | { state: "saved" } | { state: "error"; message: string }

function StatusLine({ status }: { status?: Status }) {
  if (!status) return null
  if (status.state === "error") return <p role="alert" className="text-sm text-destructive">{status.message}</p>
  return <p role="status" className="text-sm text-muted-foreground">{status.state === "saving" ? "Saving…" : "Saved"}</p>
}

function ModelPricesCard() {
  const prices = usePrices()
  const refresh = useRefreshPrices()
  return (
    <Card>
      <CardHeader>
        <CardTitle>Model prices</CardTitle>
          <CardDescription>Stored pricing reference data. Refreshing downloads the current table from models.dev.</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        {prices.data && (
          <p>Model prices: {prices.data.source}, {prices.data.fetchedAt.slice(0, 10)} ({prices.data.origin === "embedded" ? "built in" : "refreshed"})</p>
        )}
        <div>
          <Button variant="outline" onClick={() => refresh.mutate()} disabled={refresh.isPending}>Refresh model prices</Button>
        </div>
        {refresh.error && <p className="text-sm text-destructive">{refresh.error.message}</p>}
      </CardContent>
    </Card>
  )
}

function AgentTranscriptsStatus() {
  const status = useTranscriptStatus()
  const clear = useClearTranscripts()
  const [confirming, setConfirming] = useState(false)
  return (
    <div className="grid gap-2">
      {status.data && <p>Matched {status.data.matched} · Unmatched {status.data.unmatched} · Read errors {status.data.readErrors}</p>}
      {status.data?.lastCycleAt && <p className="text-sm text-muted-foreground">Last cycle: {status.data.lastCycleAt}</p>}
      {status.error && <p role="alert" className="text-sm text-destructive">{status.error.message}</p>}
      <div>
        <Button variant="outline" disabled={clear.isPending} onClick={() => setConfirming(true)}>Clear matched data</Button>
      </div>
      {clear.error && <p role="alert" className="text-sm text-destructive">{clear.error.message}</p>}
      <Dialog open={confirming} onOpenChange={setConfirming}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Clear matched data?</DialogTitle>
            <DialogDescription>Removes matched titles, models and conversations. Your session titles and notes are kept.</DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="outline" onClick={() => setConfirming(false)}>Cancel</Button>
            <Button disabled={clear.isPending} onClick={() => { setConfirming(false); clear.mutate() }}>Clear matched data</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  )
}

export function SettingsPage() {
  const query = useSettings()
  const save = useSaveSettings()
  // Edits are kept as a patch over the server copy, so the form follows the query without an effect.
  const [edited, setEdited] = useState<Settings | null>(null)
  const [confirmingSensitive, setConfirmingSensitive] = useState(false)
  const [confirmingDestructive, setConfirmingDestructive] = useState<{ next: Settings; card: CardKey } | null>(null)
  const [status, setStatus] = useState<Partial<Record<CardKey, Status>>>({})
  const [fieldErrors, setFieldErrors] = useState<Record<string, string>>({})
  const [replaceError, setReplaceError] = useState<Error | null>(null)
  const pending = useRef(0)
  // mutateAsync keeps a stable identity, so one queue per page instance is safe.
  const [queue] = useState(() => createSaveQueue((settings: Settings) => save.mutateAsync({ settings })))

  if (query.error) return <ErrorState error={query.error} />
  if (query.isPending) return <Skeleton className="h-64 w-full" />

  const draft = edited ?? query.data.settings
  const journal = draft.journal
  const setJournal = (next: Partial<Settings["journal"]>) => commit({ ...draft, journal: { ...journal, ...next } }, "journal")
  const setRetention = (next: Partial<Settings["journal"]["retention"]>) => setJournal({ retention: { ...journal.retention, ...next } })
  const setDashboard = (next: Partial<Settings["dashboard"]>) => commit({ ...draft, dashboard: { ...draft.dashboard, ...next } }, "dashboard")
  const setCompare = (next: Partial<Settings["compare"]>) => commit({ ...draft, compare: { ...draft.compare, ...next } }, "compare")

  function apply(next: Settings, card: CardKey) {
    setEdited(next)
    setFieldErrors({})
    setStatus(current => ({ ...current, [card]: { state: "saving" } }))
    pending.current++
    queue.enqueue(next).then(
      () => {
        pending.current--
        setStatus(current => ({ ...current, [card]: { state: "saved" } }))
        if (pending.current === 0) setEdited(null)
      },
      error => {
        pending.current--
        if (error instanceof FieldErrorsError) {
          setFieldErrors(Object.fromEntries(error.errors.map(e => [e.field, e.message])))
          setStatus(current => ({ ...current, [card]: { state: "error", message: "Not saved. The previous value is kept." } }))
        } else {
          setStatus(current => ({ ...current, [card]: { state: "error", message: `Not saved: ${error instanceof Error ? error.message : "request failed"}. Change the value again to retry.` } }))
        }
        // Show what the server has until the user commits again.
        if (pending.current === 0) setEdited(null)
      },
    )
  }

  function commit(next: Settings, card: CardKey) {
    if (isDestructive(draft, next)) setConfirmingDestructive({ next, card })
    else apply(next, card)
  }

  const replaceInvalid = () => {
    setReplaceError(null)
    save.mutate({ settings: draft, overwriteInvalid: true }, { onSuccess: () => setEdited(null), onError: error => setReplaceError(error) })
  }

  return (
    <div className="space-y-4">
      <h1>Settings</h1>
      <p className="text-sm text-muted-foreground">{query.data.path}</p>
      {query.data.status === "invalid" && (
        <Alert variant="destructive">
          <AlertTitle>config.json is invalid</AlertTitle>
          <AlertDescription>
            SQLHarness is using defaults. Saving replaces the file.
            <Button variant="outline" size="sm" onClick={replaceInvalid}>Replace with these settings</Button>
          </AlertDescription>
        </Alert>
      )}
      <p className="text-sm text-muted-foreground">Changes save as you make them. New processes use these settings; restart running <code>mcp serve</code> sessions to apply them.</p>
      <Card>
        <CardHeader>
          <CardTitle>Activity journal</CardTitle>
          <CardDescription>What SQLHarness records about each operation.</CardDescription>
        </CardHeader>
        <CardContent className="grid gap-4">
          <Toggle id="journal-enabled" label="Journal enabled" checked={journal.enabled} onChange={value => setJournal({ enabled: value })} />
          <Toggle
            id="journal-sensitive"
            label="Store sensitive data"
            checked={journal.storeSensitive}
            onChange={value => (value ? setConfirmingSensitive(true) : setJournal({ storeSensitive: false }))}
          />
          <Toggle id="retention-enabled" label="Retention enabled" checked={journal.retention.enabled} onChange={value => setRetention({ enabled: value })} />
          <NumberField id="retention-age" label="Maximum age (days)" value={journal.retention.maxAgeDays} min={1} max={3650} error={fieldErrors["journal.retention.maxAgeDays"]} onCommit={value => setRetention({ maxAgeDays: value })} />
          <NumberField id="retention-size" label="Maximum size (MB)" value={journal.retention.maxSizeMb} min={10} max={102400} error={fieldErrors["journal.retention.maxSizeMb"]} onCommit={value => setRetention({ maxSizeMb: value })} />
          <StatusLine status={status.journal} />
        </CardContent>
      </Card>
      <Card>
        <CardHeader>
          <CardTitle>Dashboard</CardTitle>
        </CardHeader>
        <CardContent className="grid gap-4">
          <Toggle id="dashboard-autostart" label="Start with mcp serve" checked={draft.dashboard.autoStart} onChange={value => setDashboard({ autoStart: value })} />
          <div className="grid gap-2">
            <Toggle id="dashboard-agent-transcripts" label="Read agent transcripts" checked={draft.dashboard.agentTranscripts.enabled}
              onChange={value => setDashboard({ agentTranscripts: { enabled: value } })} />
            <p className="text-sm text-muted-foreground">Reads local Claude Code and Codex transcripts to show conversation titles and Claude's model and effort. Only titles, ids, model and effort are stored.</p>
            <AgentTranscriptsStatus />
          </div>
          <NumberField id="dashboard-idle" label="Idle shutdown (hours)" value={draft.dashboard.idleShutdownHours} min={1} max={168} error={fieldErrors["dashboard.idleShutdownHours"]} onCommit={value => setDashboard({ idleShutdownHours: value })} />
          <div className="grid gap-1">
            <span className="text-sm">Port</span>
            <span>{draft.dashboard.port}</span>
            <span className="text-sm text-muted-foreground">Change the port in config.json; the running dashboard keeps its port.</span>
          </div>
          <StatusLine status={status.dashboard} />
        </CardContent>
      </Card>
      <Card>
        <CardHeader>
          <CardTitle>Compare context</CardTitle>
          <CardDescription>CLI applies changes on the next invocation. Restart running MCP servers and reconnect their clients to apply changes; saving here does not restart them.</CardDescription>
        </CardHeader>
        <CardContent className="grid gap-4">
          <Toggle id="compare-require-context" label="Require compare subject and scope" checked={draft.compare.requireContext} onChange={value => setCompare({ requireContext: value })} />
          <p className="text-sm text-muted-foreground">When enabled, CLI compare needs both <code>--subject</code> and <code>--scope</code>, and MCP compare needs both fields. This setting is on by default.</p>
          <StatusLine status={status.compare} />
        </CardContent>
      </Card>
      <ModelPricesCard />
      {replaceError && <ErrorState error={replaceError} />}

      <Dialog open={confirmingSensitive} onOpenChange={setConfirmingSensitive}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Store sensitive data?</DialogTitle>
            <DialogDescription>
              From now on the journal will store SQL text, full plans (which can include parameter values), and error messages for new operations. Existing rows are not changed.
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="outline" onClick={() => setConfirmingSensitive(false)}>Cancel</Button>
            <Button onClick={() => { setConfirmingSensitive(false); setJournal({ storeSensitive: true }) }}>Enable</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <Dialog open={confirmingDestructive !== null} onOpenChange={open => { if (!open) setConfirmingDestructive(null) }}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Apply retention change?</DialogTitle>
            <DialogDescription>
              This change can delete activity journal data the next time retention runs (dashboard start, hourly, or the next mcp serve). Continue?
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="outline" onClick={() => setConfirmingDestructive(null)}>Cancel</Button>
            <Button onClick={() => { const change = confirmingDestructive; setConfirmingDestructive(null); if (change) apply(change.next, change.card) }}>Continue</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  )
}
