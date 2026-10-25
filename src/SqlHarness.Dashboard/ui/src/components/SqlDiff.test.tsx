import { cleanup, render, screen, waitFor } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { afterEach, expect, test, vi } from "vitest"
import { SqlDiff } from "./SqlDiff"

vi.mock("@/components/SqlEditors", () => ({
  LazySqlViewer: ({ sql }: { sql: string }) => <pre aria-label="SQL viewer">{sql}</pre>,
  LazySqlMergeView: ({ baseline, candidate, mode }: { baseline: string; candidate: string; mode: string }) =>
    <pre aria-label={`merge ${mode}`}>{baseline}|{candidate}</pre>,
  loadUnifiedDiffText: async () => (await import("@/lib/unifiedDiff")).unifiedDiffText,
}))

afterEach(() => {
  cleanup()
  localStorage.clear()
})

function setClipboard(writeText: ReturnType<typeof vi.fn>) {
  Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText } })
}

test("tabs show each side and the diff; mode and ignore formatting are remembered", async () => {
  render(<SqlDiff baseline="select a from t" candidate="select a from t where x = 1" engine="sqlserver" />)
  expect(screen.getByRole("tab", { name: "Diff" })).toHaveAttribute("aria-selected", "true")
  expect(await screen.findByLabelText("merge side-by-side")).toBeInTheDocument()
  await userEvent.click(screen.getByRole("radio", { name: "Unified" }))
  expect(await screen.findByLabelText("merge unified")).toBeInTheDocument()
  expect(localStorage.getItem("sqlharness.sqlDiff.mode")).toBe("unified")
  await userEvent.click(screen.getByText("Ignore formatting"))
  expect(screen.getByRole("checkbox", { name: "Ignore formatting" })).toBeChecked()
  expect((await screen.findByLabelText("merge unified")).textContent).toContain("\n")
  await userEvent.click(screen.getByRole("tab", { name: "Baseline" }))
  expect(screen.getByLabelText("SQL viewer")).toHaveTextContent("select a from t")
})

test("diff radio groups stay independent when multiple comparisons are rendered", async () => {
  render(<>
    <SqlDiff baseline="SELECT 1" candidate="SELECT 2" engine={null} />
    <SqlDiff baseline="SELECT 3" candidate="SELECT 4" engine={null} />
  </>)
  const [firstSideBySide, secondSideBySide] = screen.getAllByRole("radio", { name: "Side by side" })
  const [firstUnified, secondUnified] = screen.getAllByRole("radio", { name: "Unified" })

  firstSideBySide.focus()
  await userEvent.keyboard("{ArrowRight}")

  expect(firstUnified).toBeChecked()
  expect(secondSideBySide).toBeChecked()
  expect(secondUnified).not.toBeChecked()
  expect(localStorage.getItem("sqlharness.sqlDiff.mode")).toBe("unified")
})

test("without stored text there is no diff tab", () => {
  render(<SqlDiff baseline={null} candidate="SELECT 1" engine={null} />)
  expect(screen.getByText(/journal\.storeSensitive is off/)).toBeInTheDocument()
  expect(screen.queryByRole("tab", { name: "Diff" })).not.toBeInTheDocument()
})

test("over the limit the diff is replaced by a message but sides stay viewable", async () => {
  render(<SqlDiff baseline={"a\n".repeat(2500)} candidate={"b\n".repeat(2500)} engine={null} />)
  expect(screen.getByText(/exceeds the 256 KiB or 4,000 line limit/)).toBeInTheDocument()
  await userEvent.click(screen.getByRole("tab", { name: "Candidate" }))
  expect(screen.getByLabelText("SQL viewer")).toBeInTheDocument()
})

test("copying the diff gives a unified text diff", async () => {
  const writeText = vi.fn().mockResolvedValue(undefined)
  setClipboard(writeText)
  render(<SqlDiff baseline="SELECT 1" candidate="SELECT 2" engine={null} />)
  await userEvent.click(screen.getByRole("button", { name: "Copy diff" }))
  await waitFor(() => expect(writeText).toHaveBeenCalledWith("@@ -1,1 +1,1 @@\n-SELECT 1\n+SELECT 2"))
})

test("formatted expansion over the limit never reaches the merge editor", async () => {
  const baseline = `SELECT ${Array.from({ length: 5000 }, (_, index) => `column_${index}`).join(", ")}`
  render(<SqlDiff baseline={baseline} candidate={`${baseline} WHERE id = 1`} engine="postgres" />)
  await userEvent.click(screen.getByText("Ignore formatting"))
  expect(await screen.findByText(/exceeds the 256 KiB or 4,000 line limit/)).toBeInTheDocument()
  expect(screen.queryByLabelText(/merge /)).not.toBeInTheDocument()
})


test("embedded over-limit diff keeps full candidate SQL copyable", async () => {
  const candidate = "SELECT 2;\n".repeat(5000)
  const writeText = vi.fn().mockResolvedValue(undefined)
  setClipboard(writeText)
  render(<SqlDiff embedded baseline="SELECT 1;" candidate={candidate} engine={null} />)
  expect(screen.getByText(/exceeds the 256 KiB or 4,000 line limit/)).toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "Copy candidate SQL" }))
  await waitFor(() => expect(writeText).toHaveBeenCalledWith(candidate))
})
