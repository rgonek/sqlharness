import type { OperationStatus } from "@/api/types"
import { Badge } from "@/components/ui/badge"
import { statusVariant } from "@/lib/flags"

export function StatusBadge({ status }: { status: OperationStatus }) {
  return <Badge variant={statusVariant(status)}>{status}</Badge>
}
