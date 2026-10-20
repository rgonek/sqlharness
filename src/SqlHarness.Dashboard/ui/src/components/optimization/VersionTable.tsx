import type { AgentSessionKey, OptimizationVersion } from "@/api/types"
import { Link } from "@tanstack/react-router"
import { operationReturnSearch } from "@/lib/sessionSearch"
import type { SessionSearch } from "@/router"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"

function metric(value: number | null): string {
  return value === null ? "—" : value.toLocaleString()
}

export function VersionTable({ subject, sessionId, agentSessionKey, sessionSearch, versions, selectedA, selectedB, onSelect }: {
  subject: string; sessionId: number; agentSessionKey?: AgentSessionKey; sessionSearch: SessionSearch; versions: OptimizationVersion[]; selectedA: string; selectedB: string | null; onSelect: (side: "a" | "b", version: string) => void
}) {
  return <Table aria-label={`${subject} versions`}>
    <TableHeader><TableRow><TableHead>Version</TableHead><TableHead>Logical reads (sum)</TableHead><TableHead>Scans</TableHead><TableHead>Elapsed median (ms)</TableHead><TableHead>Outcome</TableHead><TableHead>Observed in</TableHead><TableHead>A</TableHead><TableHead>B</TableHead></TableRow></TableHeader>
    <TableBody>{versions.map(version => <TableRow key={`${version.version}:${version.hash}`}>
      <TableHead scope="row" className="whitespace-normal">{version.version}</TableHead>
      <TableCell>{metric(version.logicalReadsSum)}</TableCell><TableCell>{metric(version.scansSum)}</TableCell>
      <TableCell>{metric(version.elapsedMedianMs)}</TableCell><TableCell className="whitespace-normal"><span>{version.caseOutcome}</span><span className="ml-1 text-xs text-muted-foreground" title={`Verdict basis: ${version.verdictBasis}`}>basis: {version.verdictBasis}</span></TableCell>
      <TableCell className="whitespace-normal"><div className="flex flex-wrap gap-x-2">{version.measuredIn.map(operationId => <Link key={operationId} to="/operations/$id" params={{ id: String(operationId) }} search={operationReturnSearch(sessionId, sessionSearch, agentSessionKey)} className="underline">Operation #{operationId}</Link>)}</div></TableCell>
      <TableCell><label className="flex items-center gap-1"><input type="radio" name={`${subject}-a`} aria-label={`A ${version.version}`} checked={selectedA === version.version} onChange={() => onSelect("a", version.version)} />A</label></TableCell>
      <TableCell><label className="flex items-center gap-1"><input type="radio" name={`${subject}-b`} aria-label={`B ${version.version}`} checked={selectedB === version.version} onChange={() => onSelect("b", version.version)} />B</label></TableCell>
    </TableRow>)}</TableBody>
  </Table>
}
