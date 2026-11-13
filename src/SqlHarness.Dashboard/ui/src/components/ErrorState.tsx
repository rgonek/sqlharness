import { NotFoundError, UnauthorizedError } from "@/api/client"
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"
import { Button } from "@/components/ui/button"

export function ErrorState({ error, onRetry, retrying = false }: { error: unknown; onRetry?: () => void; retrying?: boolean }) {
  if (error instanceof UnauthorizedError) {
    return (
      <Alert variant="destructive">
        <AlertTitle>Dashboard session expired</AlertTitle>
        <AlertDescription>Run sqlharness dashboard again to open a new session.</AlertDescription>
      </Alert>
    )
  }
  if (error instanceof NotFoundError) {
    return (
      <Alert>
        <AlertTitle>Not found</AlertTitle>
        <AlertDescription>The requested record does not exist in the activity journal.</AlertDescription>
      </Alert>
    )
  }
  return (
    <Alert variant="destructive" aria-busy={retrying || undefined}>
      <AlertTitle>Request failed</AlertTitle>
      <AlertDescription className="break-words text-sm">{error instanceof Error ? error.message : String(error)}</AlertDescription>
      {onRetry && <div className="mt-2 flex items-center gap-2">
        <Button variant="outline" onClick={onRetry} disabled={retrying}>{retrying ? "Retrying…" : "Try again"}</Button>
        <span role="status" className="sr-only">{retrying ? "Retrying request" : ""}</span>
      </div>}
    </Alert>
  )
}
