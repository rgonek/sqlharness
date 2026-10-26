import { SegmentedRadioGroup } from "@/components/SegmentedRadioGroup"
import { useEffect, useRef, useState, type ReactNode } from "react"
import { FieldErrorsError } from "@/api/client"
import { useClearTranscripts, usePrices, useRefreshPrices, useSaveSettings, useSettings, useTranscriptStatus } from "@/api/queries"
import type { Settings } from "@/api/types"
import { ErrorState } from "@/components/ErrorState"
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"
import { Button } from "@/components/ui/button"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog"
import { Input } from "@/components/ui/input"
import { Label } from "@/components/ui/label"
import { Skeleton } from "@/components/ui/skeleton"
import { Switch } from "@/components/ui/switch"
import { Select, SelectContent, SelectGroup, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { createSaveQueue } from "@/lib/saveQueue"
import { isDestructive } from "@/lib/settingsRules"
import { BUILT_IN_SOUNDS, getSoundPreferences, MAX_CUSTOM_SOUND_BYTES, previewApprovalSound, saveSoundPreferences, validateCustomSound, type SoundPreferences } from "@/mutations/sound"

function SettingRow({ title, description, children, restart = false, tag }: { tag?: string; title: string; description: ReactNode; children: ReactNode; restart?: boolean }) {
  return <div className="setting-row"><div className="setting-copy"><div className="flex flex-wrap items-center gap-2 font-medium">{title}{tag && <span className="setting-tag">{tag}</span>}{restart && <span className="setting-tag">Restart mcp serve</span>}</div><div className="setting-description">{description}</div></div><div className="setting-control">{children}</div></div>
}
function Toggle({ id, label, checked, onChange }: { id: string; label: string; checked: boolean; onChange: (value: boolean) => void }) {
  return <Switch id={id} checked={checked} onCheckedChange={onChange} aria-label={label} />
}

/** Typing stays local; a valid integer in range is committed on blur or Enter, anything else shows an error and is not sent. */
function NumberField({ id, label, value, min, max, error, onCommit, description, unit }: { description?: ReactNode; unit?: string; id: string; label: string; value: number; min: number; max: number; error?: string; onCommit: (value: number) => void }) {
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
    <SettingRow title={label.replace(/ \(.*\)$/, "")} description={description}><div className="setting-number"><Label htmlFor={id} className="sr-only">{label}</Label>
      <Input
        id={id}
        type="number" min={min} max={max} step={1}
        value={text ?? String(value)}
        aria-invalid={shownError ? true : undefined}
        onChange={event => { setText(event.target.value); setLocalError(undefined) }}
        onBlur={commit}
        onKeyDown={event => { if (event.key === "Enter") commit() }}
        className="w-[84px] text-right"
      />{unit && <span className="text-[13px] text-muted-foreground">{unit}</span>}</div>
      {shownError && <p className="text-sm text-destructive">{shownError}</p>}
    </SettingRow>
  )
}

type CardKey = "journal" | "dashboard" | "compare" | "diagnostics" | "mutations" | "connection" | "execution"
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
    <SettingRow title="Price table" description={prices.data ? `models.dev · fetched ${prices.data.fetchedAt.slice(0, 10)} · ${prices.data.origin === "embedded" ? "built in" : "refreshed"}` : "Stored pricing reference data from models.dev."}>
      <Button variant="outline" aria-label="Refresh model prices" onClick={() => refresh.mutate()} disabled={refresh.isPending}>Refresh</Button>
      {refresh.error && <p className="text-sm text-destructive">{refresh.error.message}</p>}
    </SettingRow>
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
    <div>
      <SettingRow title="Approval sound" tag="This browser only" description="Played when a mutation waits for you. MP3, WAV or OGG up to 2 MiB for custom audio.">
        {preferences ? <div className="flex flex-wrap items-center justify-end gap-2"><Select<SoundPreferences["selection"] | "upload"> items={[...BUILT_IN_SOUNDS.map(sound => ({ value: sound.id, label: sound.label })), ...(preferences.customAudio ? [{ value: "custom", label: "Custom audio" }] : []), { value: "upload", label: "Custom audio…" }]} value={preferences.selection} onValueChange={value => { if (value === "upload") document.getElementById("custom-audio")?.click(); else if (value !== null) store({ ...preferences, selection: value }) }}>
          <SelectTrigger id="approval-sound" aria-label="Approval sound" className="w-36"><SelectValue>{value => value === "custom" ? "Custom audio" : BUILT_IN_SOUNDS.find(sound => sound.id === value)?.label ?? ""}</SelectValue></SelectTrigger>
          <SelectContent><SelectGroup>{BUILT_IN_SOUNDS.map(sound => <SelectItem key={sound.id} value={sound.id}>{sound.label}</SelectItem>)}{preferences.customAudio && <SelectItem value="custom">Custom audio</SelectItem>}<SelectItem value="upload">Custom audio…</SelectItem></SelectGroup></SelectContent>
        </Select><Button type="button" variant="outline" disabled={preferences.muted} aria-label={`Preview ${BUILT_IN_SOUNDS.find(sound => sound.id === preferences.selection)?.label ?? "custom audio"}`} onClick={() => { void previewApprovalSound() }}>Preview</Button></div> : <span>Loading browser-local sound settings…</span>}
      </SettingRow>
      {preferences && <><div className="sr-only"><Label htmlFor="custom-audio">Custom audio file</Label><Input id="custom-audio" tabIndex={-1} type="file" accept=".mp3,.wav,.ogg,audio/mpeg,audio/mp3,audio/wav,audio/x-wav,audio/ogg,application/ogg" onChange={event => { void chooseFile(event.target.files?.[0]); event.target.value = "" }} /><p>Choose an MP3, WAV, or OGG file no larger than {MAX_CUSTOM_SOUND_BYTES / (1024 * 1024)} MiB.</p></div>
      <SettingRow title="Mute approval sound" tag="This browser only" description="Keep notifications, silence the sound."><Toggle id="approval-sound-muted" label="Mute approval sound" checked={preferences.muted} onChange={muted => store({ ...preferences, muted })} /></SettingRow></>}
      {error && <p role="alert" className="text-sm text-destructive">{error}</p>}{notice && <p role="status" className="text-sm text-muted-foreground">{notice}</p>}
      <SettingRow title="Browser notifications" tag="This browser only" description="Show a system notification when approval is needed. The browser asks only after you press Enable.">
        <Button type="button" variant="outline" aria-label={permission === "granted" ? "Browser notifications enabled" : "Enable browser notifications"} onClick={() => { void requestNotifications() }}>{permission === "granted" ? "Enabled" : "Enable"}</Button><span className="sr-only">Permission: {permission}. The browser asks only after you press the button.</span>
      </SettingRow>
    </div>
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
      {!!status.data?.coverage?.length && <Table aria-label="Agent session matching coverage">
        <TableHeader><TableRow><TableHead>Agent</TableHead><TableHead>Transport</TableHead><TableHead>Exact</TableHead><TableHead>Inferred</TableHead><TableHead>Unassigned</TableHead></TableRow></TableHeader>
        <TableBody>{status.data.coverage.map(row => <TableRow key={`${row.agentKind}:${row.transport}`}><TableHead scope="row" className="whitespace-normal">{row.agentKind}</TableHead><TableCell>{row.transport}</TableCell><TableCell>{row.exact}</TableCell><TableCell>{row.inferred}</TableCell><TableCell>{row.unassigned}</TableCell></TableRow>)}</TableBody>
      </Table>}
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
  const [activeSection, setActiveSection] = useState("database")
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
  const setExecution = (next: Partial<Settings["execution"]>) => commit({ ...draft, execution: { ...draft.execution, ...next } }, "execution")

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
<div className="grid gap-6 settings-layout lg:grid-cols-[200px_minmax(0,760px)] lg:gap-10 lg:justify-center"><nav aria-label="Settings sections" className="flex flex-wrap gap-1 text-sm lg:sticky lg:top-6 lg:flex-col lg:self-start"><span className="px-3 py-2 font-semibold">Settings</span><a href="#settings-database" aria-current={activeSection === "database" ? "location" : undefined} onClick={() => setActiveSection("database")} className="rounded-md px-3 py-2 text-muted-foreground hover:bg-muted hover:text-foreground">Database</a><a href="#settings-mutations" aria-current={activeSection === "mutations" ? "location" : undefined} onClick={() => setActiveSection("mutations")} className="rounded-md px-3 py-2 text-muted-foreground hover:bg-muted hover:text-foreground">Mutations</a><a href="#settings-journal" aria-current={activeSection === "journal" ? "location" : undefined} onClick={() => setActiveSection("journal")} className="rounded-md px-3 py-2 text-muted-foreground hover:bg-muted hover:text-foreground">Journal & privacy</a><a href="#settings-agents" aria-current={activeSection === "agents" ? "location" : undefined} onClick={() => setActiveSection("agents")} className="rounded-md px-3 py-2 text-muted-foreground hover:bg-muted hover:text-foreground">Agents</a><a href="#settings-dashboard" aria-current={activeSection === "dashboard" ? "location" : undefined} onClick={() => setActiveSection("dashboard")} className="rounded-md px-3 py-2 text-muted-foreground hover:bg-muted hover:text-foreground">Dashboard</a><a href="#settings-prices" aria-current={activeSection === "prices" ? "location" : undefined} onClick={() => setActiveSection("prices")} className="rounded-md px-3 py-2 text-muted-foreground hover:bg-muted hover:text-foreground">Model prices</a></nav><div className="settings-content space-y-4">      <h1>Settings</h1>
      <p className="settings-path text-xs text-muted-foreground">{query.data.path}</p>
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
<section id="settings-database" aria-label="Database" className="settings-card"><h2>Database</h2><p>How SQLHarness connects and how long statements may run.</p>
  <NumberField id="connection-timeout" label="Connection timeout (seconds)" unit="sec" description="Time to open a SQL Server or PostgreSQL connection, including driver-managed authentication. Azure CLI token acquisition has its own budget." value={draft.connection.timeoutSeconds} min={1} max={300} error={fieldErrors["connection.timeoutSeconds"]} onCommit={value => setConnection({ timeoutSeconds: value })} /><StatusLine status={status.connection} />
  <NumberField id="execution-default-timeout" label="Default execution timeout (seconds)" unit="sec" description="Used by queries, mutations, benchmarks and watches when no explicit timeout is passed." value={draft.execution.defaultTimeoutSeconds} min={1} max={300} error={fieldErrors["execution.defaultTimeoutSeconds"]} onCommit={value => setExecution({ defaultTimeoutSeconds: value })} /><StatusLine status={status.execution} />
  <SettingRow title="Connection diagnostics" description="Record safe metadata about database connection attempts. Credentials and target names are excluded."><Toggle id="diagnostics-enabled" label="Connection diagnostics" checked={draft.diagnostics.enabled} onChange={value => setDiagnostics({ enabled: value })} /></SettingRow><StatusLine status={status.diagnostics} />
</section>
<section id="settings-mutations" aria-label="Mutations" className="settings-card"><h2>Mutations</h2><p>Default approval behavior for write operations.</p>
  <SettingRow title="Default mode" description="Applies when a profile does not set its own mode."><div><SegmentedRadioGroup label="Default mutation mode" value={draft.mutations.defaultMode} options={[{ value: "never", label: "Never" }, { value: "operator", label: "Operator approval" }, { value: "yolo", label: "YOLO" }]} onValueChange={value => { if (value === "never" || value === "operator" || value === "yolo") setMutations({ defaultMode: value }) }} /><p className="setting-mode-hint max-w-72">{draft.mutations.defaultMode === "operator" ? "Each mutation waits for approval here." : draft.mutations.defaultMode === "never" ? "Write operations are rejected by default." : "Write operations do not wait for operator approval."}</p></div></SettingRow>
  <NumberField id="mutation-approval-timeout" label="Approval timeout (seconds)" unit="sec" description="How long an approval request stays open before it is rejected." value={draft.mutations.approvalTimeoutSeconds} min={1} max={3600} error={fieldErrors["mutations.approvalTimeoutSeconds"]} onCommit={value => setMutations({ approvalTimeoutSeconds: value })} />
  <SettingRow title="Verification" description="Approval decisions use a deliberate click. WebAuthn is not available in this plan."><code className="rounded bg-muted px-2 py-1">{draft.mutations.verification}</code></SettingRow>
  {draft.mutations.verification !== "click" && <p role="alert" className="text-sm text-destructive">Verification value “{draft.mutations.verification}” is unsupported. This plan supports click verification only; the value was retained without changing it.</p>}
  <StatusLine status={status.mutations} /><BrowserSoundSettings />
</section>
<section id="settings-journal" aria-label="Journal & privacy" className="settings-card"><h2>Journal & privacy</h2><p>What SQLHarness records about each operation, and for how long.</p>
  <SettingRow title="Activity journal" description="Record operations so they appear in Activity and sessions. Disabling the journal also disables agent annotations."><Toggle id="journal-enabled" label="Journal enabled" checked={journal.enabled} onChange={value => setJournal(value ? { enabled: true } : { enabled: false, operationAnnotations: false })} /></SettingRow>
  <SettingRow title="Store sensitive data" description="Store SQL text, full plans (may include parameter values) and error messages for new operations. Existing rows are not changed."><Toggle id="journal-sensitive" label="Store sensitive data" checked={journal.storeSensitive} onChange={value => value ? setConfirmingSensitive(true) : setJournal({ storeSensitive: false })} /></SettingRow>
  <SettingRow title="Agent annotations" restart description="Allows agents to label operations and mark them for keeping. Enabling agent annotations also enables the activity journal automatically. Restart the MCP server and reconnect the client after changing this setting. Keep markers do not prevent retention."><Toggle id="journal-operation-annotations" label="Agent operation annotations" checked={journal.operationAnnotations} onChange={value => setJournal(value ? { enabled: true, operationAnnotations: true } : { operationAnnotations: false })} /></SettingRow>
  <SettingRow title="Retention" description="Delete old journal data on dashboard start, hourly, and on the next mcp serve."><Toggle id="retention-enabled" label="Retention enabled" checked={journal.retention.enabled} onChange={value => setRetention({ enabled: value })} /></SettingRow>
  <div className="settings-retention"><NumberField id="retention-age" label="Maximum age (days)" unit="days" value={journal.retention.maxAgeDays} min={1} max={3650} error={fieldErrors["journal.retention.maxAgeDays"]} onCommit={value => setRetention({ maxAgeDays: value })} /><NumberField id="retention-size" label="Maximum size (MB)" unit="MB" value={journal.retention.maxSizeMb} min={10} max={102400} error={fieldErrors["journal.retention.maxSizeMb"]} onCommit={value => setRetention({ maxSizeMb: value })} /></div><StatusLine status={status.journal} />
</section>
<section id="settings-agents" aria-label="Agents" className="settings-card"><h2>Agents</h2><p>Reading agent transcripts and compare rules for agents.</p>
  <SettingRow title="Read agent transcripts" description="Reads local Claude Code and Codex transcripts to show conversation titles and Claude's model and effort. Only titles, ids, model and effort are stored."><Toggle id="dashboard-agent-transcripts" label="Read agent transcripts" checked={draft.dashboard.agentTranscripts.enabled} onChange={value => setDashboard({ agentTranscripts: { enabled: value } })} /></SettingRow>
  <div className="settings-transcript-status"><AgentTranscriptsStatus /></div>
  <SettingRow title="Require compare subject and scope" restart description={<><p>When enabled, CLI compare needs both <code>--subject</code> and <code>--scope</code>, and MCP compare needs both fields. This setting is on by default.</p><p>CLI applies changes on the next invocation. Restart running MCP servers and reconnect their clients to apply changes; saving here does not restart them.</p></>}><Toggle id="compare-require-context" label="Require compare subject and scope" checked={draft.compare.requireContext} onChange={value => setCompare({ requireContext: value })} /></SettingRow><StatusLine status={status.compare} />
</section>
<section id="settings-dashboard" aria-label="Dashboard" className="settings-card"><h2>Dashboard</h2><p>This dashboard process.</p>
  <SettingRow title="Start with mcp serve" description="Launch the dashboard when an MCP server starts."><Toggle id="dashboard-autostart" label="Start with mcp serve" checked={draft.dashboard.autoStart} onChange={value => setDashboard({ autoStart: value })} /></SettingRow>
  <SettingRow title="Safety notifications" description="Show safety warnings raised by running sessions."><Toggle id="dashboard-safety-notifications" label="Safety notifications" checked={draft.dashboard.safetyNotifications} onChange={value => setDashboard({ safetyNotifications: value })} /></SettingRow>
  <NumberField id="dashboard-idle" label="Idle shutdown (hours)" unit="hours" description="Stop the dashboard after this long without activity." value={draft.dashboard.idleShutdownHours} min={1} max={168} error={fieldErrors["dashboard.idleShutdownHours"]} onCommit={value => setDashboard({ idleShutdownHours: value })} />
  <SettingRow title="Port" description="Change the port in config.json; the running dashboard keeps its port."><code className="rounded bg-muted px-2 py-1">{draft.dashboard.port}</code></SettingRow><StatusLine status={status.dashboard} />
</section>
<section id="settings-prices" aria-label="Model prices" className="settings-card"><h2>Model prices</h2><p>Reference data used for Usage costs.</p><ModelPricesCard /></section></div></div>
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
