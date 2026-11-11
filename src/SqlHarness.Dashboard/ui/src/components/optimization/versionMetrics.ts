import type { OptimizationVersion, VersionCaseMetric } from "@/api/types"

export function versionMetrics(version: OptimizationVersion, selectedCase = "all", aggregate = "sum", matchingKeys?: string[], crossConditions = false) {
  const all = version.metricCases
  if (!all?.length) return { reads: selectedCase === "all" ? aggregate === "mean" ? version.logicalReadsMean : version.logicalReadsSum : null, scans: selectedCase === "all" && aggregate === "sum" ? version.scansSum : null, elapsed: null, cpu: null, coverage: "Case timing evidence unavailable" }
  const key = (row: VersionCaseMetric) => crossConditions ? row.varyingFingerprint : row.caseKey
  const expected = selectedCase !== "all" ? [selectedCase] : matchingKeys
  const rows = expected ? all.filter(row => expected.includes(key(row) ?? "")) : all
  const complete = rows.length > 0 && (!expected || expected.every(id => rows.filter(row => key(row) === id).length === 1))
  const combine = (field: "logicalReads" | "scans" | "elapsedMs" | "cpuMs") => !complete || rows.some(row => row[field] == null) ? null : rows.reduce((sum, row) => sum + row[field]!, 0) / (aggregate === "mean" ? rows.length : 1)
  return { reads: combine("logicalReads"), scans: combine("scans"), elapsed: combine("elapsedMs"), cpu: combine("cpuMs"), coverage: expected ? `${rows.length}/${expected.length} selected · ${rows.length}/${all.length} retained cases` : `${rows.length}/${all.length} cases` }
}
