import { render, screen } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test } from "vitest"
import { SqlBlock } from "@/components/SqlBlock"

const text = () => screen.getByTestId("sql-code").textContent

test("shows formatted SQL and toggles to the original", async () => {
  render(<SqlBlock sql="select a from t" engine="sqlserver" />)
  expect(text()).toBe("select\n  a\nfrom\n  t")
  await userEvent.click(screen.getByRole("button", { name: "Show original" }))
  expect(text()).toBe("select a from t")
  expect(screen.getByRole("button", { name: "Show formatted" })).toBeInTheDocument()
})
