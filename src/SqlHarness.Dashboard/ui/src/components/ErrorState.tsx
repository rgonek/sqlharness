import { NotFoundError, UnauthorizedError } from "@/api/client"
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"

export function ErrorState({ error }: { error: unknown }) {
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
    <Alert variant="destructive">
      <AlertTitle>Request failed</AlertTitle>
      <AlertDescription>{error instanceof Error ? error.message : String(error)}</AlertDescription>
    </Alert>
  )
}
