import { useState } from "react"
import { render, screen } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test } from "vitest"
import { SegmentedRadioGroup } from "./SegmentedRadioGroup"

function Modes() {
  const [value, setValue] = useState("side")
  return <SegmentedRadioGroup label="Display mode" value={value} onValueChange={setValue} options={[
    { value: "side", label: "Side by side" },
    { value: "unified", label: "Unified" },
  ]} />
}

test("a display mode stays selected and arrow keys select its alternative", async () => {
  const user = userEvent.setup()
  render(<Modes />)
  const side = screen.getByRole("radio", { name: "Side by side" })
  const unified = screen.getByRole("radio", { name: "Unified" })
  side.focus()
  await user.keyboard("{ArrowRight}")
  expect(unified).toBeChecked()
  expect(unified).toHaveFocus()
  expect(side).not.toBeChecked()
  await user.click(unified)
  await user.keyboard(" ")
  expect(unified).toBeChecked()
  await user.keyboard("{ArrowLeft}")
  expect(side).toBeChecked()
  expect(side).toHaveFocus()
})
