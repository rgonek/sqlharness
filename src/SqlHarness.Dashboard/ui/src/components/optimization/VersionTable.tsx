import { Sparkles } from "lucide-react"
import type { AgentSessionKey, OperationSummary, OptimizationVersion } from "@/api/types"
import { Link } from "@tanstack/react-router"
import { operationReturnSearch } from "@/lib/sessionSearch"
import type { SessionSearch } from "@/router"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"

function metric(value: number | null): string {
  return value === null ? "—" : value.toLocaleString()
}

export function VersionTable({ subject, sessionId, agentSessionKey, sessionSearch, versions, selectedA, selectedB, onSelect, operations = [] }: {
  operations?: OperationSummary[]; subject: string; sessionId: number; agentSessionKey?: AgentSessionKey; sessionSearch: SessionSearch; versions: OptimizationVersion[]; selectedA: string; selectedB: string | null; onSelect: (side: "a" | "b", version: string) => void
}) {
  const baseline = versions.find(version => version.version === selectedA)
  const delta = (value: number | null, reference: number | null | undefined) => value === null || reference === null || reference === undefined || reference === 0 ? null : `${value >= reference ? "+" : ""}${Math.round((value - reference) * 100 / reference)}% vs A`
  return <Table aria-label={`${subject} versions`}>
    <TableHeader><TableRow><TableHead>Version</TableHead><TableHead>Logical reads (sum)</TableHead><TableHead>Scans</TableHead><TableHead>Elapsed median (ms)</TableHead><TableHead>Outcome</TableHead><TableHead>Observed in</TableHead><TableHead>A</TableHead><TableHead>B</TableHead></TableRow></TableHeader>
    <TableBody>{versions.map(version => <TableRow key={`${version.version}:${version.hash}`} className={selectedA === version.version || selectedB === version.version ? "bg-muted/40" : undefined}>
      <TableHead scope="row" className="whitespace-normal"><div className="flex items-center gap-2">{version.version}{operations.some(operation => version.measuredIn.includes(operation.id) && operation.agentKeep) && <Sparkles className="size-3.5 text-violet-600" aria-label="Kept by agent" />}</div>{operations.filter(operation => version.measuredIn.includes(operation.id) && operation.agentLabel).map(operation => <div key={operation.id} className="mt-1 text-xs font-normal text-muted-foreground" title={`Agent label for operation #${operation.id}`}>{operation.agentLabel}</div>)}</TableHead>
      <TableCell className="tabular-nums">{metric(version.logicalReadsSum)}<div className="text-xs text-muted-foreground">{delta(version.logicalReadsSum, baseline?.logicalReadsSum)}</div></TableCell><TableCell>{metric(version.scansSum)}</TableCell>
      <TableCell className="tabular-nums">{metric(version.elapsedMedianMs)}<div className="text-xs text-muted-foreground">{delta(version.elapsedMedianMs, baseline?.elapsedMedianMs)}</div></TableCell><TableCell className="whitespace-normal"><span>{version.caseOutcome}</span><span className="ml-1 text-xs text-muted-foreground" title={`Verdict basis: ${version.verdictBasis}`}>basis: {version.verdictBasis}</span></TableCell>
      <TableCell className="whitespace-normal"><div className="flex flex-wrap gap-x-2">{version.measuredIn.map(operationId => <Link key={operationId} to="/operations/$id" params={{ id: String(operationId) }} search={operationReturnSearch(sessionId, sessionSearch, agentSessionKey)} className="underline">Operation #{operationId}</Link>)}</div></TableCell>
      <TableCell><label className="flex items-center gap-1"><input type="radio" name={`${subject}-a`} aria-label={`A ${version.version}`} checked={selectedA === version.version} onChange={() => onSelect("a", version.version)} />A</label></TableCell>
      <TableCell><label className="flex items-center gap-1"><input type="radio" name={`${subject}-b`} aria-label={`B ${version.version}`} checked={selectedB === version.version} onChange={() => onSelect("b", version.version)} />B</label></TableCell>
    </TableRow>)}</TableBody>
  </Table>
}
