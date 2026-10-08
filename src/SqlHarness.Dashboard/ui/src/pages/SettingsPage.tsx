import { useState } from "react"
import { FieldErrorsError } from "@/api/client"
import { useSaveSettings, useSettings } from "@/api/queries"
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

function Toggle({ id, label, checked, onChange }: { id: string; label: string; checked: boolean; onChange: (value: boolean) => void }) {
  return (
    <div className="flex items-center gap-2">
      <Switch id={id} checked={checked} onCheckedChange={onChange} aria-label={label} />
      <Label htmlFor={id}>{label}</Label>
    </div>
  )
}

function NumberField({ id, label, value, error, onChange }: { id: string; label: string; value: number; error?: string; onChange: (value: number) => void }) {
  return (
    <div className="grid gap-1">
      <Label htmlFor={id}>{label}</Label>
      <Input id={id} type="number" value={value} aria-invalid={error ? true : undefined} onChange={event => onChange(Number(event.target.value))} className="w-40" />
      {error && <p className="text-sm text-destructive">{error}</p>}
    </div>
  )
}

export function SettingsPage() {
  const query = useSettings()
  const save = useSaveSettings()
  // Edits are kept as a patch over the server copy, so the form follows the query without an effect.
  const [edited, setEdited] = useState<Settings | null>(null)
  const [confirming, setConfirming] = useState(false)
  const [saved, setSaved] = useState(false)

  if (query.error) return <ErrorState error={query.error} />
  if (query.isPending) return <Skeleton className="h-64 w-full" />

  const draft = edited ?? query.data.settings
  const errors = save.error instanceof FieldErrorsError ? Object.fromEntries(save.error.errors.map(e => [e.field, e.message])) : {}
  const journal = draft.journal
  const setJournal = (next: Partial<Settings["journal"]>) => { setSaved(false); setEdited({ ...draft, journal: { ...journal, ...next } }) }
  const setRetention = (next: Partial<Settings["journal"]["retention"]>) => setJournal({ retention: { ...journal.retention, ...next } })
  const setDashboard = (next: Partial<Settings["dashboard"]>) => { setSaved(false); setEdited({ ...draft, dashboard: { ...draft.dashboard, ...next } }) }
  const submit = (overwriteInvalid = false) =>
    save.mutate({ settings: draft, overwriteInvalid }, { onSuccess: () => { setSaved(true); setEdited(null) } })

  return (
    <div className="space-y-4">
      <h1>Settings</h1>
      <p className="text-sm text-muted-foreground">{query.data.path}</p>
      {query.data.status === "invalid" && (
        <Alert variant="destructive">
          <AlertTitle>config.json is invalid</AlertTitle>
          <AlertDescription>
            SQLHarness is using defaults. Saving replaces the file.
            <Button variant="outline" size="sm" onClick={() => submit(true)}>Replace with these settings</Button>
          </AlertDescription>
        </Alert>
      )}
      {saved && (
        <Alert>
          <AlertTitle>Saved</AlertTitle>
          <AlertDescription>Saved. New processes use these settings; restart running <code>mcp serve</code> sessions to apply them.</AlertDescription>
        </Alert>
      )}
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
            onChange={value => (value ? setConfirming(true) : setJournal({ storeSensitive: false }))}
          />
          <Toggle id="retention-enabled" label="Retention enabled" checked={journal.retention.enabled} onChange={value => setRetention({ enabled: value })} />
          <NumberField id="retention-age" label="Maximum age (days)" value={journal.retention.maxAgeDays} error={errors["journal.retention.maxAgeDays"]} onChange={value => setRetention({ maxAgeDays: value })} />
          <NumberField id="retention-size" label="Maximum size (MB)" value={journal.retention.maxSizeMb} error={errors["journal.retention.maxSizeMb"]} onChange={value => setRetention({ maxSizeMb: value })} />
        </CardContent>
      </Card>
      <Card>
        <CardHeader>
          <CardTitle>Dashboard</CardTitle>
        </CardHeader>
        <CardContent className="grid gap-4">
          <Toggle id="dashboard-autostart" label="Start with mcp serve" checked={draft.dashboard.autoStart} onChange={value => setDashboard({ autoStart: value })} />
          <NumberField id="dashboard-idle" label="Idle shutdown (hours)" value={draft.dashboard.idleShutdownHours} error={errors["dashboard.idleShutdownHours"]} onChange={value => setDashboard({ idleShutdownHours: value })} />
          <div className="grid gap-1">
            <span className="text-sm">Port</span>
            <span>{draft.dashboard.port}</span>
            <span className="text-sm text-muted-foreground">Change the port in config.json; the running dashboard keeps its port.</span>
          </div>
        </CardContent>
      </Card>
      {query.data.status !== "invalid" && <Button onClick={() => submit()} disabled={save.isPending}>Save</Button>}
      {save.error && !(save.error instanceof FieldErrorsError) && <ErrorState error={save.error} />}

      <Dialog open={confirming} onOpenChange={setConfirming}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>Store sensitive data?</DialogTitle>
            <DialogDescription>
              From now on the journal will store SQL text, full plans (which can include parameter values), and error messages for new operations. Existing rows are not changed.
            </DialogDescription>
          </DialogHeader>
          <DialogFooter>
            <Button variant="outline" onClick={() => setConfirming(false)}>Cancel</Button>
            <Button onClick={() => { setJournal({ storeSensitive: true }); setConfirming(false) }}>Enable</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  )
}
