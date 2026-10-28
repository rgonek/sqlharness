import { useState } from "react"
import { render, screen, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test } from "vitest"
import { VersionTable } from "./VersionTable"

const versions = ["v0", "v1", "v2"].map(version => ({ version, hash: version, logicalReadsSum: 10, logicalReadsMean: 10, scansSum: 1, elapsedMedianMs: 1, caseOutcome: "ok", measuredIn: [], latestOperationId: 1, verdictBasis: "whole batch" }))
function Choices({ subject }: { subject: string }) {
  const [a, setA] = useState("v0")
  const [b, setB] = useState<string | null>(null)
  return <VersionTable subject={subject} sessionId={1} sessionSearch={{}} versions={versions} selectedA={a} selectedB={b} onSelect={(side, version) => side === "a" ? setA(version) : setB(version)} />
}

test("A and B are independent exclusive native groups, isolated per table instance", async () => {
  render(<><Choices subject="First" /><Choices subject="Second" /></>)
  const first = within(screen.getByRole("table", { name: "First versions" }))
  const second = within(screen.getByRole("table", { name: "Second versions" }))
  expect(first.getByRole("radio", { name: "A v0" })).toBeChecked()
  expect(first.getByRole("radio", { name: "A v0" })).toHaveAccessibleDescription("Baseline A")
  expect(first.getByRole("radio", { name: "B v0" })).toHaveAccessibleDescription("Candidate B")
  await userEvent.click(first.getByRole("radio", { name: "A v1" }))
  first.getByRole("radio", { name: "B v2" }).focus()
  await userEvent.keyboard(" ")
  expect(first.getByRole("radio", { name: "A v1" })).toBeChecked()
  expect(first.getByRole("radio", { name: "A v0" })).not.toBeChecked()
  expect(first.getByRole("radio", { name: "B v2" })).toBeChecked()
  expect(second.getByRole("radio", { name: "A v0" })).toBeChecked()
  expect(first.getByRole("radio", { name: "A v0" }).getAttribute("name")).not.toBe(second.getByRole("radio", { name: "A v0" }).getAttribute("name"))
  expect(first.getAllByRole("radio").filter(radio => (radio as HTMLInputElement).checked)).toHaveLength(2)
})
