import { useId, useState } from "react"
import { useNavigate } from "@tanstack/react-router"
import { rangeWindow, type StatsRange, useProfiles, useStats, type StatsWindow } from "@/api/queries"
import type { DashboardStats, ProfileView, SqlHashStat } from "@/api/types"
import { agentLabel } from "@/lib/agents"
import { ErrorState } from "@/components/ErrorState"
import { SegmentedRadioGroup } from "@/components/SegmentedRadioGroup"
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "@/components/ui/card"
import { Label } from "@/components/ui/label"
import { Select, SelectContent, SelectGroup, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select"
import { Separator } from "@/components/ui/separator"
import { Skeleton } from "@/components/ui/skeleton"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { Tabs, TabsList, TabsTrigger } from "@/components/ui/tabs"
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip"
import { formatDuration, formatNumber, shortHash } from "@/lib/format"
import { failureRate, pivotPerDay } from "@/lib/stats"
import { ProfileDimensionsPanel, type DimensionAxes, type DimensionCellScope } from "./ProfileDimensionsPanel"

const ranges: { value: StatsRange; label: string }[] = [
  { value: "24h", label: "24 hours" },
  { value: "7d", label: "7 days" },
  { value: "30d", label: "30 days" },
  { value: "all", label: "All time" },
]

function SqlHash({ value }: { value: string }) {
  const id = useId()
  return <Tooltip><TooltipTrigger aria-describedby={id} render={<span tabIndex={0} className="rounded-sm focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring" />}>{shortHash(value)}</TooltipTrigger><TooltipContent id={id} role="tooltip" className="break-all">{value}</TooltipContent></Tooltip>
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
          <TableHead className="text-right">Logical reads</TableHead>
        </TableRow>
      </TableHeader>
      <TableBody>
        {rows.map(row => (
          <TableRow key={row.sqlHash}>
            <TableCell className="font-mono"><SqlHash value={row.sqlHash} /></TableCell>
            <TableCell className="text-right">{formatNumber(row.count)}</TableCell>
            <TableCell className="text-right">{formatDuration(row.totalDurationMs)}</TableCell>
            <TableCell className="text-right">{formatDuration(row.maxDurationMs)}</TableCell>
            <TableCell className="text-right"><div>{row.logicalReadsMedianSum == null ? "Unavailable" : formatNumber(row.logicalReadsMedianSum)}</div><div className="text-xs text-muted-foreground">{row.logicalReadsAvailableOperations ?? 0}/{row.count} ops · {row.logicalReadsAvailableMetrics ?? 0} recorded metrics{(row.logicalReadsUnavailableMetrics ?? 0) > 0 ? ` · ${row.logicalReadsUnavailableMetrics} missing metrics` : ""}</div></TableCell>
          </TableRow>
        ))}
      </TableBody>
    </Table>
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
  const dimensionOptions = useStats(range, window, selectedProfile, {}, undefined, undefined, selectedUnprofiled)
  // Only current, loaded dimension options may remove retained selections. While
  // the new range is loading, keep selections intact and hide its pending metrics.
  const dimensionNames = dimensionOptions.data?.profileDimensions.dimensions.map(dimension => dimension.name)
  const effectiveFilters = dimensionNames
    ? Object.fromEntries(Object.entries(dimensionFilters).filter(([name]) => dimensionNames.includes(name)))
    : dimensionFilters
  const query = useStats(range, window, selectedProfile, effectiveFilters, undefined, undefined, selectedUnprofiled, dimensionOptions.data !== undefined)
  const profileOptions = [...new Set([
    ...profiles.map(profile => profile.name),
    ...(overview.data?.profileOperations ?? []).flatMap(item => item.profile === null ? [] : [item.profile]),
    ...(manualProfile?.value ? [manualProfile.value] : []),
  ])].sort((left, right) => left.localeCompare(right))
  const selectedProfileView = profiles.find(profile => profile.name === selectedProfile)

  return (
    <div className="usage-page flex flex-col gap-4">
      <div className="flex flex-wrap items-center gap-3">
        <div><h1>Usage</h1><p className="text-xs text-muted-foreground">{selectedProfile ?? "No profile"} · {ranges.find(item => item.value === range)?.label.toLowerCase()} · UTC</p></div>
        <div className="ml-auto grid w-full min-w-0 gap-1.5 sm:w-auto sm:min-w-40">
          <Label className="sr-only" htmlFor="stats-profile">Statistics profile</Label>
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
            <SelectContent><SelectGroup>{profileOptions.map(name => <SelectItem key={name} value={`profile:${name}`}>{name}</SelectItem>)}
              <SelectItem value="unprofiled">No profile</SelectItem>
            </SelectGroup></SelectContent>
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
      ) : overview.isPending || dimensionOptions.isPending ? (
        <Skeleton className="h-64 w-full" />
      ) : (
        <StatsContent metricsPending={query.isPending} stats={{ ...(query.data ?? dimensionOptions.data!), profileDimensions: dimensionOptions.data!.profileDimensions }} range={range} window={window} profile={selectedProfile} unprofiled={selectedUnprofiled} profileView={selectedProfileView}
          dimensionFilters={effectiveFilters} onDimensionFiltersChange={setDimensionFilters}
          axisChoice={axisChoice} onAxisChoiceChange={setAxisChoice} onDimensionCellSelect={onDimensionCellSelect} />
      )}
    </div>
  )
}

function StatsContent({
  metricsPending,
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
  metricsPending: boolean
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
  const [sqlSort, setSqlSort] = useState("duration")
  const topSql = sqlSort === "reads" ? stats.topSqlByLogicalReads ?? [] : sqlSort === "runs" ? stats.topSqlByCount : stats.topSqlByDuration
  const perDay = pivotPerDay(stats.operationsPerDay)
  const total = stats.operations.reduce((sum, row) => sum + row.count, 0)

  const rate = failureRate(stats.statuses, total)
  const failed = stats.statuses.find(row => row.key === "failed")?.count ?? 0
  const rejected = stats.statuses.find(row => row.key === "rejected")?.count ?? 0
  const output = stats.workflowCosts.filter(cost => cost.accountingKind === "workflow-output")
  const kpis = [
    { label: "Operations", value: formatNumber(total), detail: `${stats.sessionCount == null ? "Session count unavailable" : `${formatNumber(stats.sessionCount)} sessions`}` },
    { label: "Failed / rejected", value: `${formatNumber(failed)} / ${formatNumber(rejected)}`, detail: rate === null ? "Rate unavailable · no operations" : `${rate.toLocaleString(undefined, { maximumFractionDigits: 1 })}% of ${formatNumber(total)} operations` },
    { label: "Estimated workflow output tokens", value: output.length ? formatNumber(output.reduce((sum, cost) => sum + cost.estimatedOutputTokens, 0)) : "Unavailable", detail: output.length ? `${formatNumber(output.reduce((sum, cost) => sum + cost.outputBytes, 0))} measured bytes · ÷ 4, rounded up per call` : "No retained workflow-output accounting" },
    { label: "Operations with spills", value: formatNumber(stats.spillOperations), detail: "Recorded spill evidence" },
    { label: "Operations on a cold cache", value: formatNumber(stats.coldCacheOperations), detail: "Recorded cold-cache evidence" },
  ]
  return (
    <div className="usage-content flex flex-col gap-4">
      {metricsPending && <Skeleton aria-label="Updating filtered statistics" className="h-64 w-full" />}
      <div hidden={metricsPending}><div className="flex flex-col gap-4">
      <Card aria-label="Usage summary" className="gap-0 overflow-hidden py-0">
        <CardHeader className="sr-only"><CardTitle>Usage summary</CardTitle></CardHeader>
        <CardContent className="grid min-w-0 grid-cols-1 p-0 sm:grid-cols-2 lg:grid-cols-5">
          {kpis.map(kpi => <div key={kpi.label} className="relative min-w-0"><div className="flex h-full flex-col gap-2 p-4"><div className="text-sm text-muted-foreground">{kpi.label}</div><div className="break-words text-2xl font-semibold tabular-nums">{kpi.value}</div><p className="text-xs text-muted-foreground">{kpi.detail}</p></div><Separator orientation="vertical" className="absolute right-0 top-0 hidden h-full sm:block" /><Separator className="sm:hidden" /></div>)}
        </CardContent>
      </Card>
      <Card role="region" aria-label="Agents"><CardHeader><CardTitle><h2>Agents</h2></CardTitle><CardDescription>Output tokens are estimated from returned UTF-8 bytes ÷ 4. Per-agent model, sessions, tokens and costs are unavailable in the recorded statistics.</CardDescription></CardHeader><CardContent>
        <Table><TableHeader><TableRow><TableHead>Agent</TableHead><TableHead>Model</TableHead><TableHead className="text-right">Sessions</TableHead><TableHead className="text-right">Operations</TableHead><TableHead className="text-right">Output tokens</TableHead><TableHead className="text-right">Est. cost</TableHead></TableRow></TableHeader><TableBody>{perDay.agents.map(agent => <TableRow key={agent}><TableCell>{agentLabel(agent)}</TableCell><TableCell>—</TableCell><TableCell className="text-right">—</TableCell><TableCell className="text-right">{formatNumber(stats.operationsPerDay.filter(row => row.agentKind === agent).reduce((sum,row) => sum + row.count,0))}</TableCell><TableCell className="text-right">—</TableCell><TableCell className="text-right">—</TableCell></TableRow>)}</TableBody></Table>
      </CardContent></Card>
      <Card role="region" aria-label="Top SQL"><CardHeader><div className="flex flex-wrap items-center gap-3"><CardTitle><h2>Top SQL</h2></CardTitle><SegmentedRadioGroup label="Top SQL order" value={sqlSort} options={[{ value: "duration", label: "Total time" }, { value: "runs", label: "Runs" }, { value: "reads", label: "Logical reads" }]} onValueChange={setSqlSort} /></div><CardDescription>Grouped by SQL hash · Logical reads: sum of recorded representative metric medians, not total executions. Coverage shown per operation and metric; missing data remains unavailable. SQL text and last run are unavailable.</CardDescription></CardHeader><CardContent><SqlTable rows={topSql} /></CardContent></Card>
      <div className="grid gap-4 md:grid-cols-2">
        <Card role="region" aria-label="Tables by logical reads"><CardHeader><CardTitle><h2>Tables by logical reads</h2></CardTitle></CardHeader><CardContent><Table><TableHeader><TableRow><TableHead>Table</TableHead><TableHead className="text-right">Logical reads</TableHead><TableHead className="text-right">Ops</TableHead></TableRow></TableHeader><TableBody>{stats.topTablesByLogicalReads.map(row => <TableRow key={row.table}><TableCell className="font-mono">{row.table}</TableCell><TableCell className="text-right">{formatNumber(row.logicalReads)}</TableCell><TableCell className="text-right">{formatNumber(row.operations)}</TableCell></TableRow>)}</TableBody></Table></CardContent></Card>
        <Card role="region" aria-label="Top waits"><CardHeader><CardTitle><h2>Top waits</h2></CardTitle></CardHeader><CardContent><Table><TableHeader><TableRow><TableHead>Wait type</TableHead><TableHead className="text-right">Total</TableHead></TableRow></TableHeader><TableBody>{stats.topWaits.map(row => <TableRow key={row.waitType}><TableCell className="font-mono">{row.waitType}</TableCell><TableCell className="text-right">{formatDuration(row.totalWaitMs)}</TableCell></TableRow>)}</TableBody></Table></CardContent></Card>
      </div>
      </div></div>
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
