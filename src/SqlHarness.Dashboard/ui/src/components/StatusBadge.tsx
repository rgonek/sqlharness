import { useState } from "react"
import type { OperationSummary } from "@/api/types"
import { Badge } from "@/components/ui/badge"
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle } from "@/components/ui/dialog"
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip"
import { describeError } from "@/lib/errors"
import { badgeTier, statusVariant } from "@/lib/flags"

type StatusFields = Pick<OperationSummary, "status" | "exitCode" | "errorKind" | "errorMessage" | "cancelReason">

export function StatusBadge({ operation }: { operation: StatusFields }) {
  const [open, setOpen] = useState(false)
  if (badgeTier(operation.status) === "info") return <span className="text-sm text-muted-foreground">{operation.status}</span>
  const badge = <Badge data-tier={badgeTier(operation.status)} variant={statusVariant(operation.status)}>{operation.status}</Badge>
  if (operation.status !== "failed" && operation.status !== "rejected") return badge

  const heading = [operation.errorKind ?? "error", operation.exitCode !== null ? `exit ${operation.exitCode}` : null]
    .filter(Boolean)
    .join(" · ")
  const sentence = describeError(operation.exitCode, operation.errorKind, operation.cancelReason)
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
              No error message is available for this operation.
            </p>
          )}
        </DialogContent>
      </Dialog>
    </>
  )
}
