import { useQuery } from "@tanstack/react-query"
import { useState } from "react"
import { fetchReproScript } from "@/api/queries"
import type { ReproScriptForm } from "@/api/types"
import { Button } from "@/components/ui/button"

const forms: { form: ReproScriptForm; text: string }[] = [
  { form: "as-executed", text: "Copy script (as executed)" },
  { form: "local-variables", text: "Copy script (local variables)" },
]

export function ReproScriptButtons({ operationId, side, caseId }: { operationId: number; side: string; caseId: string }) {
  const [warning, setWarning] = useState<string | null>(null)
  const asExecuted = useQuery({
    queryKey: ["repro-script", operationId, side, caseId, "as-executed"],
    queryFn: () => fetchReproScript(operationId, side, caseId, "as-executed"), staleTime: Infinity,
  })
  const localVariables = useQuery({
    queryKey: ["repro-script", operationId, side, caseId, "local-variables"],
    queryFn: () => fetchReproScript(operationId, side, caseId, "local-variables"), staleTime: Infinity,
  })
  const scripts = [asExecuted, localVariables]
  const incomplete = scripts.map(query => query.data).find(script => script?.completeness === "ValuesMissing")

  return <div className="flex flex-wrap items-center gap-2">
    {forms.map(({ form, text }, index) => {
      const query = scripts[index]
      const script = query.data
      const disabled = !script || (script.completeness !== "Complete" && script.completeness !== "ValuesMissing") || !script.text
      const title = script?.label ?? (query.error ? "Script not available" : "Loading reproduction script")
      return <Button key={form} type="button" variant="outline" disabled={disabled} title={title} onClick={() => {
        if (!script?.text) return
        void navigator.clipboard.writeText(script.text).then(() => setWarning(script.completeness === "ValuesMissing" ? script.label : null))
      }}>{text}</Button>
    })}
    {(warning ?? incomplete?.label) && <span role="status" className="text-sm text-amber-700 dark:text-amber-300">{warning ?? incomplete?.label}</span>}
  </div>
}
