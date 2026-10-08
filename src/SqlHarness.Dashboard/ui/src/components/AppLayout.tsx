import { Link, Outlet, useRouterState } from "@tanstack/react-router"
import { buttonVariants } from "@/components/ui/button"
import { Separator } from "@/components/ui/separator"
import { ThemeMenu } from "@/components/ThemeMenu"

const links = [
  { to: "/", label: "Live", exact: true },
  { to: "/sessions", label: "Sessions", exact: false },
  { to: "/stats", label: "Statistics", exact: false },
] as const

export function AppLayout() {
  const pathname = useRouterState({ select: state => state.location.pathname })
  return (
    <div className="min-h-screen">
      <header className="mx-auto flex max-w-7xl items-center gap-4 p-4">
        <span>SQLHarness</span>
        <nav className="flex gap-1">
          {links.map(link => {
            const active = link.exact ? pathname === link.to : pathname === link.to || pathname.startsWith(`${link.to}/`)
            return (
              <Link key={link.to} to={link.to} className={buttonVariants({ variant: active ? "secondary" : "ghost", size: "sm" })}>
                {link.label}
              </Link>
            )
          })}
        </nav>
        <div className="ml-auto"><ThemeMenu /></div>
      </header>
      <Separator />
      <main className="mx-auto max-w-7xl space-y-4 p-4">
        <Outlet />
      </main>
    </div>
  )
}
