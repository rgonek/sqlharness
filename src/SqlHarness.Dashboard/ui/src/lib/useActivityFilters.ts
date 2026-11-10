import { useEffect, useState } from "react"
import { useNavigate, useSearch } from "@tanstack/react-router"
import { compactActivitySearch, defaultActivityFilters, readActivityFilters, rememberActivityFilters, type ActivityFilters } from "./activitySearch"

export function useActivityFilters() {
  const routeSearch = useSearch({ from: "/" })
  const navigate = useNavigate()
  const [remembered] = useState(readActivityFilters)
  const filters: ActivityFilters = { ...defaultActivityFilters(), ...(Object.keys(routeSearch).length ? routeSearch : remembered) }
  useEffect(() => { rememberActivityFilters(filters) }, [JSON.stringify(filters)])
  const change = (patch: Partial<ActivityFilters>) => {
    const next = { ...filters, ...patch }
    rememberActivityFilters(next)
    void navigate({ to: "/", search: compactActivitySearch(next), replace: true, resetScroll: false })
  }
  return { filters, change }
}
