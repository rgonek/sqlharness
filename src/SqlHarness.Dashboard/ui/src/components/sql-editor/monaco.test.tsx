import { fireEvent, render, screen } from "@testing-library/react"
import { expect, test, vi } from "vitest"
import { SqlMergeView, SqlViewer } from "./monaco"

const editorMocks = vi.hoisted(() => ({ createDiffEditor: vi.fn(() => ({
  setModel: vi.fn(), updateOptions: vi.fn(), dispose: vi.fn(),
})), resizeObservers: [] as ResizeObserverCallback[] }))
vi.mock("@monaco-editor/react", () => ({
  loader: { config: vi.fn() },
  Editor: ({ options, value }: { options: { fontSize: number }; value: string }) => <pre data-testid="editor" data-font-size={options.fontSize}>{value}</pre>,
}))
vi.mock("monaco-editor/editor/editor.worker?worker", () => ({ default: class {} }))
vi.mock("monaco-editor/editor/editor.api", () => ({
  languages: { register: vi.fn(), setMonarchTokensProvider: vi.fn() },
  editor: {
    defineTheme: vi.fn(), setTheme: vi.fn(), setModelLanguage: vi.fn(),
    createModel: () => ({ setValue: vi.fn(), dispose: vi.fn() }),
    createDiffEditor: editorMocks.createDiffEditor,
  },
}))

test("short SQL has a compact frame; large SQL grows only to a bounded initial height", () => {
  const { container, rerender } = render(<SqlViewer sql="SELECT 1" dialect="tsql" />)
  const frame = () => container.querySelector<HTMLElement>('[data-slot="sql-editor-frame"]')!
  const shortHeight = Number.parseFloat(frame().style.height)
  expect(shortHeight).toBeLessThan(160)
  expect(screen.getByTestId("editor")).toHaveAttribute("data-font-size", "14")
  rerender(<SqlViewer sql={"SELECT 1;\n".repeat(4000)} dialect="tsql" />)
  expect(Number.parseFloat(frame().style.height)).toBeGreaterThan(shortHeight)
  expect(Number.parseFloat(frame().style.height)).toBeLessThanOrEqual(480)
})

test("SQL frame can be resized with a keyboard and preserves that choice after content changes", () => {
  const { container, rerender } = render(<SqlViewer sql="SELECT 1" dialect="tsql" />)
  const frame = container.querySelector<HTMLElement>('[data-slot="sql-editor-frame"]')!
  const original = Number.parseFloat(frame.style.height)
  fireEvent.keyDown(frame, { key: "ArrowDown", altKey: true })
  expect(Number.parseFloat(frame.style.height)).toBeGreaterThan(original)
  const resized = frame.style.height
  rerender(<SqlViewer sql={"SELECT 2;\n".repeat(60)} dialect="tsql" />)
  expect(frame.style.height).toBe(resized)
})

test("diff frame follows content density and uses readable editor text", () => {
  const { container } = render(<SqlMergeView baseline="SELECT 1" candidate="SELECT 2" dialect="tsql" mode="side-by-side" />)
  expect(Number.parseFloat(container.querySelector<HTMLElement>('[data-slot="sql-editor-frame"]')!.style.height)).toBeLessThan(160)
  expect(editorMocks.createDiffEditor).toHaveBeenCalledWith(expect.anything(), expect.objectContaining({ fontSize: 14 }))
})
