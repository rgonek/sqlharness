import { useState } from "react"
import type { OperationSummary } from "@/api/types"

const storageKey = "sqlharness.showInternalOperations"

export function isInternalOperation(operation: OperationSummary): boolean {
  return operation.operation.toLowerCase().includes("artifact")
}

function readPreference(): boolean {
  try { return window.localStorage.getItem(storageKey) === "true" } catch { return false }
}

export function useShowInternalOperations(): [boolean, (show: boolean) => void] {
  const [show, setShow] = useState(readPreference)
  return [show, value => {
    setShow(value)
    try { window.localStorage.setItem(storageKey, String(value)) } catch { /* storage may be unavailable */ }
  }]
}
