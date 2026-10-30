import type { CaseOutcome } from "@/api/types"
import { Button } from "@/components/ui/button"
import { Dialog, DialogContent, DialogTitle, DialogTrigger } from "@/components/ui/dialog"

function pair(a: number | null, b: number | null) {
  const delta = a === null || b === null || a === 0 ? "—" : `${Math.round((b - a) * 100 / a)}%`
  return `${a ?? "—"} → ${b ?? "—"} (${delta})`
}
export function CaseOutcomeDetails({ cases, selectedCase = "all" }: { cases: CaseOutcome[]; selectedCase?: string }) {
  const selected = selectedCase === "all" ? cases : cases.filter(c => c.id === selectedCase)
  const count = (predicate: (item: CaseOutcome) => boolean) => selected.filter(predicate).length
  const better = count(c => c.ioOutcome === "better"), same = count(c => c.ioOutcome === "same"), worse = count(c => c.ioOutcome === "worse"), unavailable = count(c => c.ioOutcome === "unavailable")
  const pass = count(c => c.policyVerdict === "pass"), fail = count(c => c.policyVerdict === "fail"), missing = selected.length - pass - fail
  const equivalent = count(c => c.resultsEquivalent === true), different = count(c => c.resultsEquivalent === false)
  const sorted = selected.map((item, index) => ({ item, index })).sort((a, b) => Number(b.item.policyVerdict === "fail") - Number(a.item.policyVerdict === "fail") || Number(b.item.ioOutcome === "worse") - Number(a.item.ioOutcome === "worse") || a.index - b.index)
  const ioLabel = `IO: ${better} better, ${same} same, ${worse} worse, ${unavailable} unavailable`
  const policyLabel = `Policy: ${pass} pass, ${fail} fail, ${missing} unavailable`
  return <Dialog><div className="flex flex-wrap gap-1"><span title={`${equivalent} equivalent, ${different} differ, ${selected.length - equivalent - different} unavailable`}>Results {equivalent}/{selected.length}</span>
    <DialogTrigger render={<Button variant="outline" size="sm" />} aria-label={ioLabel} title={`${ioLabel}; ↑ means fewer reads`}>IO {better > 0 && `↑${better} `}{same > 0 && `=${same} `}{worse > 0 && `↓${worse} `}{unavailable > 0 && `?${unavailable}`}</DialogTrigger>
    <DialogTrigger render={<Button variant="outline" size="sm" />} aria-label={policyLabel} title={policyLabel}>Policy {pass > 0 && `✓${pass} `}{fail > 0 && `⚠${fail} `}{missing > 0 && `?${missing}`}</DialogTrigger></div>
    <DialogContent className="sm:max-w-5xl max-h-[90vh] overflow-auto"><DialogTitle>Case outcome details</DialogTitle>
      <div className="overflow-x-auto"><table className="w-full text-left"><thead><tr><th>Case</th><th>Results</th><th>Reads before → after (delta)</th><th>Elapsed ms before → after (delta)</th><th>CPU ms before → after (delta)</th><th>Policy and recorded reasons</th></tr></thead><tbody>
      {sorted.map(({ item }) => <tr key={item.id}><th scope="row" className="whitespace-normal break-all max-w-52">{item.label}</th><td>{item.resultsEquivalent === null ? "unavailable" : item.resultsEquivalent ? "equivalent" : "different"}</td><td>{pair(item.readsBefore, item.readsAfter)}</td><td>{pair(item.elapsedBeforeMs, item.elapsedAfterMs)}</td><td>{pair(item.cpuBeforeMs, item.cpuAfterMs)}</td><td>{item.policyVerdict ?? `unavailable: ${item.policyUnavailableReason ?? "not recorded"}`}{item.policyReasons.length > 0 && `: ${item.policyReasons.join(", ")}`}</td></tr>)}
      </tbody></table></div></DialogContent></Dialog>
}
