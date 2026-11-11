import { act, fireEvent, render, screen } from "@testing-library/react"
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
  expect(sessionStorage.getItem("sqlharness.sqlViewer.height")).toBeNull()
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

test("unrelated root classes do not recalculate editor colours", async () => {
  render(<SqlMergeView baseline="SELECT 1" candidate="SELECT 2" dialect="tsql" mode="side-by-side" />)
  const styles = vi.spyOn(window, "getComputedStyle")
  try {
    await act(async () => { document.documentElement.classList.add("theme-changing") })
    expect(styles).not.toHaveBeenCalled()
  } finally {
    styles.mockRestore()
    document.documentElement.classList.remove("theme-changing")
  }
})

test("manual SQL frame height survives a version-driven editor remount", () => {
  const first = render(<SqlMergeView baseline="SELECT 1" candidate="SELECT 2" dialect="tsql" mode="side-by-side" />)
  const frame = first.container.querySelector<HTMLElement>('[data-slot="sql-editor-frame"]')!
  fireEvent.keyDown(frame, { key: "ArrowDown", altKey: true })
  const chosen = frame.style.height
  first.unmount()
  const second = render(<SqlMergeView baseline="SELECT 3" candidate="SELECT 4" dialect="tsql" mode="side-by-side" />)
  expect(second.container.querySelector<HTMLElement>('[data-slot="sql-editor-frame"]')!.style.height).toBe(chosen)
})

test("diff gives both native input contexts meaningful accessible names", () => {
  render(<SqlMergeView baseline="SELECT 1" candidate="SELECT 2" dialect="tsql" mode="side-by-side" />)
  expect(editorMocks.createDiffEditor).toHaveBeenCalledWith(expect.anything(), expect.objectContaining({
    originalAriaLabel: "Baseline SQL", modifiedAriaLabel: "Candidate SQL",
  }))
})

test("read-only diff uses the labelled textarea accessibility path and lets Tab leave", () => {
  render(<SqlMergeView baseline="SELECT 1" candidate="SELECT 2" dialect="tsql" mode="side-by-side" />)
  expect(editorMocks.createDiffEditor).toHaveBeenCalledWith(expect.anything(), expect.objectContaining({
    editContext: false, accessibilitySupport: "on", tabFocusMode: true,
  }))
})

test("changing diff mode keeps both side labels in the options update", () => {
  const { rerender } = render(<SqlMergeView baseline="SELECT 1" candidate="SELECT 2" dialect="tsql" mode="side-by-side" />)
  const editor = editorMocks.createDiffEditor.mock.results.at(-1)!.value
  rerender(<SqlMergeView baseline="SELECT 1" candidate="SELECT 2" dialect="tsql" mode="unified" />)
  expect(editor.updateOptions).toHaveBeenLastCalledWith(expect.objectContaining({
    renderSideBySide: false, originalAriaLabel: "Baseline SQL", modifiedAriaLabel: "Candidate SQL",
  }))
})

test.each(["88", "1200"])("restores a valid bounded manual height %s", stored => {
  sessionStorage.setItem("sqlharness.sqlViewer.height", stored)
  const { container, rerender } = render(<SqlViewer sql="SELECT 1" dialect="tsql" />)
  const frame = container.querySelector<HTMLElement>('[data-slot="sql-editor-frame"]')!
  expect(frame.style.height).toBe(`${stored}px`)
  rerender(<SqlViewer sql={"SELECT 1;\n".repeat(100)} dialect="tsql" />)
  expect(frame.style.height).toBe(`${stored}px`)
})

test.each(["", " ", "NaN", "Infinity", "87", "1201", "-100", '{"height":200}'])("ignores invalid stored manual height %j", stored => {
  sessionStorage.setItem("sqlharness.sqlViewer.height", stored)
  const { container, rerender } = render(<SqlViewer sql="SELECT 1" dialect="tsql" />)
  const frame = container.querySelector<HTMLElement>('[data-slot="sql-editor-frame"]')!
  expect(frame.style.height).toBe("88px")
  rerender(<SqlViewer sql={"SELECT 1;\n".repeat(100)} dialect="tsql" />)
  expect(frame.style.height).toBe("480px")
})

test("keeps automatic content sizing until a manual resize even when storage is unavailable", () => {
  vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => { throw new Error("blocked") })
  vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => { throw new Error("blocked") })
  const { container, rerender } = render(<SqlViewer sql="SELECT 1" dialect="tsql" />)
  const frame = container.querySelector<HTMLElement>('[data-slot="sql-editor-frame"]')!
  expect(frame.style.height).toBe("88px")
  rerender(<SqlViewer sql={"SELECT 1;\n".repeat(100)} dialect="tsql" />)
  expect(frame.style.height).toBe("480px")
  fireEvent.keyDown(frame, { key: "ArrowDown", altKey: true })
  expect(frame.style.height).toBe("524px")
})

test("native drag resize is remembered across editor remounts", () => {
  vi.stubGlobal("ResizeObserver", class {
    constructor(callback: ResizeObserverCallback) { editorMocks.resizeObservers.push(callback) }
    observe() {}
    disconnect() {}
  })
  try {
    const first = render(<SqlViewer sql="SELECT 1" dialect="tsql" />)
    const frame = first.container.querySelector<HTMLElement>('[data-slot="sql-editor-frame"]')!
    vi.spyOn(frame, "getBoundingClientRect").mockReturnValue({ height: 420 } as DOMRect)
    act(() => editorMocks.resizeObservers.at(-1)!([], {} as ResizeObserver))
    expect(sessionStorage.getItem("sqlharness.sqlViewer.height")).toBe("420")
    first.unmount()
    const second = render(<SqlViewer sql="SELECT 2" dialect="tsql" />)
    expect(second.container.querySelector<HTMLElement>('[data-slot="sql-editor-frame"]')!.style.height).toBe("420px")
  } finally {
    vi.unstubAllGlobals()
  }
})
