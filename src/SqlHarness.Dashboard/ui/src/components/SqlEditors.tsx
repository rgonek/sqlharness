import { lazy, Suspense } from "react"
import type { SqlMergeViewProps, SqlViewerProps } from "@/lib/sqlDiff"
import { unifiedDiffText } from "@/lib/unifiedDiff"

const MonacoSqlViewer = lazy(() => import("@/components/sql-editor/monaco").then(module => ({ default: module.SqlViewer })))
const MonacoSqlMergeView = lazy(() => import("@/components/sql-editor/monaco").then(module => ({ default: module.SqlMergeView })))

function EditorLoading() {
  return <div className="flex h-24 items-center justify-center rounded border text-sm text-muted-foreground">Loading SQL viewer…</div>
}

export function LazySqlViewer(props: SqlViewerProps) {
  return <Suspense fallback={<EditorLoading />}><MonacoSqlViewer {...props} /></Suspense>
}

export function LazySqlMergeView(props: SqlMergeViewProps) {
  return <Suspense fallback={<EditorLoading />}><MonacoSqlMergeView {...props} /></Suspense>
}

// oxlint-disable-next-line react/only-export-components
export async function loadUnifiedDiffText(): Promise<typeof unifiedDiffText> {
  return unifiedDiffText
}
