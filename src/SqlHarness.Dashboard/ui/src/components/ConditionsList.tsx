import type { ConditionsModel, ConditionsParameter } from "@/api/types"
import { displayCaseLabel } from "@/lib/caseLabel"

function parameterValue(parameter: ConditionsParameter): string {
  return parameter.value === null || parameter.value === undefined ? "(value not stored)" : JSON.stringify(parameter.value) ?? String(parameter.value)
}

function Parameters({ values }: { values: ConditionsParameter[] }) {
  const present = values.filter(parameter => parameter.name !== "")
  if (present.length === 0) return null
  return <dl className="grid gap-x-4 gap-y-1 sm:grid-cols-[max-content_1fr]">
    {present.map(parameter => <div key={parameter.name} className="contents">
      <dt className="font-mono text-sm">{parameter.name}</dt><dd className="break-words font-mono text-sm">{parameterValue(parameter)} <span className="text-muted-foreground">({parameter.effectiveType})</span></dd>
    </div>)}
  </dl>
}

export function ConditionsList({ conditions }: { conditions: ConditionsModel | null }) {
  if (!conditions) return <p className="text-sm text-muted-foreground">Historical measurement conditions are unavailable.</p>
  const setup = conditions.setup?.text
  const cases = conditions.cases.filter(item => item.varying.length > 0)
  return <div className="space-y-4">
    {typeof setup === "string" && setup.trim() !== "" && <section>
      <dl className="grid gap-x-4 gap-y-1 sm:grid-cols-[max-content_1fr]"><dt className="font-medium">Setup SQL</dt><dd><pre className="max-h-64 overflow-auto whitespace-pre-wrap break-words rounded border p-2 font-mono text-xs">{setup}</pre></dd></dl>
    </section>}
    {conditions.fixed.length > 0 && <section>
      <h3 className="mb-1 font-medium">Fixed parameters</h3>
      <Parameters values={conditions.fixed} />
    </section>}
    {cases.map(item => {
      const label = conditions.cases.length === 1 && item.label === "Case 1 of 1" ? null : item.label
      return <section key={item.id} role={label ? "group" : undefined} aria-label={label ?? undefined}>
      {label && <h3 className="mb-1 font-medium" title={label}>{displayCaseLabel(label)}</h3>}
      <Parameters values={item.varying} />
    </section>})}
    <dl className="grid gap-1 text-sm sm:grid-cols-[max-content_1fr]">
      <dt className="text-muted-foreground">Timeout</dt><dd>{conditions.execution.timeoutSeconds} seconds</dd>
      <dt className="text-muted-foreground">Repeat</dt><dd>{conditions.execution.repeat}</dd>
      {conditions.execution.comparisonMode && <><dt className="text-muted-foreground">Comparison mode</dt><dd>{conditions.execution.comparisonMode}</dd></>}
    </dl>
  </div>
}
