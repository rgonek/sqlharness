import type { HiddenWork } from "@/api/types"

export function HiddenWorkFlag({ item }: { item: HiddenWork }) {
  if (item.kind === "scalar-udf" && item.sharePercent !== null)
    return <span className="rounded border px-2 py-1 text-xs">UDF time {item.sharePercent}% of statement</span>
  if (item.kind === "multi-statement-tvf" && item.executions !== null)
    return <span className="rounded border px-2 py-1 text-xs">multi-statement function: internal reads not visible ({item.executions} executions)</span>
  if (item.kind === "scalar-udf") return <span className="rounded border px-2 py-1 text-xs">UDF time unavailable</span>
  if (item.kind === "multi-statement-tvf") return <span className="rounded border px-2 py-1 text-xs">multi-statement function: internal reads not visible</span>
  return null
}
