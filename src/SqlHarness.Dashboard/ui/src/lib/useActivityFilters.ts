import { useEffect, useMemo, useState } from "react"
import { useNavigate, useSearch } from "@tanstack/react-router"
import { compactActivitySearch, defaultActivityFilters, readActivityFilters, rememberActivityFilters, type ActivityFilters } from "./activitySearch"

export function useActivityFilters() {
  const routeSearch = useSearch({ from: "/" })
  const navigate = useNavigate()
  const [remembered] = useState(readActivityFilters)
  const filters = useMemo<ActivityFilters>(() => ({ ...defaultActivityFilters(), ...(Object.keys(routeSearch).length ? routeSearch : remembered) }), [routeSearch, remembered])
  useEffect(() => { rememberActivityFilters(filters) }, [filters])
  useEffect(() => {
    // Give even an initially bare entry its own explicit selection; later visits must not rewrite its meaning.
    if (!Object.keys(routeSearch).length) void navigate({ to: "/", search: compactActivitySearch(filters), replace: true, resetScroll: false })
  }, [routeSearch, filters, navigate])
  const change = (patch: Partial<ActivityFilters>) => {
    const next = { ...filters, ...patch }
    rememberActivityFilters(next)
    void navigate({ to: "/", search: compactActivitySearch(next), replace: true, resetScroll: false })
  }
  return { filters, change }
}
