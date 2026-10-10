const byKind: Record<string, string> = {
  safety_rejected: "SQLHarness rejected the request before running it (validation or safety rule).",
  authentication_failed: "The database login failed.",
  target_mismatch: "The connected server or database did not match the profile.",
  sql_execution_failed: "The database returned an error while running the SQL.",
  local_storage_failed: "SQLHarness could not write its local files or artifacts.",
  operation_failed: "The operation failed.",
  cancelled: "The operation was cancelled before it finished.",
  unhandled_exception: "SQLHarness stopped on an unexpected error.",
}

const byExitCode: Record<number, string> = {
  2: byKind.safety_rejected,
  3: byKind.authentication_failed,
  4: byKind.target_mismatch,
  5: byKind.sql_execution_failed,
  6: byKind.local_storage_failed,
}

const byCancelReason: Record<string, string> = {
  client: "Cancelled by the client.",
  shutdown: "Cancelled because the MCP host shut down.",
  deadline: "Cancelled when the call time budget ran out.",
}

/** A fixed, data-free sentence for an error kind; the stored message (if any) carries the details. */
export function describeError(exitCode: number | null, errorKind: string | null, cancelReason: string | null = null): string {
  if (errorKind === "cancelled" && cancelReason && byCancelReason[cancelReason]) return byCancelReason[cancelReason]
  return (errorKind && byKind[errorKind]) || (exitCode !== null && byExitCode[exitCode]) || byKind.operation_failed
}
