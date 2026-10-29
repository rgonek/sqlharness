import { useEffect, useMemo, useState } from "react"
import { Alert, AlertDescription } from "@/components/ui/alert"
import { Separator } from "@/components/ui/separator"
import { Button } from "@/components/ui/button"
import { Badge } from "@/components/ui/badge"
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog"
import { agentLabel } from "@/lib/agents"
import { Link } from "@tanstack/react-router"
import { SegmentedRadioGroup } from "@/components/SegmentedRadioGroup"
import { inlineMutationSql } from "./sqlDisplay"
import { SqlBlock } from "@/components/SqlBlock"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { postMutationDecision } from "@/api/client"
import { mutationStore, usePendingMutations } from "./mutationStore"

export function ApprovalDialog({ requestId }: { requestId: string | null }) {
  const pending = usePendingMutations()
  const item = pending.find(request => request.requestId === requestId)
  const [openedAtState, setOpenedAtState] = useState<{ requestId: string | null; time: number | null }>({ requestId: null, time: null })
  const [modeState, setModeState] = useState<{ id: string | null; mode: string }>({ id: null, mode: "sent" })
  const [submitting, setSubmitting] = useState(false)
  const preview = useMemo(() => item ? inlineMutationSql(item.request.sql, item.request.engine, item.request.parameters) : null, [item])
  const mode = modeState.id === requestId ? modeState.mode : "sent"
  const [now, setNow] = useState(0)
  const [messageState, setMessageState] = useState<{ requestId: string | null; message: string }>({ requestId: null, message: "" })
  useEffect(() => {
    const initialize = window.setTimeout(() => {
      const currentTime = Date.now()
      setOpenedAtState({ requestId, time: requestId ? currentTime : null })
      setNow(currentTime)
    }, 0)
    const timer = window.setInterval(() => setNow(Date.now()), 250)
    return () => { window.clearTimeout(initialize); window.clearInterval(timer) }
  }, [requestId])
  const openedAt = openedAtState.requestId === requestId ? openedAtState.time : null
  const message = messageState.requestId === requestId ? messageState.message : ""
  const close = () => mutationStore.close()
  const decide = async (decision: "approve" | "deny") => {
    const currentTime = now
    if (submitting || !item || Date.parse(item.expiresAt) <= Date.now() || !requestId || (decision === "approve" && (openedAt === null || currentTime - openedAt < 1_000 || !item || Date.parse(item.expiresAt) <= currentTime))) return
    setMessageState({ requestId, message: "" })
    setSubmitting(true)
    try {
      const result = await postMutationDecision(requestId, decision)
      if (result === "not-pending") setMessageState({ requestId, message: "This request is no longer pending" })
      else if (result === "busy") setMessageState({ requestId, message: "Another change is already running for this profile. This request was not executed." })
      else if (decision === "deny") close()
    } catch {
      setMessageState({ requestId, message: "The decision could not be submitted. Try again." })
    } finally { setSubmitting(false) }
  }
  const remaining = item ? Math.max(0, Math.ceil((Date.parse(item.expiresAt) - now) / 1_000)) : 0
  const environment = item?.environment || "unknown"
  const riskyEnvironment = /^(prod|production|staging)$/i.test(environment)
  const open = requestId !== null && (item !== undefined || Boolean(message))
  return (
    <Dialog open={open} onOpenChange={value => { if (!value) close() }}>
      <DialogContent className="mutation-dialog max-h-[90vh] overflow-y-auto sm:max-w-[720px]" onKeyDownCapture={event => { if (event.key === "Enter") event.preventDefault() }}>
        {item ? <>
          <DialogHeader className="pr-6">
            <DialogTitle className="flex flex-wrap items-center gap-3">
              <span className={`text-xl font-semibold ${riskyEnvironment ? "text-destructive" : "text-foreground"}`}>{environment}</span>
              <Badge variant="outline">{item.request.tier}</Badge><span className="text-xs font-normal text-muted-foreground">{pending.indexOf(item) + 1} of {pending.length} pending</span>
            </DialogTitle>
            <DialogDescription className="break-all">{item.request.profile} | {item.request.server} / {item.request.database} | {item.request.engine}</DialogDescription>
            <p className="text-sm text-muted-foreground">Requested by {item.requester ? <>{agentLabel(item.requester.agentLabel || "unknown")} in <Link to="/sessions/$id" params={{ id: String(item.requester.sessionId) }} className="underline">session {item.requester.sessionId}</Link></> : "unknown requester"}</p>
          </DialogHeader>
          <Separator />
          {item.writerSameAsReader && <Alert variant="destructive"><AlertDescription>The writer uses the same account as the reader.</AlertDescription></Alert>}
          {hasMultipleStatements(item.request.sql) && <Alert variant="destructive"><AlertDescription>This request contains multiple statements.</AlertDescription></Alert>}
          <SegmentedRadioGroup label="SQL display" value={mode} onValueChange={value => setModeState({ id: requestId, mode: value })} options={[
            { value: "inline", label: "Values inline", disabled: Boolean(preview?.reason) },
            { value: "sent", label: "As sent" },
          ]} />
          {preview?.reason && <p className="text-sm text-muted-foreground">{preview.reason}</p>}
          <div className="rounded-md border p-3"><SqlBlock sql={mode === "inline" && preview && !preview.reason ? preview.sql : item.request.sql} engine={item.request.engine} label="SQL" initiallyFormatted={false} /></div>
          <div className="flex flex-col gap-2">
            <h3 className="font-medium">Parameters</h3>
            <Table>
              <TableHeader><TableRow><TableHead>Name</TableHead><TableHead>Type</TableHead><TableHead>Value</TableHead></TableRow></TableHeader>
              <TableBody>{item.request.parameters.map((parameter, index) => <TableRow key={`${parameter.name}-${index}`}><TableHead scope="row" className="whitespace-normal break-all">{parameter.name}</TableHead><TableCell className="whitespace-normal break-all">{parameter.type ?? "—"}</TableCell><TableCell className="whitespace-pre-wrap break-all">{parameter.value ?? "NULL"}</TableCell></TableRow>)}</TableBody>
            </Table>
          </div>

          {message && <p role="alert" className="text-destructive">{message}</p>}
          <Separator />
          <DialogFooter>
            <p className="mr-auto self-center text-xs text-muted-foreground">Expires in {Math.floor(remaining / 60)}:{String(remaining % 60).padStart(2, "0")} · Digest {item.request.digest.slice(0, 12)}</p>
            <Button variant="outline" disabled={submitting || remaining === 0} onClick={() => void decide("deny")}>Deny</Button>
            <Button onClick={() => void decide("approve")} disabled={submitting || openedAt === null || now - openedAt < 1_000 || remaining === 0}>Approve</Button>
          </DialogFooter>
        </> : <><DialogHeader><DialogTitle>Request no longer pending</DialogTitle></DialogHeader>{message && <p role="alert">{message}</p>}</>}
      </DialogContent>
    </Dialog>
  )
}

function hasMultipleStatements(sql: string): boolean {
  return /;\s*\S/.test(sql)
}
