import { useEffect, useRef, useState } from "react"
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
import { BUILT_IN_SOUNDS, getSoundPreferences, MAX_CUSTOM_SOUND_BYTES, previewApprovalSound, saveSoundPreferences, validateCustomSound, type SoundPreferences } from "@/mutations/sound"

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

type CardKey = "journal" | "dashboard" | "compare" | "diagnostics" | "mutations" | "connection"
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

function BrowserSoundSettings() {
  const [preferences, setPreferences] = useState<SoundPreferences | null>(null)
  const [error, setError] = useState<string>()
  const [notice, setNotice] = useState<string>()
  const [permission, setPermission] = useState(() => typeof Notification === "undefined" ? "unsupported" : Notification.permission)

  useEffect(() => {
    let active = true
    void getSoundPreferences().then(value => { if (active) setPreferences(value) })
    return () => { active = false }
  }, [])

  function store(next: SoundPreferences, preview = false) {
    setPreferences(next)
    setError(undefined)
    setNotice(undefined)
    const persistence = saveSoundPreferences(next)
    if (preview) void previewApprovalSound()
    void persistence.then(
      () => setNotice("Sound preference saved for reload."),
      reason => setError(`Sound is active for this page but could not be saved for reload: ${reason instanceof Error ? reason.message : "IndexedDB write failed"}.`),
    )
  }

  async function chooseFile(file?: File) {
    if (!file) return
    const message = await validateCustomSound(file)
    if (message) { setError(message); setNotice(undefined); return }
    store({ ...(preferences ?? { selection: "warm-chime", muted: false, customAudio: null }), selection: "custom", customAudio: file })
  }

  async function requestNotifications() {
    setError(undefined)
    if (typeof Notification === "undefined") { setPermission("unsupported"); return }
    try { setPermission(await Notification.requestPermission()) }
    catch (reason) { setError(`Browser notification permission could not be requested: ${reason instanceof Error ? reason.message : "request failed"}.`) }
  }

  return (
    <Card>
      <CardHeader><CardTitle>Browser notifications and approval sound</CardTitle>
        <CardDescription>Sound choices and custom audio stay in this browser. Custom audio is stored locally in IndexedDB, up to 2 MiB.</CardDescription>
      </CardHeader>
      <CardContent className="grid gap-4">
        {preferences ? <>
          <div className="grid gap-1">
            <Label htmlFor="approval-sound">Approval sound</Label>
            <select id="approval-sound" className="h-9 w-fit rounded-lg border border-input bg-background px-2 text-sm" value={preferences.selection}
              onChange={event => store({ ...preferences, selection: event.target.value as SoundPreferences["selection"] })}>
              {BUILT_IN_SOUNDS.map(sound => <option key={sound.id} value={sound.id}>{sound.label}</option>)}
              {preferences.customAudio && <option value="custom">Custom audio</option>}
            </select>
          </div>
          <div className="flex flex-wrap gap-2">
            {BUILT_IN_SOUNDS.map(sound => <Button key={sound.id} type="button" variant="outline" disabled={preferences.muted}
              aria-label={`Preview ${sound.label}`} onClick={() => store({ ...preferences, selection: sound.id }, true)}>Preview {sound.label}</Button>)}
            {preferences.selection === "custom" && preferences.customAudio && <Button type="button" variant="outline" disabled={preferences.muted}
              aria-label="Preview custom audio" onClick={() => { void previewApprovalSound() }}>Preview custom audio</Button>}
          </div>
          <Toggle id="approval-sound-muted" label="Mute approval sound" checked={preferences.muted} onChange={muted => store({ ...preferences, muted })} />
          <div className="grid gap-1">
            <Label htmlFor="custom-audio">Custom audio file</Label>
            <Input id="custom-audio" type="file" accept=".mp3,.wav,.ogg,audio/mpeg,audio/mp3,audio/wav,audio/x-wav,audio/ogg,application/ogg"
              onChange={event => { void chooseFile(event.target.files?.[0]); event.target.value = "" }} />
            <p className="text-sm text-muted-foreground">Choose an MP3, WAV, or OGG file no larger than {MAX_CUSTOM_SOUND_BYTES / (1024 * 1024)} MiB.</p>
          </div>
          {error && <p role="alert" className="text-sm text-destructive">{error}</p>}
          {notice && <p role="status" className="text-sm text-muted-foreground">{notice}</p>}
        </> : <p className="text-sm text-muted-foreground">Loading browser-local sound settings…</p>}
        <div className="grid gap-1">
          <Button type="button" variant="outline" className="w-fit" onClick={() => { void requestNotifications() }}>
            {permission === "granted" ? "Browser notifications enabled" : "Enable browser notifications"}
          </Button>
          <p className="text-sm text-muted-foreground">Permission: {permission}. The browser asks only after you press the button.</p>
        </div>
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
            <DialogDescription>Removes matched titles, models, conversations and frozen version labels. Session and agent-session annotations are kept.</DialogDescription>
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
  const setDiagnostics = (next: Partial<Settings["diagnostics"]>) => commit({ ...draft, diagnostics: { ...draft.diagnostics, ...next } }, "diagnostics")
  const setMutations = (next: Partial<Settings["mutations"]>) => commit({ ...draft, mutations: { ...draft.mutations, ...next } }, "mutations")
  const setConnection = (next: Partial<Settings["connection"]>) => commit({ ...draft, connection: { ...draft.connection, ...next } }, "connection")

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
        <CardHeader><CardTitle>Database connection</CardTitle><CardDescription>Maximum time to establish a SQL Server or PostgreSQL connection, including driver-managed authentication. Azure CLI token acquisition has its own existing time budget.</CardDescription></CardHeader>
        <CardContent className="grid gap-4">
          <NumberField id="connection-timeout" label="Connection timeout (seconds)" value={draft.connection.timeoutSeconds} min={1} max={300} error={fieldErrors["connection.timeoutSeconds"]} onCommit={value => setConnection({ timeoutSeconds: value })} />
          <StatusLine status={status.connection} />
        </CardContent>
      </Card>
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
          <Toggle id="dashboard-safety-notifications" label="Safety notifications" checked={draft.dashboard.safetyNotifications}
            onChange={value => setDashboard({ safetyNotifications: value })} />
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
      <Card>
        <CardHeader>
          <CardTitle>Connection diagnostics</CardTitle>
          <CardDescription>Record safe metadata about database connection attempts. Credentials and target names are excluded.</CardDescription>
        </CardHeader>
        <CardContent className="grid gap-4">
          <Toggle id="diagnostics-enabled" label="Connection diagnostics" checked={draft.diagnostics.enabled} onChange={value => setDiagnostics({ enabled: value })} />
          <StatusLine status={status.diagnostics} />
        </CardContent>
      </Card>
      <Card>
        <CardHeader><CardTitle>Mutations</CardTitle><CardDescription>Choose the default approval behavior and the time an approval request remains open.</CardDescription></CardHeader>
        <CardContent className="grid gap-4">
          <div className="grid gap-1">
            <Label htmlFor="mutation-default-mode">Default mutation mode</Label>
            <select id="mutation-default-mode" aria-label="Default mutation mode" className="h-9 w-fit rounded-lg border border-input bg-background px-2 text-sm"
              value={draft.mutations.defaultMode} onChange={event => setMutations({ defaultMode: event.target.value as Settings["mutations"]["defaultMode"] })}>
              <option value="never">Never</option><option value="operator">Operator approval</option><option value="yolo">YOLO</option>
            </select>
          </div>
          <div className="grid gap-1">
            <span className="text-sm">Verification</span>
            <code>{draft.mutations.verification}</code>
            {draft.mutations.verification !== "click" && <p role="alert" className="text-sm text-destructive">Verification value “{draft.mutations.verification}” is unsupported. This plan supports click verification only; the value was retained without changing it.</p>}
            <p className="text-sm text-muted-foreground">Approval decisions use a deliberate click. WebAuthn is not available in this plan.</p>
          </div>
          <NumberField id="mutation-approval-timeout" label="Approval timeout (seconds)" value={draft.mutations.approvalTimeoutSeconds} min={1} max={3600}
            error={fieldErrors["mutations.approvalTimeoutSeconds"]} onCommit={value => setMutations({ approvalTimeoutSeconds: value })} />
          <StatusLine status={status.mutations} />
        </CardContent>
      </Card>
      <BrowserSoundSettings />
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
