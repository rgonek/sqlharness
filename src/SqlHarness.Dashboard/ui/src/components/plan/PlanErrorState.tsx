import { HttpStatusError, NotFoundError } from "@/api/client"
import { Button } from "@/components/ui/button"

export function PlanErrorState({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  let status: number | null = null
  let code: string | null = null
  let message: string
  if (error instanceof HttpStatusError) {
    status = error.status
    code = error.code
    message = error.message
  } else if (error instanceof NotFoundError) {
    status = 404
    message = "The selected plan document or operation was not found."
  } else if (error instanceof Error) message = error.message
  else if (error && typeof error === "object") {
    const detail = error as { code?: unknown; message?: unknown }
    code = typeof detail.code === "string" ? detail.code : null
    message = typeof detail.message === "string" ? detail.message : "The plan data could not be loaded."
  } else message = "The plan data could not be loaded."

  const reason = status === 400 ? "The plan source selection is invalid."
    : status === 409 ? "Plan data is unavailable while journal.storeSensitive is off."
    : status === 413 ? "The plan response exceeds the dashboard size limit."
    : status === 422 ? "The stored plan could not be converted into a complete graph."
    : status === 404 ? "The selected plan document is missing."
    : null

  return <div role="alert" className="space-y-1 rounded border border-destructive/40 bg-destructive/5 p-3 text-sm">
    <p className="font-medium">{reason ?? `Plan request failed${status ? ` (${status})` : ""}.`}</p>
    {status !== null && <p>HTTP {status}{code ? ` · ${code}` : ""}</p>}
    <p>{message}</p>
    {onRetry && <Button type="button" variant="outline" size="sm" className="mt-1 underline" onClick={onRetry}>Retry</Button>}
  </div>
}
