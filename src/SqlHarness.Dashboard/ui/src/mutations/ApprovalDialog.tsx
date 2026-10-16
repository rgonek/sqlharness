import { useEffect, useState } from "react"
import { Button } from "@/components/ui/button"
import { Badge } from "@/components/ui/badge"
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog"
import { SqlBlock } from "@/components/SqlBlock"
import { postMutationDecision } from "@/api/client"
import { mutationStore, usePendingMutations } from "./mutationStore"

export function ApprovalDialog({ requestId }: { requestId: string | null }) {
  const pending = usePendingMutations()
  const item = pending.find(request => request.requestId === requestId)
  const [openedAtState, setOpenedAtState] = useState<{ requestId: string | null; time: number | null }>({ requestId: null, time: null })
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
    if (!requestId || (decision === "approve" && (openedAt === null || currentTime - openedAt < 1_000 || !item || Date.parse(item.expiresAt) <= currentTime))) return
    setMessageState({ requestId, message: "" })
    try {
      const result = await postMutationDecision(requestId, decision)
      if (result === "not-pending") setMessageState({ requestId, message: "This request is no longer pending" })
      else if (result === "busy") setMessageState({ requestId, message: "Another change is already running for this profile. This request was not executed." })
      else if (decision === "deny") close()
    } catch {
      setMessageState({ requestId, message: "The decision could not be submitted. Try again." })
    }
  }
  const remaining = item ? Math.max(0, Math.ceil((Date.parse(item.expiresAt) - now) / 1_000)) : 0
  const environment = item?.environment || "unknown"
  const riskyEnvironment = /^(prod|production|staging)$/i.test(environment)
  const open = requestId !== null && (item !== undefined || Boolean(message))
  return (
    <Dialog open={open} onOpenChange={value => { if (!value) close() }}>
      <DialogContent className="max-h-[90vh] max-w-4xl overflow-y-auto" onKeyDownCapture={event => { if (event.key === "Enter") event.preventDefault() }}>
        {item ? <>
          <DialogHeader>
            <DialogTitle className="flex flex-wrap items-center gap-3">
              <span className={`text-3xl font-bold ${riskyEnvironment ? "text-destructive" : "text-foreground"}`}>{environment}</span>
              <Badge variant="outline">{item.request.tier}</Badge>
            </DialogTitle>
            <DialogDescription>Review the requested database change before deciding.</DialogDescription>
          </DialogHeader>
          <dl className="grid gap-2 text-sm sm:grid-cols-3">
            <div><dt className="text-muted-foreground">Profile</dt><dd>{item.request.profile}</dd></div>
            <div><dt className="text-muted-foreground">Server</dt><dd>{item.request.server}</dd></div>
            <div><dt className="text-muted-foreground">Database</dt><dd>{item.request.database}</dd></div>
          </dl>
          {item.writerSameAsReader && <p role="alert" className="rounded-md border border-destructive/40 bg-destructive/10 p-3">The writer uses the same account as the reader.</p>}
          {hasMultipleStatements(item.request.sql) && <p role="alert" className="rounded-md border border-destructive/40 bg-destructive/10 p-3">This request contains multiple statements.</p>}
          <SqlBlock sql={item.request.sql} engine={item.request.engine} label="SQL" initiallyFormatted={false} />
          <div className="space-y-2">
            <h3 className="font-medium">Parameters</h3>
            <table className="w-full border-collapse text-left text-sm"><thead><tr><th className="border-b p-2">Name</th><th className="border-b p-2">Type</th><th className="border-b p-2">Value</th></tr></thead>
              <tbody>{item.request.parameters.map((parameter, index) => <tr key={`${parameter.name}-${index}`}><td className="border-b p-2">{parameter.name}</td><td className="border-b p-2">{parameter.type ?? "—"}</td><td className="border-b p-2 whitespace-pre-wrap">{parameter.value ?? "NULL"}</td></tr>)}</tbody>
            </table>
          </div>
          <p className="text-sm text-muted-foreground">Digest {item.request.digest.slice(0, 12)} · Expires in {remaining}s</p>
          {message && <p role="alert" className="text-destructive">{message}</p>}
          <DialogFooter>
            <Button variant="outline" onClick={() => void decide("deny")}>Deny</Button>
            <Button onClick={() => void decide("approve")} disabled={openedAt === null || now - openedAt < 1_000 || remaining === 0}>Approve</Button>
          </DialogFooter>
        </> : <><DialogHeader><DialogTitle>Request no longer pending</DialogTitle></DialogHeader>{message && <p role="alert">{message}</p>}</>}
      </DialogContent>
    </Dialog>
  )
}

function hasMultipleStatements(sql: string): boolean {
  return /;\s*\S/.test(sql)
}
