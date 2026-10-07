import type { DistilledPlan } from "@/api/types"
import { Badge } from "@/components/ui/badge"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { formatNumber, formatPercent } from "@/lib/format"
import { flattenPlan } from "@/lib/plan"

export function PlanTree({ plan }: { plan: DistilledPlan }) {
  return (
    <div className="space-y-4">
      {plan.statements.map((statement, index) => (
        <div key={index} className="space-y-2">
          {statement.sql && <pre className="overflow-x-auto whitespace-pre-wrap font-mono text-sm">{statement.sql}</pre>}
          <div className="overflow-x-auto">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Operator</TableHead>
                  <TableHead>Object</TableHead>
                  <TableHead className="text-right">Estimated rows</TableHead>
                  <TableHead className="text-right">Actual rows</TableHead>
                  <TableHead className="text-right">Executions</TableHead>
                  <TableHead className="text-right">Cost</TableHead>
                  <TableHead>Warnings</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {flattenPlan(statement.root).map(row => (
                  <TableRow key={row.key}>
                    {/* Indentation is layout: one step per tree level. */}
                    <TableCell style={{ paddingLeft: `${row.depth * 1.25 + 0.5}rem` }}>
                      {row.node.physicalOp}
                      {row.node.logicalOp && row.node.logicalOp !== row.node.physicalOp && (
                        <span className="text-sm text-muted-foreground"> ({row.node.logicalOp})</span>
                      )}
                    </TableCell>
                    <TableCell className="font-mono">{[row.node.objectName, row.node.indexName].filter(Boolean).join(".") || "—"}</TableCell>
                    <TableCell className="text-right">{formatNumber(row.node.estimatedRows)}</TableCell>
                    <TableCell className="text-right">{formatNumber(row.node.actualRows)}</TableCell>
                    <TableCell className="text-right">{formatNumber(row.node.executions)}</TableCell>
                    <TableCell className="text-right">{formatPercent(row.node.costFraction)}</TableCell>
                    <TableCell>
                      <div className="flex flex-wrap gap-1">
                        {/* The distiller does not dedupe warnings, so the index is part of the key. */}
                        {(row.node.warnings ?? []).map((warning, i) => <Badge key={`${i}:${warning}`} variant="outline">{warning}</Badge>)}
                      </div>
                    </TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </div>
          {(statement.missingIndexes?.length ?? 0) > 0 && (
            <div className="space-y-1 text-sm">
              {(statement.missingIndexes ?? []).map((missing, i) => (
                <div key={i} className="font-mono">
                  Missing index on {missing.table} ({[...missing.equalityColumns, ...missing.inequalityColumns].join(", ")})
                  {missing.includeColumns.length > 0 && ` include (${missing.includeColumns.join(", ")})`} — impact {Math.round(missing.impact)}%
                </div>
              ))}
            </div>
          )}
        </div>
      ))}
    </div>
  )
}
