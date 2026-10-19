import type { OptimizationVersion } from "@/api/types"
import { Link } from "@tanstack/react-router"
import { operationReturnSearch } from "@/lib/sessionSearch"
import type { SessionSearch } from "@/router"

function metric(value: number | null): string {
  return value === null ? "—" : value.toLocaleString()
}

export function VersionTable({ subject, sessionId, sessionSearch, versions, selectedA, selectedB, onSelect }: {
  subject: string; sessionId: number; sessionSearch: SessionSearch; versions: OptimizationVersion[]; selectedA: string; selectedB: string | null; onSelect: (side: "a" | "b", version: string) => void
}) {
  return <div className="overflow-x-auto">
    <table className="w-full text-sm" aria-label={`${subject} versions`}>
      <thead><tr className="border-b text-left"><th className="py-2 pr-3">Version</th><th className="px-3 py-2">Logical reads (sum)</th><th className="px-3 py-2">Scans</th><th className="px-3 py-2">Elapsed median (ms)</th><th className="px-3 py-2">Outcome</th><th className="px-3 py-2">Observed in</th><th className="px-3 py-2">A</th><th className="px-3 py-2">B</th></tr></thead>
      <tbody>{versions.map(version => <tr key={`${version.version}:${version.hash}`} className="border-b">
        <th scope="row" className="py-2 pr-3 text-left font-medium">{version.version}</th>
        <td className="px-3 py-2">{metric(version.logicalReadsSum)}</td><td className="px-3 py-2">{metric(version.scansSum)}</td>
        <td className="px-3 py-2">{metric(version.elapsedMedianMs)}</td><td className="px-3 py-2"><span>{version.caseOutcome}</span><span className="ml-1 text-xs text-muted-foreground" title={`Verdict basis: ${version.verdictBasis}`}>basis: {version.verdictBasis}</span></td>
        <td className="px-3 py-2"><div className="flex flex-wrap gap-x-2">{version.measuredIn.map(operationId => <Link key={operationId} to="/operations/$id" params={{ id: String(operationId) }} search={operationReturnSearch(sessionId, sessionSearch)} className="underline">Operation #{operationId}</Link>)}</div></td>
        <td className="px-3 py-2"><label className="flex items-center gap-1"><input type="radio" name={`${subject}-a`} aria-label={`A ${version.version}`} checked={selectedA === version.version} onChange={() => onSelect("a", version.version)} />A</label></td>
        <td className="px-3 py-2"><label className="flex items-center gap-1"><input type="radio" name={`${subject}-b`} aria-label={`B ${version.version}`} checked={selectedB === version.version} onChange={() => onSelect("b", version.version)} />B</label></td>
      </tr>)}</tbody>
    </table>
  </div>
}
