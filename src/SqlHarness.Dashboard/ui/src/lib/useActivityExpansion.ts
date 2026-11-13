import { useEffect, useState, type SetStateAction } from "react"
import { boundActivityExpansion, readActivityExpansionEntries, rememberActivityExpansion } from "./activitySearch"

export function useActivityExpansion() {
  const [entries, setEntries] = useState(readActivityExpansionEntries)
  const expansion: Record<number, boolean> = Object.fromEntries(entries)
  useEffect(() => { rememberActivityExpansion(Object.fromEntries(entries)) }, [entries])
  const setExpansion = (update: SetStateAction<Record<number, boolean>>) => setEntries(previous => {
    const current = Object.fromEntries(previous)
    return boundActivityExpansion(previous, typeof update === "function" ? update(current) : update)
  })
  return [expansion, setExpansion] as const
}
