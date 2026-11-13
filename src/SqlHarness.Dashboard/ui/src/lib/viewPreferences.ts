import { useEffect, useState, type Dispatch, type SetStateAction } from "react"

export const isViewString = (value: unknown): value is string => typeof value === "string" && value.length <= 512
export const isViewBoolean = (value: unknown): value is boolean => typeof value === "boolean"
export const isViewRecord = (value: unknown): value is Record<string, unknown> => !!value && typeof value === "object" && !Array.isArray(value)
export const isDimensionSelection = (value: unknown): value is Record<string, string | null> =>
  isViewRecord(value) && Object.entries(value).length <= 20 && Object.entries(value).every(([key, item]) => key.length <= 128 && (item === null || isViewString(item)))

/** Tab-local, bounded display preferences; never store SQL, results or credentials here. */
export function useViewPreference<T>(name: string, initial: T | (() => T), accept: (value: unknown) => value is T): [T, Dispatch<SetStateAction<T>>] {
  const key = "sqlharness.view." + name
  const [value, setValue] = useState<T>(() => {
    try {
      const raw = sessionStorage.getItem(key)
      if (raw && raw.length <= 16_384) {
        const parsed: unknown = JSON.parse(raw)
        if (accept(parsed)) return parsed
      }
    } catch { /* Invalid or unavailable preferences use the initial view. */ }
    return typeof initial === "function" ? (initial as () => T)() : initial
  })
  useEffect(() => {
    try {
      if (value === undefined) { sessionStorage.removeItem(key); return }
      const raw = JSON.stringify(value)
      if (raw.length <= 16_384 && accept(value)) sessionStorage.setItem(key, raw)
      else sessionStorage.removeItem(key)
    } catch { /* A failed preference write must not interfere with navigation. */ }
  }, [key, value, accept])
  return [value, setValue]
}
