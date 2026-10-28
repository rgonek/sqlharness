import { useId } from "react"
import { Button } from "@/components/ui/button"
import { Popover, PopoverTrigger, PopoverContent, PopoverTitle } from "@/components/ui/popover"
import type { AgentSessionKey, OperationSummary, OptimizationVersion } from "@/api/types"
import { Link } from "@tanstack/react-router"
import { operationReturnSearch } from "@/lib/sessionSearch"
import type { SessionSearch } from "@/router"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { HugeiconsIcon } from "@hugeicons/react";
import { MoreHorizontalCircle01Icon, SparklesIcon } from "@hugeicons/core-free-icons";

function metric(value: number | null): string {
  return value === null ? "—" : value.toLocaleString("en-US")
}

export function VersionTable({ subject, sessionId, agentSessionKey, sessionSearch, versions, selectedA, selectedB, onSelect, operations = [] }: {
  operations?: OperationSummary[]; subject: string; sessionId: number; agentSessionKey?: AgentSessionKey; sessionSearch: SessionSearch; versions: OptimizationVersion[]; selectedA: string; selectedB: string | null; onSelect: (side: "a" | "b", version: string) => void
}) {
  const groupId = useId()
  const baseline = versions.find(version => version.version === selectedA)
  const delta = (value: number | null, reference: number | null | undefined) => value === null || reference === null || reference === undefined || reference === 0 ? null : `${value > reference ? "+" : ""}${Math.round((value - reference) * 100 / reference)}%`
  return <Table className="version-table" aria-label={`${subject} versions`}>
    <TableHeader><TableRow><TableHead>Version</TableHead><TableHead className="text-right">Logical reads</TableHead><TableHead className="text-right">vs A</TableHead><TableHead className="text-right">Elapsed median</TableHead><TableHead className="text-right">vs A</TableHead><TableHead>Outcome</TableHead><TableHead id={`${groupId}-a-label`}><span aria-hidden="true">A</span><span className="sr-only">Baseline A</span></TableHead><TableHead id={`${groupId}-b-label`}><span aria-hidden="true">B</span><span className="sr-only">Candidate B</span></TableHead><TableHead className="w-10"><span className="sr-only">Observed in</span></TableHead></TableRow></TableHeader>
    <TableBody>{versions.map(version => <TableRow key={`${version.version}:${version.hash}`} className={selectedA === version.version || selectedB === version.version ? "bg-muted/40" : undefined}>
      <TableHead scope="row" className="whitespace-normal"><div className="flex items-center gap-2">{version.version}{operations.some(operation => version.measuredIn.includes(operation.id) && operation.agentKeep) && <HugeiconsIcon icon={SparklesIcon} strokeWidth={2} className="size-3.5 text-violet-600" aria-label="Kept by agent" />}</div>{operations.filter(operation => version.measuredIn.includes(operation.id) && operation.agentLabel).map(operation => <div key={operation.id} className="mt-1 text-xs font-normal text-muted-foreground" title={`Agent label for operation #${operation.id}`}>{operation.agentLabel}</div>)}</TableHead>
      <TableCell className="text-right tabular-nums">{metric(version.logicalReadsSum)}</TableCell><TableCell className="text-right tabular-nums" data-delta={version.logicalReadsSum !== null && baseline?.logicalReadsSum !== null && baseline?.logicalReadsSum !== undefined && version.logicalReadsSum < baseline.logicalReadsSum ? "better" : undefined}>{selectedA === version.version ? "—" : delta(version.logicalReadsSum, baseline?.logicalReadsSum) ?? "—"}</TableCell>
      <TableCell className="text-right tabular-nums">{metric(version.elapsedMedianMs)}{version.elapsedMedianMs !== null && " ms"}</TableCell><TableCell className="text-right tabular-nums" data-delta={version.elapsedMedianMs !== null && baseline?.elapsedMedianMs !== null && baseline?.elapsedMedianMs !== undefined && version.elapsedMedianMs < baseline.elapsedMedianMs ? "better" : undefined}>{selectedA === version.version ? "—" : delta(version.elapsedMedianMs, baseline?.elapsedMedianMs) ?? "—"}</TableCell><TableCell title={`basis: ${version.verdictBasis}`}>{version.caseOutcome}</TableCell>

      <TableCell><input type="radio" className="accent-primary" value={version.version} name={`${groupId}-a`} aria-describedby={`${groupId}-a-label`} aria-label={`A ${version.version}`} checked={selectedA === version.version} onChange={() => onSelect("a", version.version)} /></TableCell>
      <TableCell><input type="radio" className="accent-primary" value={version.version} name={`${groupId}-b`} aria-describedby={`${groupId}-b-label`} aria-label={`B ${version.version}`} checked={selectedB === version.version} onChange={() => onSelect("b", version.version)} /></TableCell>
      <TableCell><Popover><PopoverTrigger render={<Button variant="ghost" size="icon-sm" />} aria-label={`Details for ${version.version}`}><HugeiconsIcon icon={MoreHorizontalCircle01Icon} strokeWidth={2} /></PopoverTrigger><PopoverContent align="end"><PopoverTitle>{version.version}</PopoverTitle><p>basis: {version.verdictBasis}</p><p>Scans: {metric(version.scansSum)}</p>{version.measuredIn.map(operationId => <Link key={operationId} to="/operations/$id" params={{ id: String(operationId) }} search={operationReturnSearch(sessionId, sessionSearch, agentSessionKey)} className="block underline">Operation #{operationId}</Link>)}</PopoverContent></Popover></TableCell>
    </TableRow>)}</TableBody>
  </Table>
}
