import { render, screen } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test, vi } from "vitest"
import { AppLayout } from "./AppLayout"

vi.mock("@tanstack/react-router", () => ({
  useRouterState: () => "/profiles",
  Link: ({ to, children, ...props }: React.ComponentProps<"a"> & { to: string }) => <a href={to} {...props}>{children}</a>,
  Outlet: () => <h1>Profiles</h1>,
}))
vi.mock("@/api/queries", () => ({ useProfiles: () => ({}), useSettings: () => ({}) }))
vi.mock("@/mutations/useMutationEvents", () => ({ useMutationEvents: () => {} }))
vi.mock("@/live/useLiveFeed", () => ({ useLiveFeed: () => {}, subscribeSafetyNotices: () => () => {} }))
vi.mock("@/components/ConnectionBanner", () => ({ ConnectionBanner: () => null }))
vi.mock("@/components/ThemeMenu", () => ({ ThemeMenu: () => <button>Theme</button> }))
vi.mock("@/mutations/ApprovalToast", () => ({ ApprovalToast: () => null }))
vi.mock("@/mutations/ApprovalDialog", () => ({ ApprovalDialog: () => null }))

test("the first keyboard action skips navigation to a focusable main landmark", async () => {
  render(<AppLayout />)
  await userEvent.tab()
  const skip = screen.getByRole("link", { name: "Skip to main content" })
  expect(skip).toHaveFocus()
  await userEvent.keyboard("{Enter}")
  expect(screen.getByRole("main")).toHaveFocus()
  expect(skip).toHaveAttribute("href", `#${screen.getByRole("main").id}`)
  expect(screen.getByRole("link", { name: "Profiles" })).toHaveAttribute("aria-current", "page")
})
