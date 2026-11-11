import type { CaseOutcomeDetail, OutcomeCounts } from "@/api/types"
import { Button } from "@/components/ui/button"
import { Dialog, DialogContent, DialogDescription, DialogHeader, DialogTitle, DialogTrigger } from "@/components/ui/dialog"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"

const metric = (value: number | null) => value == null ? "—" : value.toLocaleString("en-US")
function pair(before: number | null, after: number | null, unit = "") {
  const change = before == null || after == null ? "unavailable" : before === 0 ? after === 0 ? "0%" : "baseline zero" : `${after > before ? "+" : ""}${Math.round((after - before) * 100 / before)}%`
  return <><span>{metric(before)} → {metric(after)}{unit}</span><span className="block text-xs text-muted-foreground">{change}</span></>
}
export function CaseOutcomeDetails({ cases }: { cases: CaseOutcomeDetail[] }) {
  const rank = (row: CaseOutcomeDetail) => row.policyVerdict === "fail" ? 0 : row.ioOutcome === "worse" ? 1 : 2
  const sorted = [...cases].sort((a, b) => rank(a) - rank(b))
  return <Table aria-label="Case outcome details"><TableHeader><TableRow><TableHead>Case</TableHead><TableHead>Results</TableHead><TableHead>Logical reads</TableHead><TableHead>Elapsed</TableHead><TableHead>CPU</TableHead><TableHead>Recorded policy</TableHead></TableRow></TableHeader><TableBody>{sorted.map(row => <TableRow key={row.caseKey}>
    <TableHead scope="row" className="max-w-64 whitespace-normal break-words">{row.label}</TableHead><TableCell>{row.resultsEquivalent == null ? "Unavailable" : row.resultsEquivalent ? "Equivalent" : "Different"}</TableCell>
    <TableCell className="tabular-nums">{pair(row.readsBefore, row.readsAfter)}</TableCell><TableCell className="tabular-nums">{pair(row.elapsedBeforeMs, row.elapsedAfterMs, " ms")}</TableCell><TableCell className="tabular-nums">{pair(row.cpuBeforeMs, row.cpuAfterMs, " ms")}</TableCell>
    <TableCell className="max-w-80 whitespace-normal break-words"><span>{row.policyVerdict === "pass" ? "✓ Pass" : row.policyVerdict === "fail" ? "⚠ Fail" : "Unavailable"}</span>{row.policyReasons.map((reason, index) => <p key={index}>{reason}</p>)}{!row.policyVerdict && <p className="text-muted-foreground">{row.policyUnavailableReason ?? "No recorded policy verdict."}</p>}</TableCell>
  </TableRow>)}</TableBody></Table>
}
export function OutcomeDimensions({ outcomes, cases = [], baseline = false, coverageOnly = false, coverage, dimension }: { outcomes?: OutcomeCounts | null; cases?: CaseOutcomeDetail[]; baseline?: boolean; coverageOnly?: boolean; coverage?: { available: number; total: number }; dimension?: "results" | "io" | "policy" }) {
  const total = outcomes ? outcomes.resultsEquivalent + outcomes.resultsDifferent + outcomes.resultsUnavailable : 0
  const results = outcomes ? `Results ${outcomes.resultsEquivalent}/${total}` : "Results unavailable"
  const resultsName = outcomes ? `Results: ${outcomes.resultsEquivalent} equivalent, ${outcomes.resultsDifferent} different, ${outcomes.resultsUnavailable} unavailable` : results
  const io = !outcomes || baseline || coverageOnly ? `IO ${coverage ? `${coverage.available}/${coverage.total} measured` : "coverage unavailable"}` : `IO ${[[outcomes.ioBetter, "↑"], [outcomes.ioSame, "="], [outcomes.ioWorse, "↓"], [outcomes.ioUnavailable, "?"]].filter(([count]) => count).map(([count, symbol]) => `${symbol}${count}`).join(" · ") || "unavailable"}`
  const ioName = outcomes && !baseline && !coverageOnly ? `IO: ${outcomes.ioBetter} better, ${outcomes.ioSame} same, ${outcomes.ioWorse} worse, ${outcomes.ioUnavailable} unavailable. ↑ means improvement.` : "IO: measurement coverage; select a candidate pair for outcomes"
  const policy = !outcomes || baseline || coverageOnly ? "Policy unavailable" : `Policy ${[[outcomes.policyPass, "✓"], [outcomes.policyFail, "⚠"], [outcomes.policyUnavailable, "?"]].filter(([count]) => count).map(([count, symbol]) => `${symbol}${count}`).join(" · ") || "unavailable"}`
  const policyName = outcomes && !baseline && !coverageOnly ? `Policy: ${outcomes.policyPass} pass, ${outcomes.policyFail} fail, ${outcomes.policyUnavailable} unavailable` : "Policy unavailable without an active comparison pair"
  const action = (text: string, name: string) => cases.length ? <Dialog><DialogTrigger render={<Button variant="ghost" size="sm" className="h-auto whitespace-normal px-1 py-1" />} aria-label={name}>{text}</DialogTrigger><DialogContent className="max-h-[85vh] overflow-auto sm:max-w-5xl"><DialogHeader><DialogTitle>Case outcomes</DialogTitle><DialogDescription>Results, observed IO and recorded policy are independent. ↑ means fewer reads; policy uses the original recorded direction.</DialogDescription></DialogHeader><CaseOutcomeDetails cases={cases} /></DialogContent></Dialog> : <span title={name} aria-label={name}>{text}</span>
  return <div className="flex flex-wrap items-center gap-x-2 gap-y-1 text-sm">{(!dimension || dimension === "results") && <span title={resultsName} aria-label={resultsName}>{results}</span>}{(!dimension || dimension === "io") && action(io, ioName)}{(!dimension || dimension === "policy") && action(policy, policyName)}</div>
}
