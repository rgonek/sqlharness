import { render, screen } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test, vi } from "vitest"
import { SqlBlock } from "@/components/SqlBlock"

const text = () => screen.getByTestId("sql-code").textContent

test("shows formatted SQL and toggles to the original", async () => {
  render(<SqlBlock sql="select a from t" engine="sqlserver" />)
  expect(text()).toBe("select\n  a\nfrom\n  t")
  await userEvent.click(screen.getByRole("button", { name: "Show original" }))
  expect(text()).toBe("select a from t")
  expect(screen.getByRole("button", { name: "Show formatted" })).toBeInTheDocument()
})

test("can show unmodified SQL first for replaying setup", async () => {
  render(<SqlBlock sql="select a from t" engine="sqlserver" initiallyFormatted={false} />)
  expect(text()).toBe("select a from t")
  expect(screen.getByRole("button", { name: "Show formatted" })).toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "Show formatted" }))
  expect(text()).toBe("select\n  a\nfrom\n  t")
})

test("named SQL copy preserves the original text and offers manual copy when denied", async () => {
  const sql = "select a from t"
  const writeText = vi.fn().mockRejectedValue(new Error("denied"))
  Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText } })
  render(<SqlBlock label="Setup SQL" sql={sql} engine="sqlserver" />)
  await userEvent.click(screen.getByRole("button", { name: "Copy Setup SQL" }))
  expect(writeText).toHaveBeenCalledWith(sql)
  expect(await screen.findByRole("textbox", { name: "Text to copy" })).toHaveValue(sql)
})
