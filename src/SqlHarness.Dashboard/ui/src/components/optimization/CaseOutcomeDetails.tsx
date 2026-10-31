import type { CaseOutcome, OutcomeCounts } from "@/api/types"
import { TableCell } from "@/components/ui/table"
import { Button } from "@/components/ui/button"
import { Dialog, DialogContent, DialogTitle, DialogTrigger } from "@/components/ui/dialog"

function pair(a: number | null, b: number | null) {
  const delta = a === null || b === null || a === 0 ? "—" : `${Math.round((b - a) * 100 / a)}%`
  return `${a ?? "—"} → ${b ?? "—"} (${delta})`
}
export function CaseOutcomeDetails({ cases, counts, selectedCase = "all", columns = false }: { cases: CaseOutcome[]; counts: OutcomeCounts; selectedCase?: string; columns?: boolean }) {
  const selected = selectedCase === "all" ? cases : cases.filter(c => c.id === selectedCase)
  const { better, same, worse, unavailable } = counts.io
  const { pass, fail, unavailable: missing } = counts.policy
  const { equivalent, different, unavailable: resultUnavailable } = counts.results
  const sorted = selected.map((item, index) => ({ item, index })).sort((a, b) => Number(b.item.policyVerdict === "fail") - Number(a.item.policyVerdict === "fail") || Number(b.item.ioOutcome === "worse") - Number(a.item.ioOutcome === "worse") || a.index - b.index)
  const ioLabel = `IO: ${better} better, ${same} same, ${worse} worse, ${unavailable} unavailable`
  const policyLabel = `Policy: ${pass} pass, ${fail} fail, ${missing} unavailable`
  const result = <span title={`${equivalent} equivalent, ${different} differ, ${resultUnavailable} unavailable`}>Results {equivalent}/{counts.total}{different > 0 && ` · ${different} different`}{resultUnavailable > 0 && ` · ${resultUnavailable} unavailable`}</span>
  const io = <DialogTrigger render={<Button variant="outline" size="sm" />} aria-label={ioLabel} title={`${ioLabel}; ↑ means fewer reads`}>IO {better > 0 && `↑${better} `}{same > 0 && `=${same} `}{worse > 0 && `↓${worse} `}{unavailable > 0 && `?${unavailable}`}</DialogTrigger>
  const policy = <DialogTrigger render={<Button variant="outline" size="sm" />} aria-label={policyLabel} title={policyLabel}>Policy {pass > 0 && `✓${pass} `}{fail > 0 && `⚠${fail} `}{missing > 0 && `?${missing}`}</DialogTrigger>
  return <Dialog>{columns ? <><TableCell>{result}</TableCell><TableCell>{io}</TableCell><TableCell>{policy}</TableCell></> : <div className="flex flex-wrap gap-1">{result}{io}{policy}</div>}
    <DialogContent className="sm:max-w-5xl max-h-[90vh] overflow-auto"><DialogTitle>Case outcome details</DialogTitle>
      <div className="overflow-x-auto"><table className="w-full text-left"><thead><tr><th>Case</th><th>Results</th><th>Reads before → after (delta)</th><th>Elapsed ms before → after (delta)</th><th>CPU ms before → after (delta)</th><th>Policy and recorded reasons</th></tr></thead><tbody>
      {sorted.map(({ item }) => <tr key={item.id}><th scope="row" className="whitespace-normal break-all max-w-52">{item.label}</th><td>{item.resultsEquivalent === null ? "unavailable" : item.resultsEquivalent ? "equivalent" : "different"}</td><td>{pair(item.readsBefore, item.readsAfter)}</td><td>{pair(item.elapsedBeforeMs, item.elapsedAfterMs)}</td><td>{pair(item.cpuBeforeMs, item.cpuAfterMs)}</td><td>{item.policyVerdict ?? `unavailable: ${item.policyUnavailableReason ?? "not recorded"}`}{item.policyReasons.length > 0 && `: ${item.policyReasons.join(", ")}`}</td></tr>)}
      </tbody></table></div></DialogContent></Dialog>
}
