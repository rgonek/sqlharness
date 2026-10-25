import { Link, Outlet, useRouterState } from "@tanstack/react-router"
import { buttonVariants } from "@/components/ui/button"
import { Separator } from "@/components/ui/separator"
import { ConnectionBanner } from "@/components/ConnectionBanner"
import { ThemeMenu } from "@/components/ThemeMenu"
import { BrandLogo } from "@/components/BrandLogo"
import { useEffect, useState, useSyncExternalStore } from "react"
import { useProfiles, useSettings } from "@/api/queries"
import { ApprovalDialog } from "@/mutations/ApprovalDialog"
import { ApprovalToast } from "@/mutations/ApprovalToast"
import { mutationStore } from "@/mutations/mutationStore"
import { useMutationEvents } from "@/mutations/useMutationEvents"
import { SafetyNoticeToast, type SafetyNotice } from "@/components/SafetyNoticeToast"
import { useLiveFeed, subscribeSafetyNotices } from "@/live/useLiveFeed"

const links = [
  { to: "/", label: "Activity", exact: true },
  { to: "/stats", label: "Usage", exact: false },
  { to: "/profiles", label: "Profiles", exact: false },
  { to: "/settings", label: "Settings", exact: false },
] as const

function SafetyNoticeFeed() {
  const [notices, setNotices] = useState<SafetyNotice[]>([])
  useEffect(() => subscribeSafetyNotices(item => {
    setNotices(current => [item, ...current.filter(existing => existing.operationId !== item.operationId)].slice(0, 20))
  }), [])
  return <SafetyNoticeToast enabled notices={notices} />
}

export function AppLayout() {
  const pathname = useRouterState({ select: state => state.location.pathname })
  const { data: profiles } = useProfiles()
  const { data: settings } = useSettings()
  const safetyNotificationsEnabled = settings?.settings.dashboard.safetyNotifications ?? false
  const mutationState = useSyncExternalStore(mutationStore.subscribe, mutationStore.getSnapshot, mutationStore.getSnapshot)
  useMutationEvents()
  useLiveFeed()
  const productionYoloProfiles = profiles?.profiles.filter(profile => profile.mutationMode?.toLowerCase() === "yolo" && profile.acknowledgeProductionYolo && /^(prod|production|staging)$/i.test(profile.environment ?? "")) ?? []
  return (
    <div className="min-h-screen">
      <ConnectionBanner />
      {productionYoloProfiles.map(profile => <div key={profile.name} role="alert" className="border-b border-destructive/40 bg-destructive/10 px-4 py-3 text-center font-semibold text-destructive">YOLO is on for production profile {profile.name}</div>)}
      <ApprovalToast />
      {safetyNotificationsEnabled && <SafetyNoticeFeed />}
      <ApprovalDialog requestId={mutationState.openRequestId} />
      <header className="app-header flex items-center gap-3 p-4">
        <Link to="/" aria-label="SQLHarness home" className="shrink-0 rounded-sm focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring">
          <BrandLogo />
        </Link>
        <nav aria-label="Main navigation" className="flex min-w-0 flex-1 gap-1 overflow-x-auto">
          {links.map(link => {
            const active = link.exact ? pathname === link.to || /^\/(sessions|agent-sessions|operations|named-operations|mutations)(\/|$)/.test(pathname) : pathname === link.to || pathname.startsWith(`${link.to}/`)
            return (
              <Link key={link.to} to={link.to} className={buttonVariants({ variant: active ? "secondary" : "ghost", size: "sm" })}>
                {link.label}
              </Link>
            )
          })}
        </nav>
        <div className="shrink-0"><ThemeMenu /></div>
      </header>
      <Separator />
      <main className="app-main mx-auto min-w-0 space-y-4 p-4" data-page={pathname === "/" ? "activity" : pathname.split("/")[1]}>
        <Outlet />
      </main>
    </div>
  )
}
