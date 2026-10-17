import type { OptimizationVersion } from "@/api/types"

function metric(value: number | null): string {
  return value === null ? "—" : value.toLocaleString()
}

export function VersionTable({ subject, versions, selectedA, selectedB, onSelect }: {
  subject: string; versions: OptimizationVersion[]; selectedA: string; selectedB: string; onSelect: (side: "a" | "b", version: string) => void
}) {
  return <div className="overflow-x-auto">
    <table className="w-full text-sm" aria-label={`${subject} versions`}>
      <thead><tr className="border-b text-left"><th className="py-2 pr-3">Version</th><th className="px-3 py-2">Logical reads (sum)</th><th className="px-3 py-2">Scans</th><th className="px-3 py-2">Elapsed median (ms)</th><th className="px-3 py-2">Outcome</th><th className="px-3 py-2">A</th><th className="px-3 py-2">B</th></tr></thead>
      <tbody>{versions.map(version => <tr key={`${version.version}:${version.hash}`} className="border-b">
        <th scope="row" className="py-2 pr-3 text-left font-medium">{version.version}</th>
        <td className="px-3 py-2">{metric(version.logicalReadsSum)}</td><td className="px-3 py-2">{metric(version.scansSum)}</td>
        <td className="px-3 py-2">{metric(version.elapsedMedianMs)}</td><td className="px-3 py-2">{version.caseOutcome}</td>
        <td className="px-3 py-2"><label className="flex items-center gap-1"><input type="radio" name={`${subject}-a`} aria-label={`A ${version.version}`} checked={selectedA === version.version} onChange={() => onSelect("a", version.version)} />A</label></td>
        <td className="px-3 py-2"><label className="flex items-center gap-1"><input type="radio" name={`${subject}-b`} aria-label={`B ${version.version}`} checked={selectedB === version.version} onChange={() => onSelect("b", version.version)} />B</label></td>
      </tr>)}</tbody>
    </table>
  </div>
}
