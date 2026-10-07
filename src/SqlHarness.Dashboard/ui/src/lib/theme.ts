import { useEffect } from "react"

/** Follows the operating-system colour scheme by toggling the `dark` class that shadcn's theme uses. */
export function useSystemTheme(): void {
  useEffect(() => {
    const query = window.matchMedia("(prefers-color-scheme: dark)")
    const apply = () => document.documentElement.classList.toggle("dark", query.matches)
    apply()
    query.addEventListener("change", apply)
    return () => query.removeEventListener("change", apply)
  }, [])
}
