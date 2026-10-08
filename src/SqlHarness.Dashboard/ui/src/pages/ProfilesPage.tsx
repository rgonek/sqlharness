import { useProfiles } from "@/api/queries"
import { ErrorState } from "@/components/ErrorState"
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert"
import { Skeleton } from "@/components/ui/skeleton"
import { Table, TableBody, TableCell, TableHead, TableHeader, TableRow } from "@/components/ui/table"

export function ProfilesPage() {
  const query = useProfiles()
  if (query.error) return <ErrorState error={query.error} />
  if (query.isPending) return <Skeleton className="h-64 w-full" />
  const { status, profiles, message } = query.data
  return (
    <div className="space-y-4">
      <h1>Profiles</h1>
      <p className="text-sm text-muted-foreground">Read-only view of targets.json. Passwords stay in their environment variables and are never read.</p>
      {status === "missing" && <p className="text-sm text-muted-foreground">No targets.json in the SQLHarness home.</p>}
      {status === "invalid" && (
        <Alert variant="destructive">
          <AlertTitle>targets.json is invalid</AlertTitle>
          <AlertDescription>{message}</AlertDescription>
        </Alert>
      )}
      {profiles.length > 0 && (
        <div className="overflow-x-auto">
          <Table>
            <TableHeader>
              <TableRow>
                <TableHead>Name</TableHead>
                <TableHead>Engine</TableHead>
                <TableHead>Server</TableHead>
                <TableHead>Database</TableHead>
                <TableHead>Auth</TableHead>
                <TableHead>Password variable</TableHead>
                <TableHead>TLS</TableHead>
                <TableHead>Variables</TableHead>
              </TableRow>
            </TableHeader>
            <TableBody>
              {profiles.map(profile => (
                <TableRow key={profile.name}>
                  <TableCell>{profile.name}</TableCell>
                  <TableCell>{profile.engine}</TableCell>
                  <TableCell>{profile.server}</TableCell>
                  <TableCell>{profile.database}</TableCell>
                  <TableCell>{profile.sqlUser ? `${profile.auth} (${profile.sqlUser})` : profile.auth}</TableCell>
                  <TableCell>{profile.passwordEnvVar ?? "—"}</TableCell>
                  <TableCell>
                    <div>{profile.tls}</div>
                    {profile.rootCertificate && <div>{`root: ${profile.rootCertificate}`}</div>}
                  </TableCell>
                  <TableCell>
                    {profile.vars.map(variable => (
                      <div key={variable.name}>{`${variable.name}: ${variable.rule}`}</div>
                    ))}
                  </TableCell>
                </TableRow>
              ))}
            </TableBody>
          </Table>
        </div>
      )}
    </div>
  )
}
