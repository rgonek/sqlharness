import { NotFoundError } from "@/api/client"
import { ErrorState } from "@/components/ErrorState"

export function NotFoundPage() {
  return <ErrorState error={new NotFoundError(window.location.pathname)} />
}
