import { act, cleanup, render, screen, waitFor } from "@testing-library/react"
import { afterEach, expect, test, vi } from "vitest"
import type { PlanSqlViewerProps } from "@/lib/sqlDiff"

type CursorEvent = {
  selection: {
    getStartPosition: () => { lineNumber: number; column: number }
    getEndPosition: () => { lineNumber: number; column: number }
  }
}
type CursorListener = (event: CursorEvent) => void

const mocks = vi.hoisted(() => {
  let selectionListener: CursorListener | null = null
  const model = {
    getValueLength: vi.fn(() => 100),
    getPositionAt: vi.fn((offset: number) => ({ lineNumber: 1, column: offset + 1 })),
    getOffsetAt: vi.fn((position: { lineNumber: number; column: number }) => position.column - 1),
  }
  const dispose = vi.fn()
  const editor = {
    getModel: vi.fn(() => model),
    deltaDecorations: vi.fn(() => ["decoration-1"]),
    revealRangeInCenter: vi.fn(),
    onDidChangeCursorSelection: vi.fn((listener: CursorListener) => {
      selectionListener = listener
      return { dispose }
    }),
  }

  return {
    editor,
    model,
    dispose,
    emitSelection: (startColumn: number, endColumn: number) => selectionListener?.({
      selection: {
        getStartPosition: () => ({ lineNumber: 1, column: startColumn }),
        getEndPosition: () => ({ lineNumber: 1, column: endColumn }),
      },
    }),
  }
})

vi.mock("@monaco-editor/react", async () => {
  const React = await import("react")
  const EditorStub = ({ onMount, value }: { onMount: (editor: typeof mocks.editor) => void; value: string }) => {
    const onMountRef = React.useRef(onMount)
    React.useEffect(() => { onMountRef.current(mocks.editor) }, [])
    return React.createElement("textarea", { "aria-label": "Monaco mock", readOnly: true, value })
  }
  return {
    Editor: EditorStub,
    DiffEditor: () => null,
    loader: { config: vi.fn() },
  }
})

vi.mock("monaco-editor/editor/editor.api", () => ({
  Range: class Range {
    readonly startLineNumber: number
    readonly startColumn: number
    readonly endLineNumber: number
    readonly endColumn: number

    constructor(
      startLineNumber: number,
      startColumn: number,
      endLineNumber: number,
      endColumn: number,
    ) {
      this.startLineNumber = startLineNumber
      this.startColumn = startColumn
      this.endLineNumber = endLineNumber
      this.endColumn = endColumn
    }
  },
  editor: {
    OverviewRulerLane: { Center: 2 },
    ScrollType: { Smooth: 0 },
    defineTheme: vi.fn(),
  },
  languages: { register: vi.fn(), setMonarchTokensProvider: vi.fn() },
}))

vi.mock("monaco-editor/editor/editor.worker?worker", () => ({ default: class WorkerStub {} }))

afterEach(() => cleanup())

test("PlanSqlViewer applies source spans and keeps the latest selection callback until unmount", async () => {
  const firstSelection = vi.fn()
  const latestSelection = vi.fn()
  const spans = [{ start: 2, length: 4 }]
  const revealSpan = { start: 7, length: 3 }
  const props: PlanSqlViewerProps = {
    sql: "SELECT id FROM dbo.Orders",
    dialect: "tsql",
    highlightedSpans: spans,
    revealSpan,
    onSelectionChange: firstSelection,
  }
  const { PlanSqlViewer } = await import("./monaco")
  const view = render(<PlanSqlViewer {...props} />)

  expect(screen.getByRole("textbox", { name: "Monaco mock" })).toHaveValue(props.sql)
  await waitFor(() => expect(mocks.editor.onDidChangeCursorSelection).toHaveBeenCalledOnce())
  expect(mocks.editor.deltaDecorations).toHaveBeenCalledWith([], [{
    range: expect.objectContaining({ startLineNumber: 1, startColumn: 3, endLineNumber: 1, endColumn: 7 }),
    options: expect.objectContaining({ inlineClassName: "plan-source-highlight" }),
  }])
  expect(mocks.editor.revealRangeInCenter).toHaveBeenCalledWith(
    expect.objectContaining({ startLineNumber: 1, startColumn: 8, endLineNumber: 1, endColumn: 11 }),
    0,
  )

  view.rerender(<PlanSqlViewer {...props} onSelectionChange={latestSelection} />)
  await act(async () => mocks.emitSelection(3, 7))
  expect(firstSelection).not.toHaveBeenCalled()
  expect(latestSelection).toHaveBeenCalledWith({ start: 2, end: 6 })

  view.unmount()
  expect(mocks.dispose).toHaveBeenCalledOnce()
})
