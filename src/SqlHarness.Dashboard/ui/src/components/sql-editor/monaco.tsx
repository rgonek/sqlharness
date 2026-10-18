import { Editor, loader } from "@monaco-editor/react"
import * as monaco from "monaco-editor/editor/editor.api"
import EditorWorker from "monaco-editor/editor/editor.worker?worker"
import { useEffect, useRef, useState } from "react"
import type { editor as MonacoEditorApi } from "monaco-editor"
import type { PlanSqlViewerProps, SqlMergeViewProps, SqlViewerProps, SqlSourceSpan } from "@/lib/sqlDiff"

globalThis.MonacoEnvironment = {
  getWorker: () => new EditorWorker(),
}

loader.config({ monaco })

const postgresLanguage = "sqlharness-postgresql"
const tsqlLanguage = "sqlharness-tsql"

const keywords = [
  "all", "alter", "and", "as", "asc", "begin", "by", "case", "cast", "coalesce", "column", "commit",
  "create", "cross", "current_date", "current_timestamp", "database", "delete", "desc", "distinct", "drop",
  "else", "end", "except", "exists", "false", "fetch", "for", "foreign", "from", "full", "group", "having",
  "in", "index", "inner", "insert", "intersect", "into", "is", "join", "left", "like", "limit", "not", "null",
  "offset", "on", "or", "order", "outer", "primary", "references", "returning", "right", "rollback", "select",
  "set", "table", "then", "true", "union", "unique", "update", "values", "when", "where", "with",
]

function registerSqlLanguage(id: string) {
  monaco.languages.register({ id })
  monaco.languages.setMonarchTokensProvider(id, {
    ignoreCase: true,
    keywords,
    tokenizer: {
      root: [
        [/--.*$/, "comment"],
        [/\/\*/, "comment", "@comment"],
        [/'([^']|'')*'/, "string"],
        [/"([^"]|"")*"/, "identifier"],
        [/\[[^\]]*\]/, "identifier"],
        [/\b\d+(\.\d+)?([eE][+-]?\d+)?\b/, "number"],
        [/[a-zA-Z_][\w$]*/, { cases: { "@keywords": "keyword", "@default": "identifier" } }],
        [/[()]/, "delimiter.parenthesis"],
        [/[;,.]/, "delimiter"],
        [/[=<>!+*/%-]+/, "operator"],
      ],
      comment: [[/[^/*]+/, "comment"], [/\/\*/, "comment", "@push"], [/\*\//, "comment", "@pop"], [/[/*]/, "comment"]],
    },
  })
}

registerSqlLanguage(postgresLanguage)
registerSqlLanguage(tsqlLanguage)

function editorTokenColor(token: string, fallback: string): string {
  const value = getComputedStyle(document.documentElement).getPropertyValue(token).trim()
  if (!value) return fallback
  const canvas = document.createElement("canvas")
  const context = canvas.getContext("2d")
  if (!context) return fallback
  context.fillStyle = value
  context.fillRect(0, 0, 1, 1)
  const [red, green, blue] = context.getImageData(0, 0, 1, 1).data
  return `#${[red, green, blue].map(channel => channel.toString(16).padStart(2, "0")).join("")}`
}

function defineSqlTheme(dark: boolean) {
  const base = dark ? "vs-dark" : "vs"
  const suffix = dark ? "dark" : "light"
  monaco.editor.defineTheme(`sqlharness-${suffix}`, {
    base,
    inherit: true,
    rules: [
      { token: "keyword", foreground: editorTokenColor("--sql-keyword", dark ? "C4B5FD" : "6D28D9"), fontStyle: "bold" },
      { token: "string", foreground: editorTokenColor("--sql-string", dark ? "86EFAC" : "15803D") },
      { token: "number", foreground: editorTokenColor("--sql-number", dark ? "FDBA74" : "C2410C") },
      { token: "comment", foreground: editorTokenColor("--muted-foreground", dark ? "A1A1AA" : "71717A"), fontStyle: "italic" },
    ],
    colors: {
      "editor.background": editorTokenColor("--background", dark ? "252526" : "FFFFFF"),
      "editor.foreground": editorTokenColor("--foreground", dark ? "D4D4D4" : "1F1F1F"),
      "editorGutter.background": editorTokenColor("--background", dark ? "252526" : "FFFFFF"),
      "editorLineNumber.foreground": editorTokenColor("--muted-foreground", dark ? "858585" : "858585"),
      "editorLineNumber.activeForeground": editorTokenColor("--foreground", dark ? "D4D4D4" : "1F1F1F"),
    },
  })
}

defineSqlTheme(document.documentElement.classList.contains("dark"))

const editorOptions: MonacoEditorApi.IStandaloneEditorConstructionOptions = {
  readOnly: true,
  domReadOnly: true,
  minimap: { enabled: false },
  suggest: { showWords: false, showKeywords: false, showSnippets: false },
  quickSuggestions: false,
  suggestOnTriggerCharacters: false,
  parameterHints: { enabled: false },
  hover: { enabled: "off" },
  wordBasedSuggestions: "off",
  occurrencesHighlight: "off",
  selectionHighlight: false,
  codeLens: false,
  links: false,
  colorDecorators: false,
  folding: false,
  glyphMargin: false,
  lineNumbersMinChars: 3,
  overviewRulerLanes: 0,
  hideCursorInOverviewRuler: true,
  renderLineHighlight: "none",
  contextmenu: false,
  scrollBeyondLastLine: false,
  automaticLayout: true,
  padding: { top: 8, bottom: 8 },
  fontSize: 12,
  lineHeight: 19,
}

function useSqlTheme(): string {
  const [theme, setTheme] = useState(() => document.documentElement.classList.contains("dark") ? "sqlharness-dark" : "sqlharness-light")
  useEffect(() => {
    const root = document.documentElement
    const update = () => {
      const dark = root.classList.contains("dark")
      defineSqlTheme(dark)
      setTheme(dark ? "sqlharness-dark" : "sqlharness-light")
    }
    const observer = new MutationObserver(update)
    observer.observe(root, { attributes: true, attributeFilter: ["class"] })
    return () => observer.disconnect()
  }, [])
  return theme
}

function languageFor(dialect: SqlViewerProps["dialect"]): string {
  return dialect === "postgres" ? postgresLanguage : tsqlLanguage
}

export function SqlViewer({ sql, dialect }: SqlViewerProps) {
  const theme = useSqlTheme()
  return (
    <div className="h-[32rem] max-h-[32rem] overflow-hidden rounded border">
      <Editor
        height="32rem"
        language={languageFor(dialect)}
        value={sql}
        theme={theme}
        options={editorOptions}
      />
    </div>
  )
}

export function SqlMergeView({ baseline, candidate, dialect, mode }: SqlMergeViewProps) {
  const theme = useSqlTheme()
  const modeRef = useRef(mode)
  const container = useRef<HTMLDivElement>(null)
  const diffEditor = useRef<MonacoEditorApi.IStandaloneDiffEditor | null>(null)
  const originalModel = useRef<MonacoEditorApi.ITextModel | null>(null)
  const modifiedModel = useRef<MonacoEditorApi.ITextModel | null>(null)

  useEffect(() => {
    modeRef.current = mode
  }, [mode])

  useEffect(() => {
    if (!container.current) return
    const original = monaco.editor.createModel("", postgresLanguage)
    const modified = monaco.editor.createModel("", postgresLanguage)
    const editor = monaco.editor.createDiffEditor(container.current, {
      ...editorOptions,
      automaticLayout: true,
      renderSideBySide: modeRef.current === "side-by-side",
      originalEditable: false,
      diffCodeLens: false,
      enableSplitViewResizing: false,
    })
    originalModel.current = original
    modifiedModel.current = modified
    diffEditor.current = editor
    editor.setModel({ original, modified })
    return () => {
      editor.setModel(null)
      editor.dispose()
      original.dispose()
      modified.dispose()
      diffEditor.current = null
      originalModel.current = null
      modifiedModel.current = null
    }
  }, [])

  useEffect(() => {
    originalModel.current?.setValue(baseline)
    modifiedModel.current?.setValue(candidate)
  }, [baseline, candidate])

  useEffect(() => {
    const language = languageFor(dialect)
    if (originalModel.current) monaco.editor.setModelLanguage(originalModel.current, language)
    if (modifiedModel.current) monaco.editor.setModelLanguage(modifiedModel.current, language)
  }, [dialect])

  useEffect(() => {
    diffEditor.current?.updateOptions({ renderSideBySide: mode === "side-by-side" })
  }, [mode])

  useEffect(() => {
    monaco.editor.setTheme(theme)
  }, [theme])

  return (
    <div className="h-[32rem] max-h-[32rem] overflow-hidden rounded border">
      <div ref={container} className="h-[32rem] w-full" />
    </div>
  )
}

function sourceRange(model: MonacoEditorApi.ITextModel, span: SqlSourceSpan): MonacoEditorApi.IModelDeltaDecoration["range"] | null {
  const start = Math.max(0, Math.min(span.start, model.getValueLength()))
  const end = Math.max(start, Math.min(span.start + span.length, model.getValueLength()))
  if (start === end) return null
  const from = model.getPositionAt(start)
  const to = model.getPositionAt(end)
  return new monaco.Range(from.lineNumber, from.column, to.lineNumber, to.column)
}

/** Read-only executed SQL with source-map decorations; input text and offsets stay byte-for-byte as captured. */
export function PlanSqlViewer({ sql, dialect, highlightedSpans, revealSpan, onSelectionChange }: PlanSqlViewerProps) {
  const theme = useSqlTheme()
  const editorRef = useRef<MonacoEditorApi.IStandaloneCodeEditor | null>(null)
  const decorationIdsRef = useRef<string[]>([])
  const [ready, setReady] = useState(false)
  const selectionRef = useRef(onSelectionChange)

  useEffect(() => {
    selectionRef.current = onSelectionChange
  }, [onSelectionChange])

  useEffect(() => {
    if (!ready) return
    const editor = editorRef.current
    if (!editor) return
    const model = editor.getModel()
    if (!model) return
    const decorations = highlightedSpans.flatMap(span => {
      const range = sourceRange(model, span)
      return range ? [{
        range,
        options: {
          inlineClassName: "plan-source-highlight",
          overviewRuler: { color: "var(--primary)", position: monaco.editor.OverviewRulerLane.Center },
        },
      }] : []
    })
    decorationIdsRef.current = editor.deltaDecorations(decorationIdsRef.current, decorations)
    const revealRange = revealSpan ? sourceRange(model, revealSpan) : null
    if (revealRange) editor.revealRangeInCenter(revealRange, monaco.editor.ScrollType.Smooth)
  }, [highlightedSpans, ready, revealSpan, sql])

  useEffect(() => {
    if (!ready) return
    const editor = editorRef.current
    if (!editor) return
    const listener = editor.onDidChangeCursorSelection(event => {
      const model = editor.getModel()
      if (!model) return
      const start = model.getOffsetAt(event.selection.getStartPosition())
      const end = model.getOffsetAt(event.selection.getEndPosition())
      selectionRef.current(end > start ? { start, end } : null)
    })
    return () => listener.dispose()
  }, [ready])

  const onMount = (editor: MonacoEditorApi.IStandaloneCodeEditor) => {
    editorRef.current = editor
    setReady(true)
  }

  return (
    <section aria-label="Executed SQL" className="space-y-2">
      <h3 className="text-sm font-medium">Executed SQL batch</h3>
      <div className="h-[32rem] max-h-[32rem] overflow-hidden rounded border">
        <Editor
          height="32rem"
          language={languageFor(dialect)}
          value={sql}
          theme={theme}
          onMount={onMount}
          options={{ ...editorOptions, wordWrap: "off" }}
        />
      </div>
    </section>
  )
}
