import { render, screen, within } from "@testing-library/react"
import { expect, test } from "vitest"
import { ConditionsList } from "./ConditionsList"
import { conditions } from "@/test/fixtures"

test("renders setup, fixed values, and varying values by case without raw field names", () => {
  render(<ConditionsList conditions={conditions({
    setup: { hash: "setup-hash", text: "CREATE TABLE #t (id int)" },
    fixed: [{ name: "@tenant", effectiveType: "int", value: 7 }],
    cases: [{ id: "0", index: 0, label: "@kind = N'gold'", setName: null, varying: [{ name: "@kind", effectiveType: "nvarchar(20)", value: "gold" }], fingerprint: "case-fingerprint" }],
  })} />)

  expect(screen.getByText("CREATE TABLE #t (id int)")).toBeInTheDocument()
  expect(screen.getByText("@tenant")).toBeInTheDocument()
  expect(screen.getByText("7")).toBeInTheDocument()
  expect(screen.getByText("(int)")).toBeInTheDocument()
  const caseSection = screen.getByRole("group", { name: "@kind = N'gold'" })
  expect(within(caseSection).getByText("@kind")).toBeInTheDocument()
  expect(caseSection.querySelector("dl dd")?.textContent).toContain('"gold" (nvarchar(20))')
  expect(document.body).not.toHaveTextContent("typedParameters")
  expect(document.body).not.toHaveTextContent("matrixText")
})

test("omits empty condition fields", () => {
  render(<ConditionsList conditions={conditions()} />)
  expect(screen.queryByText("Setup SQL")).not.toBeInTheDocument()
  expect(screen.queryByText("Fixed parameters")).not.toBeInTheDocument()
  expect(screen.queryByText("Cases")).not.toBeInTheDocument()
})
