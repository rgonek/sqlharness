import { useCallback, useEffect, useState } from "react"

export type ThemeMode = "system" | "light" | "dark"

const storageKey = "sqlharness.theme"
const darkQuery = "(prefers-color-scheme: dark)"
let transitionFrame: number | undefined

/** The remembered mode; storage can be missing or blocked, which means "system". */
export function readThemeMode(): ThemeMode {
  try {
    const value = localStorage.getItem(storageKey)
    return value === "light" || value === "dark" || value === "system" ? value : "system"
  } catch {
    return "system"
  }
}

export function writeThemeMode(mode: ThemeMode): void {
  try {
    localStorage.setItem(storageKey, mode)
  } catch {
    // Not remembered; the choice still applies for this page.
  }
}

/** Toggles the `dark` class that shadcn's theme uses. */
export function applyTheme(mode: ThemeMode): void {
  const dark = mode === "dark" || (mode === "system" && window.matchMedia(darkQuery).matches)
  const root = document.documentElement
  if (root.classList.contains("dark") === dark) return
  if (transitionFrame !== undefined) window.cancelAnimationFrame(transitionFrame)
  root.classList.add("theme-changing")
  root.classList.toggle("dark", dark)
  transitionFrame = window.requestAnimationFrame(() => {
    transitionFrame = window.requestAnimationFrame(() => {
      root.classList.remove("theme-changing")
      transitionFrame = undefined
    })
  })
}

export function useTheme(): { mode: ThemeMode; setMode: (mode: ThemeMode) => void } {
  const [mode, setModeState] = useState<ThemeMode>(readThemeMode)
  useEffect(() => {
    applyTheme(mode)
    if (mode !== "system") return
    const query = window.matchMedia(darkQuery)
    const follow = () => applyTheme("system")
    query.addEventListener("change", follow)
    return () => query.removeEventListener("change", follow)
  }, [mode])
  const setMode = useCallback((next: ThemeMode) => {
    writeThemeMode(next)
    setModeState(next)
  }, [])
  return { mode, setMode }
}
