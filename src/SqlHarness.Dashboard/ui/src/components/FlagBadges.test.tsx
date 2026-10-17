import { render, screen } from "@testing-library/react"
import { expect, test } from "vitest"
import { FlagBadges } from "@/components/FlagBadges"
import { operation } from "@/test/fixtures"

test("informational operation flags are muted text rather than badges", () => {
  render(<FlagBadges operation={operation({ hasSpill: true, coldCache: true, overGranted: true })} />)
  for (const label of ["spill", "cold cache", "over-granted"]) {
    const item = screen.getByText(label)
    expect(item).toHaveClass("text-muted-foreground")
    expect(item.closest("[data-slot='badge']")).toBeNull()
  }
})
