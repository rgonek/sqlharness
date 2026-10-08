import { useState } from "react"
import type { OperationSummary } from "@/api/types"
import { Badge } from "@/components/ui/badge"
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog"
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip"
import { describeError } from "@/lib/errors"
import { statusVariant } from "@/lib/flags"

type StatusFields = Pick<OperationSummary, "status" | "exitCode" | "errorKind" | "errorMessage">

export function StatusBadge({ operation }: { operation: StatusFields }) {
  const [open, setOpen] = useState(false)
  const badge = <Badge variant={statusVariant(operation.status)}>{operation.status}</Badge>
  if (operation.status !== "failed" && operation.status !== "rejected") return badge

  const heading = [operation.errorKind ?? "error", operation.exitCode !== null ? `exit ${operation.exitCode}` : null]
    .filter(Boolean)
    .join(" · ")
  const sentence = describeError(operation.exitCode, operation.errorKind)
  return (
    <>
      <Tooltip>
        <TooltipTrigger render={<button type="button" aria-haspopup="dialog" onClick={() => setOpen(true)} />}>
          {badge}
        </TooltipTrigger>
        <TooltipContent>
          <div>{heading}</div>
          <div>{sentence}</div>
        </TooltipContent>
      </Tooltip>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{heading}</DialogTitle>
            <DialogDescription>{sentence}</DialogDescription>
          </DialogHeader>
          {operation.errorMessage !== null ? (
            <pre className="max-h-96 overflow-auto whitespace-pre-wrap font-mono text-sm">{operation.errorMessage}</pre>
          ) : (
            <p className="text-sm text-muted-foreground">
              The full message is stored only when <code>journal.storeSensitive</code> is enabled in{" "}
              <a href="/settings">Settings</a>.
            </p>
          )}
        </DialogContent>
      </Dialog>
    </>
  )
}
