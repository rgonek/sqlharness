import { useState } from "react"
import { Bar, BarChart, CartesianGrid, XAxis, YAxis } from "recharts"
import { type StatsRange, useProfiles, useStats } from "@/api/queries"
import type { DashboardStats, KeyCount, ProfileView, SqlHashStat } from "@/api/types"
import { ErrorState } from "@/components/ErrorState"
import { Kpi } from "@/components/Kpi"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import { type ChartConfig, ChartContainer, ChartLegend, ChartLegendContent, ChartTooltip, ChartTooltipContent } from "@/components/ui/chart"
import { Label } from "@/components/ui/label"
import { Skeleton } from "@/components/ui/skeleton"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"
import { Tabs, TabsList, TabsTrigger } from "@/components/ui/tabs"
import { formatDuration, formatNumber, formatPercent, shortHash } from "@/lib/format"
import { pivotPerDay, tokenSavings } from "@/lib/stats"
import { ProfileDimensionsPanel, type DimensionCellScope } from "./ProfileDimensionsPanel"

const ranges: { value: StatsRange; label: string }[] = [
  { value: "24h", label: "24 hours" },
  { value: "7d", label: "7 days" },
  { value: "30d", label: "30 days" },
  { value: "all", label: "All time" },
]

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

export function StatsPage({ onDimensionCellSelect }: { onDimensionCellSelect?: (scope: DimensionCellScope) => void } = {}) {
  const [range, setRange] = useState<StatsRange>("7d")
  const [manualProfile, setManualProfile] = useState<{ value: string | null } | undefined>()
  const overview = useStats(range)
  const profilesQuery = useProfiles()
  const profiles = profilesQuery.data?.profiles ?? []
  const selectedProfile = manualProfile
    ? manualProfile.value
    : overview.data?.profileOperations.length
      ? overview.data.profileOperations[0].profile
      : profiles[0]?.name ?? null
  const query = useStats(range, selectedProfile)
  const profileOptions = [...new Set([
    ...profiles.map(profile => profile.name),
    ...(overview.data?.profileOperations ?? []).flatMap(item => item.profile === null ? [] : [item.profile]),
    ...(manualProfile?.value ? [manualProfile.value] : []),
  ])].sort((left, right) => left.localeCompare(right))
  const selectedProfileView = profiles.find(profile => profile.name === selectedProfile)

  return (
    <div className="space-y-4">
      <div className="flex flex-wrap items-center gap-4">
        <h1>Statistics</h1>
        <div className="grid min-w-52 gap-1.5">
          <Label htmlFor="stats-profile">Profile</Label>
          <select
            id="stats-profile"
            aria-label="Statistics profile"
            className="h-8 rounded-lg border border-input bg-background px-2 text-sm focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-ring active:bg-muted"
            value={selectedProfile === null ? "unprofiled" : `profile:${selectedProfile}`}
            onChange={event => setManualProfile({ value: event.target.value === "unprofiled" ? null : event.target.value.slice("profile:".length) })}
          >
            {profileOptions.map(name => <option key={name} value={`profile:${name}`}>{name}</option>)}
            <option value="unprofiled">No profile</option>
          </select>
        </div>
        <Tabs value={range} onValueChange={value => setRange(String(value) as StatsRange)}>
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
        <StatsContent stats={query.data} range={range} profile={selectedProfile} profileView={selectedProfileView}
          onDimensionCellSelect={onDimensionCellSelect} />
      )}
    </div>
  )
}

function StatsContent({
  stats,
  range,
  profile,
  profileView,
  onDimensionCellSelect,
}: {
  stats: DashboardStats
  range: StatsRange
  profile: string | null
  profileView: ProfileView | undefined
  onDimensionCellSelect?: (scope: DimensionCellScope) => void
}) {
  const perDay = pivotPerDay(stats.operationsPerDay)
  const perDayConfig = Object.fromEntries(
    perDay.agents.map((agent, index) => [agent, { label: agent, color: `var(--chart-${(index % 5) + 1})` }]),
  ) satisfies ChartConfig
  const total = stats.operations.reduce((sum, row) => sum + row.count, 0)

  return (
    <div className="space-y-4">
      <div className="grid grid-cols-2 gap-4 md:grid-cols-4">
        <Kpi label="Operations" value={formatNumber(total)} />
        <Kpi label="Tokens saved" value={formatPercent(tokenSavings(stats.tokens))} hint={`${formatNumber(stats.tokens.raw)} raw → ${formatNumber(stats.tokens.emitted)} emitted`} />
        <Kpi label="Operations with spills" value={formatNumber(stats.spillOperations)} />
        <Kpi label="Operations on a cold cache" value={formatNumber(stats.coldCacheOperations)} />
      </div>
      <Card>
        <CardHeader>
          <CardTitle>Operations per day (UTC)</CardTitle>
        </CardHeader>
        <CardContent>
          <ChartContainer config={perDayConfig} className="h-64 w-full">
            <BarChart data={perDay.rows}>
              <CartesianGrid vertical={false} />
              <XAxis dataKey="day" tickLine={false} axisLine={false} />
              <YAxis allowDecimals={false} tickLine={false} axisLine={false} width={32} />
              <ChartTooltip content={<ChartTooltipContent />} />
              <ChartLegend content={<ChartLegendContent />} />
              {perDay.agents.map(agent => (
                <Bar key={agent} dataKey={agent} stackId="agents" fill={`var(--color-${agent})`} />
              ))}
            </BarChart>
          </ChartContainer>
        </CardContent>
      </Card>
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
        key={profile ?? "unprofiled"}
        range={range}
        profile={profile}
        profileName={profile ?? "No profile"}
        databaseTemplate={profileView?.database}
        profileDefinitionAvailable={stats.profileDimensions.profileDefinitionAvailable}
        dimensions={stats.profileDimensions.dimensions}
        filterOptions={stats.profileDimensions.dimensions}
        targets={stats.profileDimensions.targets}
        onCellSelect={onDimensionCellSelect}
      />
    </div>
  )
}
