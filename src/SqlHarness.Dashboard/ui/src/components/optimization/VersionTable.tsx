import { CaseOutcomeDetails } from "./CaseOutcomeDetails"
import type { OptimizationComparison } from "@/api/types"
import { Badge } from "@/components/ui/badge"
import { cn } from "@/lib/utils"
import { useId } from "react"
import { buttonVariants, Button } from "@/components/ui/button"
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

export function VersionTable({ subject, sessionId, agentSessionKey, sessionSearch, versions, selectedA, selectedB, onSelect, operations = [], groupA, groupB, comparison }: {
  comparison?: OptimizationComparison; operations?: OperationSummary[]; subject: string; sessionId: number; agentSessionKey?: AgentSessionKey; sessionSearch: SessionSearch; versions: (OptimizationVersion & { group?: { fingerprint: string; label: string; badge: string } })[]; groupA?: string; groupB?: string; selectedA: string; selectedB: string | null; onSelect: (side: "a" | "b", version: string, group?: string) => void
}) {
  const groupId = useId()
  const isA = (version: typeof versions[number]) => version.version === selectedA && (!groupA || version.group?.fingerprint === groupA)
  const isB = (version: typeof versions[number]) => version.version === selectedB && (!groupB || version.group?.fingerprint === groupB)
  const accessibleName = (side: string, version: typeof versions[number]) => `${side} ${version.version}${versions.filter(item => item.version === version.version).length > 1 ? ` (${version.group?.badge}: ${version.group?.label})` : ""}`
  const baseline = versions.find(isA)
  const delta = (value: number | null, reference: number | null | undefined) => value === null || reference === null || reference === undefined || reference === 0 ? null : `${value > reference ? "+" : ""}${Math.round((value - reference) * 100 / reference)}%`
  return <Table className="version-table" aria-label={`${subject} versions`}>
    <TableHeader><TableRow><TableHead>Version</TableHead><TableHead>Conditions</TableHead><TableHead className="text-right">Rows</TableHead><TableHead className="text-right">Logical reads</TableHead><TableHead className="text-right">vs A</TableHead><TableHead className="text-right">Elapsed median</TableHead><TableHead className="text-right">vs A</TableHead><TableHead>Outcome</TableHead><TableHead id={`${groupId}-a-label`}><span aria-hidden="true">A</span><span className="sr-only">Baseline A</span></TableHead><TableHead id={`${groupId}-b-label`}><span aria-hidden="true">B</span><span className="sr-only">Candidate B</span></TableHead><TableHead className="w-10"><span className="sr-only">Observed in</span></TableHead></TableRow></TableHeader>
    <TableBody>{versions.map(version => <TableRow key={`${version.group?.fingerprint ?? ""}:${version.version}`} className={isA(version) || isB(version) ? "bg-muted/40" : undefined}>
      <TableHead scope="row" className="max-w-80"><div className="flex items-center gap-2">{version.version}{operations.some(operation => version.measuredIn.includes(operation.id) && operation.agentKeep) && <HugeiconsIcon icon={SparklesIcon} strokeWidth={2} className="size-3.5 text-muted-foreground" aria-label="Kept by agent" />}{operations.filter(operation => version.measuredIn.includes(operation.id) && operation.agentLabel).map(operation => <span key={operation.id} className="truncate text-xs font-normal text-muted-foreground" title={operation.agentLabel ?? undefined}>{operation.agentLabel}</span>)}</div></TableHead>
      <TableCell><Badge variant="secondary" title={version.group?.label}>{version.group?.badge}</Badge></TableCell><TableCell className="text-right tabular-nums">{version.rowsReturnedVaries ? "varies" : metric(version.rowsReturnedSum ?? null)}</TableCell><TableCell className="text-right tabular-nums">{metric(version.logicalReadsSum)}</TableCell><TableCell className="text-right tabular-nums" data-delta={version.logicalReadsSum !== null && baseline?.logicalReadsSum !== null && baseline?.logicalReadsSum !== undefined && version.group?.fingerprint === baseline?.group?.fingerprint && version.logicalReadsSum < baseline.logicalReadsSum ? "better" : undefined}>{isA(version) ? "—" : (version.group?.fingerprint === baseline?.group?.fingerprint ? delta(version.logicalReadsSum, baseline?.logicalReadsSum) : null) ?? "—"}</TableCell>
      <TableCell className="text-right tabular-nums">{metric(version.elapsedMedianMs)}{version.elapsedMedianMs !== null && " ms"}</TableCell><TableCell className="text-right tabular-nums" data-delta={version.elapsedMedianMs !== null && baseline?.elapsedMedianMs !== null && baseline?.elapsedMedianMs !== undefined && version.group?.fingerprint === baseline?.group?.fingerprint && version.elapsedMedianMs < baseline.elapsedMedianMs ? "better" : undefined}>{isA(version) ? "—" : (version.group?.fingerprint === baseline?.group?.fingerprint ? delta(version.elapsedMedianMs, baseline?.elapsedMedianMs) : null) ?? "—"}</TableCell><TableCell>{isB(version) && comparison?.outcomeCounts && !isA(version) ? <CaseOutcomeDetails cases={comparison.caseOutcomes ?? []} counts={comparison.outcomeCounts} selectedCase={comparison.selectedCase} /> : isA(version) && comparison?.caseOutcomes && comparison.outcomeCounts ? `Baseline · ${comparison.caseOutcomes.filter(c => (comparison.selectedCase === "all" || c.id === comparison.selectedCase) && c.readsBefore !== null).length}/${comparison.outcomeCounts.total} measured` : "—"}</TableCell>

      <TableCell><label className="relative inline-flex"><input type="radio" className="peer sr-only" value={version.version} name={`${groupId}-a`} aria-describedby={`${groupId}-a-label`} aria-label={accessibleName("A", version)} checked={isA(version)} onChange={() => onSelect("a", version.version, version.group?.fingerprint)} /><span aria-hidden="true" className={cn(buttonVariants({ variant: isA(version) ? "secondary" : "outline", size: "icon-sm" }), "rounded-full peer-focus-visible:ring-2 peer-focus-visible:ring-ring")}>A</span></label></TableCell>
      <TableCell><label className="relative inline-flex"><input type="radio" className="peer sr-only" value={version.version} name={`${groupId}-b`} aria-describedby={`${groupId}-b-label`} aria-label={accessibleName("B", version)} checked={isB(version)} onChange={() => onSelect("b", version.version, version.group?.fingerprint)} /><span aria-hidden="true" className={cn(buttonVariants({ variant: isB(version) ? "default" : "outline", size: "icon-sm" }), "rounded-full peer-focus-visible:ring-2 peer-focus-visible:ring-ring")}>B</span></label></TableCell>
      <TableCell><Popover><PopoverTrigger render={<Button variant="ghost" size="icon-sm" />} aria-label={`Details for ${version.version}`}><HugeiconsIcon icon={MoreHorizontalCircle01Icon} strokeWidth={2} /></PopoverTrigger><PopoverContent align="end"><PopoverTitle>{version.version}</PopoverTitle><p>basis: {version.verdictBasis}</p><p>Scans: {metric(version.scansSum)}</p>{version.measuredIn.map(operationId => <Link key={operationId} to="/operations/$id" params={{ id: String(operationId) }} search={operationReturnSearch(sessionId, sessionSearch, agentSessionKey)} className="block underline">Operation #{operationId}</Link>)}</PopoverContent></Popover></TableCell>
    </TableRow>)}</TableBody>
  </Table>
}
