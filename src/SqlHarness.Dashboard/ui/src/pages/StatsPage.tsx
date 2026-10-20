import { useState } from "react"
import { useNavigate } from "@tanstack/react-router"
import { Bar, BarChart, CartesianGrid, XAxis, YAxis } from "recharts"
import { rangeWindow, type StatsRange, useProfiles, useStats, type StatsWindow } from "@/api/queries"
import type { DashboardStats, KeyCount, ProfileView, SqlHashStat, WorkflowCost } from "@/api/types"
import { agentLabel } from "@/lib/agents"
import { ErrorState } from "@/components/ErrorState"
import { Kpi } from "@/components/Kpi"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import { type ChartConfig, ChartContainer, ChartLegend, ChartLegendContent, ChartTooltip, ChartTooltipContent } from "@/components/ui/chart"
import { Label } from "@/components/ui/label"
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { Skeleton } from "@/components/ui/skeleton"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { Tabs, TabsList, TabsTrigger } from "@/components/ui/tabs"
import { formatDuration, formatNumber, shortHash } from "@/lib/format"
import { pivotPerDay } from "@/lib/stats"
import { ProfileDimensionsPanel, type DimensionAxes, type DimensionCellScope } from "./ProfileDimensionsPanel"

const ranges: { value: StatsRange; label: string }[] = [
  { value: "24h", label: "24 hours" },
  { value: "7d", label: "7 days" },
  { value: "30d", label: "30 days" },
  { value: "all", label: "All time" },
]

function WorkflowCostCard({ costs }: { costs: WorkflowCost[] }) {
  const outputBytes = costs.reduce((sum, cost) => sum + cost.outputBytes, 0)
  return (
    <Card>
      <CardHeader>
        <CardTitle>Workflow output cost</CardTitle>
      </CardHeader>
      <CardContent className="space-y-3 overflow-x-auto">
        {costs.length > 0 ? (
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Workflow</TableHead>
                <TableHead className="text-right">Output bytes</TableHead>
                <TableHead className="text-right">Estimated output tokens</TableHead>
                <TableHead className="text-right">Calls</TableHead>
                <TableHead className="text-right">Dry runs</TableHead>
                <TableHead className="text-right">Database executions</TableHead>
                <TableHead>Comparison</TableHead>
                <TableHead className="text-right">Baseline bytes</TableHead>
                <TableHead className="text-right">Output overhead bytes</TableHead>
                <TableHead className="text-right">Estimated output-token delta</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {costs.map((cost, index) => (
                <TableRow key={cost.rootOperationRef ?? `legacy-${index}`}>
                  <TableCell>{cost.rootOperationRef ?? "Legacy output"}</TableCell>
                  <TableCell className="text-right">{formatNumber(cost.outputBytes)}</TableCell>
                  <TableCell className="text-right">{formatNumber(cost.estimatedOutputTokens)}</TableCell>
                  <TableCell className="text-right">{formatNumber(cost.callCount)}</TableCell>
                  <TableCell className="text-right">{formatNumber(cost.dryRunCalls)}</TableCell>
                  <TableCell className="text-right">{formatNumber(cost.databaseExecutionCalls)}</TableCell>
                  <TableCell>{cost.comparison?.baselineKind ?? "Unavailable"}</TableCell>
                  <TableCell className="text-right">{cost.comparison ? formatNumber(cost.comparison.baselineBytes) : "—"}</TableCell>
                  <TableCell className="text-right">{cost.comparison ? formatNumber(cost.comparison.outputOverheadBytes) : "—"}</TableCell>
                  <TableCell className="text-right">{cost.comparison?.estimatedOutputTokenDelta == null ? "—" : formatNumber(cost.comparison.estimatedOutputTokenDelta)}</TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        ) : <p className="text-sm text-muted-foreground">No measured workflow output in this time range.</p>}
        {costs.length > 0 && <p className="text-sm text-muted-foreground">Measured output: {formatNumber(outputBytes)} bytes. Input, discovery and chat are unmeasured. Token estimation method: UTF-8 bytes divided by four, rounded up.</p>}
        {costs.some(cost => cost.accountingKind === "legacy-artifact-volume") &&
          <p className="text-xs text-muted-foreground">Legacy artifact-volume estimates are reported separately without a comparison.</p>}
      </CardContent>
    </Card>
  )
}

function SqlTable({ rows }: { rows: SqlHashStat[] }) {
  return (
    <Table>
      <TableHeader>
        <TableRow>
          <TableHead>SQL hash</TableHead>
          <TableHead className="text-right">Runs</TableHead>
          <TableHead className="text-right">Total</TableHead>
          <TableHead className="text-right">Slowest</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {rows.map(row => (
          <TableRow key={row.sqlHash}>
            <TableCell className="font-mono">{shortHash(row.sqlHash)}</TableCell>
            <TableCell className="text-right">{formatNumber(row.count)}</TableCell>
            <TableCell className="text-right">{formatDuration(row.totalDurationMs)}</TableCell>
            <TableCell className="text-right">{formatDuration(row.maxDurationMs)}</TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
  )
}

function CountChart({ rows, label }: { rows: KeyCount[]; label: string }) {
  const config = { count: { label, color: "var(--chart-1)" } } satisfies ChartConfig
  return (
    <ChartContainer config={config} className="h-56 w-full">
      <BarChart data={rows}>
        <CartesianGrid vertical={false} />
        <XAxis dataKey="key" tickLine={false} axisLine={false} />
        <YAxis allowDecimals={false} tickLine={false} axisLine={false} width={32} />
        <ChartTooltip content={<ChartTooltipContent />} />
        <Bar dataKey="count" fill="var(--color-count)" radius={4} />
      </BarChart>
    </ChartContainer>
  )
}

type StatsPageProps = { onDimensionCellSelect?: (scope: DimensionCellScope) => void }

export function StatsPage(props: StatsPageProps = {}) {
  return props.onDimensionCellSelect
    ? <StatsPageContent onDimensionCellSelect={props.onDimensionCellSelect} />
    : <StatsPageRoute />
}

function StatsPageRoute() {
  const navigate = useNavigate()
  return <StatsPageContent onDimensionCellSelect={scope => void navigate({ to: "/operations", search: {
    from: scope.from, to: scope.to, profile: scope.unprofiled ? undefined : scope.profile ?? undefined,
    unprofiled: scope.unprofiled || undefined, dimensions: JSON.stringify(scope.dimensions),
  } })} />
}

function StatsPageContent({ onDimensionCellSelect }: Required<StatsPageProps>) {
  const [range, setRange] = useState<StatsRange>("7d")
  const [window, setWindow] = useState<StatsWindow>(() => rangeWindow("7d", Date.now()))
  const [manualProfile, setManualProfile] = useState<{ value: string | null } | undefined>()
  const [dimensionFilters, setDimensionFilters] = useState<Record<string, string | null>>({})
  const [axisChoice, setAxisChoice] = useState<DimensionAxes | null>(null)
  const overview = useStats(range, window)
  const profilesQuery = useProfiles()
  const profiles = profilesQuery.data?.profiles ?? []
  const selectedProfile = manualProfile
    ? manualProfile.value
    : overview.data?.profileOperations.length
      ? overview.data.profileOperations[0].profile
      : profiles[0]?.name ?? null
  const selectedUnprofiled = manualProfile ? manualProfile.value === null
    : overview.data?.profileOperations[0]?.profile === null && overview.data.profileOperations.length > 0
  const query = useStats(range, window, selectedProfile, {}, undefined, undefined, selectedUnprofiled)
  const profileOptions = [...new Set([
    ...profiles.map(profile => profile.name),
    ...(overview.data?.profileOperations ?? []).flatMap(item => item.profile === null ? [] : [item.profile]),
    ...(manualProfile?.value ? [manualProfile.value] : []),
  ])].sort((left, right) => left.localeCompare(right))
  const selectedProfileView = profiles.find(profile => profile.name === selectedProfile)

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-4">
        <h1>Usage</h1>
        <div className="grid w-full min-w-0 gap-1.5 sm:w-auto sm:min-w-52">
          <Label htmlFor="stats-profile">Statistics profile</Label>
          <Select items={[...profileOptions.map(name => ({ value: `profile:${name}`, label: name })), { value: "unprofiled", label: "No profile" }]}
            value={selectedProfile === null ? "unprofiled" : `profile:${selectedProfile}`}
            onValueChange={value => {
              if (value === null) return
              setManualProfile({ value: value === "unprofiled" ? null : value.slice("profile:".length) })
              setDimensionFilters({})
              setAxisChoice(null)
            }}
          >
            <SelectTrigger id="stats-profile" className="w-full min-w-0"><SelectValue /></SelectTrigger>
            <SelectContent>{profileOptions.map(name => <SelectItem key={name} value={`profile:${name}`}>{name}</SelectItem>)}
              <SelectItem value="unprofiled">No profile</SelectItem>
            </SelectContent>
          </Select>
        </div>
        <Tabs value={range} onValueChange={value => {
          const nextRange = String(value) as StatsRange
          setRange(nextRange)
          setWindow(rangeWindow(nextRange, Date.now()))
        }}>
          <TabsList>
            {ranges.map(item => (
              <TabsTrigger key={item.value} value={item.value}>
                {item.label}
              </TabsTrigger>
            ))}
          </TabsList>
        </Tabs>
      </div>
      {overview.error ? (
        <ErrorState error={overview.error} />
      ) : query.error ? (
        <ErrorState error={query.error} />
      ) : overview.isPending || query.isPending ? (
        <Skeleton className="h-64 w-full" />
      ) : (
        <StatsContent stats={query.data} range={range} window={window} profile={selectedProfile} unprofiled={selectedUnprofiled} profileView={selectedProfileView}
          dimensionFilters={dimensionFilters} onDimensionFiltersChange={setDimensionFilters}
          axisChoice={axisChoice} onAxisChoiceChange={setAxisChoice} onDimensionCellSelect={onDimensionCellSelect} />
      )}
    </div>
  )
}

function StatsContent({
  stats,
  range,
  window,
  profile,
  unprofiled,
  profileView,
  dimensionFilters,
  onDimensionFiltersChange,
  axisChoice,
  onAxisChoiceChange,
  onDimensionCellSelect,
}: {
  stats: DashboardStats
  range: StatsRange
  window: StatsWindow
  profile: string | null
  unprofiled: boolean
  profileView: ProfileView | undefined
  dimensionFilters: Record<string, string | null>
  onDimensionFiltersChange: (filters: Record<string, string | null>) => void
  axisChoice: DimensionAxes | null
  onAxisChoiceChange: (axes: DimensionAxes | null) => void
  onDimensionCellSelect?: (scope: DimensionCellScope) => void
}) {
  const perDay = pivotPerDay(stats.operationsPerDay)
  // Series are keyed by index: an agent kind is client-supplied text and must never become a CSS variable name.
  const seriesKeys = perDay.agents.map((_, index) => `a${index}`)
  const perDayRows = perDay.rows.map(row => ({
    day: row.day,
    ...Object.fromEntries(perDay.agents.map((agent, index) => [seriesKeys[index], row[agent]])),
  }))
  const perDayConfig = Object.fromEntries(
    perDay.agents.map((agent, index) => [seriesKeys[index], { label: agentLabel(agent), color: `var(--chart-${(index % 5) + 1})` }]),
  ) satisfies ChartConfig
  const total = stats.operations.reduce((sum, row) => sum + row.count, 0)

  return (
    <div className="space-y-4">
      <div className="grid grid-cols-2 gap-4 md:grid-cols-4">
        <Kpi label="Operations" value={formatNumber(total)} />
        <Kpi label="Measured workflow output" value={`${formatNumber(stats.workflowCosts.reduce((sum, cost) => sum + cost.outputBytes, 0))} bytes`} />
        <Kpi label="Operations with spills" value={formatNumber(stats.spillOperations)} />
        <Kpi label="Operations on a cold cache" value={formatNumber(stats.coldCacheOperations)} />
      </div>
      <Card>
        <CardHeader>
          <CardTitle>Operations per day (UTC)</CardTitle>
        </CardHeader>
        <CardContent>
          <ChartContainer config={perDayConfig} className="h-64 w-full">
            <BarChart data={perDayRows}>
              <CartesianGrid vertical={false} />
              <XAxis dataKey="day" tickLine={false} axisLine={false} />
              <YAxis allowDecimals={false} tickLine={false} axisLine={false} width={32} />
              <ChartTooltip content={<ChartTooltipContent />} />
              <ChartLegend content={<ChartLegendContent />} />
              {seriesKeys.map(key => (
                <Bar key={key} dataKey={key} stackId="agents" fill={`var(--color-${key})`} />
              ))}
            </BarChart>
          </ChartContainer>
        </CardContent>
      </Card>
      <WorkflowCostCard costs={stats.workflowCosts} />
      <div className="grid gap-4 md:grid-cols-2">
        <Card>
          <CardHeader>
            <CardTitle>Statuses</CardTitle>
          </CardHeader>
          <CardContent>
            <CountChart rows={stats.statuses} label="Operations" />
          </CardContent>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle>Exit codes</CardTitle>
          </CardHeader>
          <CardContent>
            <CountChart rows={stats.exitCodes} label="Operations" />
          </CardContent>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle>Most frequent SQL</CardTitle>
          </CardHeader>
          <CardContent className="overflow-x-auto">
            <SqlTable rows={stats.topSqlByCount} />
          </CardContent>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle>Slowest SQL (total time)</CardTitle>
          </CardHeader>
          <CardContent className="overflow-x-auto">
            <SqlTable rows={stats.topSqlByDuration} />
          </CardContent>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle>Tables by logical reads</CardTitle>
          </CardHeader>
          <CardContent className="overflow-x-auto">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Table</TableHead>
                  <TableHead className="text-right">Logical reads</TableHead>
                  <TableHead className="text-right">Operations</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {stats.topTablesByLogicalReads.map(row => (
                  <TableRow key={row.table}>
                    <TableCell className="font-mono">{row.table}</TableCell>
                    <TableCell className="text-right">{formatNumber(row.logicalReads)}</TableCell>
                    <TableCell className="text-right">{formatNumber(row.operations)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </CardContent>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle>Top waits</CardTitle>
          </CardHeader>
          <CardContent className="overflow-x-auto">
            <Table>
              <TableHeader>
                <TableRow>
                  <TableHead>Wait type</TableHead>
                  <TableHead className="text-right">Total time</TableHead>
                </TableRow>
              </TableHeader>
              <TableBody>
                {stats.topWaits.map(row => (
                  <TableRow key={row.waitType}>
                    <TableCell className="font-mono">{row.waitType}</TableCell>
                    <TableCell className="text-right">{formatDuration(row.totalWaitMs)}</TableCell>
                  </TableRow>
                ))}
              </TableBody>
            </Table>
          </CardContent>
        </Card>
        <Card>
          <CardHeader>
            <CardTitle>Operations by kind</CardTitle>
          </CardHeader>
          <CardContent>
            <CountChart rows={stats.operations} label="Operations" />
          </CardContent>
        </Card>
      </div>
      <ProfileDimensionsPanel
        key={profile === null ? `scope:${unprofiled ? "unprofiled" : "all"}` : `profile:${profile}`}
        range={range}
        window={window}
        profile={profile}
        unprofiled={unprofiled}
        profileName={profile ?? "No profile"}
        databaseTemplate={profileView?.database}
        profileDefinitionAvailable={stats.profileDimensions.profileDefinitionAvailable}
        dimensions={stats.profileDimensions.dimensions}
        filterOptions={stats.profileDimensions.dimensions}
        targets={stats.profileDimensions.targets}
        dimensionFilters={dimensionFilters}
        onDimensionFiltersChange={onDimensionFiltersChange}
        axisChoice={axisChoice}
        onAxisChoiceChange={onAxisChoiceChange}
        onCellSelect={onDimensionCellSelect}
      />
    </div>
  )
}
