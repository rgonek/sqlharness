import { useState } from "react"
import { Link } from "@tanstack/react-router"
import { useProfiles } from "@/api/queries"
import { ErrorState } from "@/components/ErrorState"
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"
import { Badge } from "@/components/ui/badge"
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Skeleton } from "@/components/ui/skeleton"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"

export function ProfilesPage() {
  const query = useProfiles()
  const [selected, setSelected] = useState<string | null>(null)
  const [search, setSearch] = useState("")
  if (query.error) return <ErrorState error={query.error} />
  if (query.isPending) return <Skeleton className="h-64 w-full" />
  const { status, profiles, message } = query.data
  const filtered = profiles.filter(profile => [profile.name, profile.server, profile.database].some(value => value.toLowerCase().includes(search.toLowerCase())))
  const profile = filtered.find(item => item.name === selected) ?? filtered[0]
  const connection = profile ? [
    ["Engine", profile.engine], ["Server", profile.server], ["Database", profile.database],
    ["Authentication", profile.sqlUser ? `${profile.auth} (${profile.sqlUser})` : profile.auth],
    ["Password variable", profile.passwordEnvVar ?? "—"], ["TLS", profile.tls],
    ...(profile.rootCertificate ? [["Root certificate", `root: ${profile.rootCertificate}`]] : []),
  ] : []
  return <div className="profiles-layout">
    <aside className="flex min-w-0 flex-col gap-3" aria-label="Profiles">
      <h2 className="text-sm font-medium">Profiles</h2>
      <Input aria-label="Search profiles" placeholder="Search profiles" value={search} onChange={event => setSearch(event.target.value)} />
      <div className="flex flex-col gap-1">{filtered.map(item => <Button key={item.name} variant={profile?.name === item.name ? "secondary" : "ghost"} className="h-auto min-w-0 flex-col items-stretch gap-1 px-3 py-2 text-left" aria-pressed={profile?.name === item.name} onClick={() => setSelected(item.name)}>
        <span className="flex items-center justify-between gap-2"><span className="truncate">{item.name}</span><span className="text-muted-foreground">{item.environment ?? "—"}</span></span>
        <span className="truncate text-xs font-normal text-muted-foreground">{item.engine} · {item.server}</span>
      </Button>)}</div>
      {status === "valid" && !filtered.length && <p className="text-sm text-muted-foreground">No profile matches.</p>}
    </aside>
    <div className="flex min-w-0 flex-col gap-5">
      <header className="flex flex-col gap-1"><h1>Profiles</h1><p className="text-sm text-muted-foreground">Read-only view of targets.json. Passwords stay in their environment variables and are never read.</p></header>
      {status === "missing" && <p className="text-sm text-muted-foreground">No targets.json in the SQLHarness home.</p>}
      {status === "invalid" && <Alert variant="destructive"><AlertTitle>targets.json is invalid</AlertTitle><AlertDescription>{message}</AlertDescription></Alert>}
      {status === "valid" && profile && <>
        <header className="flex flex-col gap-2"><div className="flex flex-wrap items-center gap-3"><h2 className="text-xl font-semibold">{profile.name}</h2><Badge variant="outline">{profile.environment ?? "no environment"}</Badge><Link to="/stats" className="ml-auto text-sm underline">Usage overview →</Link></div><p className="break-all text-sm text-muted-foreground">{profile.engine} · {profile.server} / {profile.database}</p></header>
        <Card className="profile-section" role="region" aria-label="Connection"><CardHeader><CardTitle><h3>Connection</h3></CardTitle></CardHeader><CardContent className="flex flex-col gap-3"><Table><TableBody>{connection.map(([label,value]) => <TableRow key={label}><TableHead scope="row" className="w-40">{label}</TableHead><TableCell className="whitespace-normal break-all">{value}</TableCell></TableRow>)}</TableBody></Table>{profile.database.includes("{") && <p className="text-xs text-muted-foreground">Database template · resolved per operation from variables</p>}</CardContent></Card>
        <Card className="profile-section" role="region" aria-label="Mutations"><CardHeader><CardTitle><h3>Mutations</h3></CardTitle></CardHeader><CardContent className="flex flex-col gap-3"><Table><TableBody><TableRow><TableHead scope="row" className="w-40">Mode</TableHead><TableCell>{profile.mutationMode || "never"}</TableCell></TableRow><TableRow><TableHead scope="row">Writer</TableHead><TableCell>{profile.mutationWriterKind ?? "—"}</TableCell></TableRow></TableBody></Table>{profile.mutationMode === "yolo" && <p className="text-sm text-destructive">Mutations run without approval{profile.environment ? ` on ${profile.environment}` : ""}.</p>}</CardContent></Card>
        <Card className="profile-section" role="region" aria-label="Variables"><CardHeader><CardTitle><h3>Variables</h3></CardTitle></CardHeader><CardContent className="flex flex-col gap-3">{profile.vars.length ? <Table><TableHeader><TableRow><TableHead>Name</TableHead><TableHead>Rule</TableHead></TableRow></TableHeader><TableBody>{profile.vars.map(variable => <TableRow key={variable.name}><TableCell className="font-mono">{variable.name}</TableCell><TableCell>{variable.rule}</TableCell></TableRow>)}</TableBody></Table> : <p className="text-sm text-muted-foreground">No variables. Operations on this profile have no dimensions.</p>}</CardContent></Card>
      </>}
    </div>
  </div>
}
