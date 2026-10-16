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
  const [openedAt, setOpenedAt] = useState<number | null>(null)
  const [now, setNow] = useState(Date.now())
  const [message, setMessage] = useState("")
  useEffect(() => {
    if (!requestId) return
    setOpenedAt(Date.now())
    setNow(Date.now())
    setMessage("")
    const timer = window.setInterval(() => setNow(Date.now()), 250)
    return () => window.clearInterval(timer)
  }, [requestId])
  const close = () => mutationStore.close()
  const decide = async (decision: "approve" | "deny") => {
    const currentTime = Date.now()
    if (!requestId || (decision === "approve" && (openedAt === null || currentTime - openedAt < 1_000 || !item || Date.parse(item.expiresAt) <= currentTime))) return
    setMessage("")
    try {
      const result = await postMutationDecision(requestId, decision)
      if (result === "not-pending") setMessage("This request is no longer pending")
      else if (decision === "deny") close()
    } catch {
      setMessage("The decision could not be submitted. Try again.")
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
