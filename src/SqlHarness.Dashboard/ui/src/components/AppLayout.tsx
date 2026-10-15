import { Link, Outlet, useRouterState } from "@tanstack/react-router"
import { buttonVariants } from "@/components/ui/button"
import { Separator } from "@/components/ui/separator"
import { ConnectionBanner } from "@/components/ConnectionBanner"
import { ThemeMenu } from "@/components/ThemeMenu"
import { BrandLogo } from "@/components/BrandLogo"

const links = [
  { to: "/", label: "Live", exact: true },
  { to: "/sessions", label: "Sessions", exact: false },
  { to: "/operations", label: "Operations", exact: false },
  { to: "/stats", label: "Statistics", exact: false },
  { to: "/profiles", label: "Profiles", exact: false },
  { to: "/settings", label: "Settings", exact: false },
] as const

export function AppLayout() {
  const pathname = useRouterState({ select: state => state.location.pathname })
  return (
    <div className="min-h-screen">
      <ConnectionBanner />
      <header className="mx-auto flex max-w-7xl items-center gap-3 p-4">
        <Link to="/" aria-label="SQLHarness home" className="shrink-0 rounded-sm focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring">
          <BrandLogo />
        </Link>
        <nav aria-label="Main navigation" className="flex min-w-0 flex-1 gap-1 overflow-x-auto">
          {links.map(link => {
            const active = link.exact ? pathname === link.to : pathname === link.to || pathname.startsWith(`${link.to}/`)
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
      <main className="mx-auto max-w-7xl space-y-4 p-4">
        <Outlet />
      </main>
    </div>
  )
}
