import { useEffect } from "react"
import { compactActivitySearch, type ActivityFilters } from "./activitySearch"

const SCROLL_KEY = "sqlharness.activity.scroll"
/** Restore the most recent Activity viewport for return links as well as browser history. */
export function useActivityScroll(filters: ActivityFilters) {
  const key = JSON.stringify(compactActivitySearch(filters))
  useEffect(() => {
    let top = window.scrollY
    try {
      const saved = JSON.parse(sessionStorage.getItem(SCROLL_KEY) ?? "null")
      if (saved?.key === key && Number.isFinite(saved.top) && saved.top >= 0 && saved.top <= 10_000_000) top = saved.top
    } catch { /* Scroll memory is optional. */ }
    const save = () => {
      try { sessionStorage.setItem(SCROLL_KEY, JSON.stringify({ key, top })) } catch { /* Disabled storage. */ }
    }
    let pending: number | undefined
    const restore = requestAnimationFrame(() => window.scrollTo({ top, behavior: "instant" }))
    const onScroll = () => {
      top = window.scrollY
      if (pending === undefined) pending = requestAnimationFrame(() => { pending = undefined; save() })
    }
    window.addEventListener("scroll", onScroll, { passive: true })
    return () => {
      cancelAnimationFrame(restore)
      if (pending !== undefined) cancelAnimationFrame(pending)
      window.removeEventListener("scroll", onScroll)
      save()
    }
  }, [key])
}
