import { useState } from "react"
import { FieldErrorsError } from "@/api/client"
import { useSettings, useUpdateProfile } from "@/api/queries"
import type { ProfileView, TargetProfile } from "@/api/types"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Field, FieldError, FieldGroup, FieldLabel } from "@/components/ui/field"

export function ProfileEditForm({ profile, revision, onDirty, onCancel, onSaved, onPending }: { profile: ProfileView; revision: string; onDirty: (dirty: boolean) => void; onCancel: () => void; onSaved: () => void; onPending: (pending: boolean) => void }) {
  const [original] = useState(() => structuredClone(profile.editableProfile!))
  const [originalRevision] = useState(revision)
  const [draft, setDraft] = useState<TargetProfile>(() => structuredClone(original))
  const [variables, setVariables] = useState(() => Object.entries(original.vars).map(([name, rule]) => ({ name, rule })))
  const [separateWriter, setSeparateWriter] = useState(() => ({ sqlUser: original.mutations?.writer?.sqlUser ?? "", passwordEnvVar: original.mutations?.writer?.passwordEnvVar ?? "" }))
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [error, setError] = useState("")
  const save = useUpdateProfile()
  const settings = useSettings()
  const mode = draft.mutations?.mode ?? "inherit"
  const writer = draft.mutations?.writer
  const writerKind = !writer ? "unconfigured" : writer.sameAsReader ? "sameAsReader" : "separate"
  const globalMode = settings.data?.settings?.mutations?.defaultMode ?? (profile.modeSource === "global" ? profile.effectiveMode : undefined)
  const effective = mode === "inherit" ? globalMode : mode
  const change = (next: TargetProfile, nextVars = variables) => { setDraft(next); setVariables(nextVars); onDirty(JSON.stringify(next) !== JSON.stringify(original) || JSON.stringify(nextVars) !== JSON.stringify(Object.entries(original.vars).map(([name, rule]) => ({ name, rule })))) }
  const mutation = (patch: NonNullable<TargetProfile["mutations"]>) => change({ ...draft, mutations: { ...draft.mutations, ...patch } })
  const input = (key: keyof TargetProfile, label: string) => <Field data-invalid={!!errors[key]}><FieldLabel htmlFor={`profile-${key}`}>{label}</FieldLabel><Input autoFocus={key === "server"} id={`profile-${key}`} value={String(draft[key] ?? "")} aria-invalid={!!errors[key]} aria-describedby={errors[key] ? `error-${key}` : undefined} onChange={e => change({ ...draft, [key]: e.target.value })} />{errors[key] && <FieldError id={`error-${key}`}>{errors[key]}</FieldError>}</Field>
  const renderedErrorFields = new Set(["engine", "server", "database", "auth", "sqlUser", "passwordEnvVar", "environment", "mutations", "vars", ...(draft.engine === "postgres" ? ["sslMode", "rootCertificate"] : [])])
  const otherErrors = Object.entries(errors).filter(([field]) => !renderedErrorFields.has(field))
  const select = (id: string, label: string, value: string, options: [string, string][], onChange: (value: string) => void, field = id) => <Field data-invalid={!!errors[field]}><FieldLabel htmlFor={`profile-${id}`}>{label}</FieldLabel><select id={`profile-${id}`} value={value} aria-invalid={!!errors[field]} aria-describedby={errors[field] ? `error-${id}` : undefined} onChange={e => onChange(e.target.value)}>{options.map(([v, l]) => <option key={v} value={v}>{l}</option>)}</select>{errors[field] && <FieldError id={`error-${id}`}>{errors[field]}</FieldError>}</Field>
  async function submit(e: React.FormEvent) {
    e.preventDefault(); setError(""); setErrors({})
    if (variables.some(v => !v.name) || new Set(variables.map(v => v.name)).size !== variables.length) { setErrors({ vars: "Variable names must be non-empty and unique." }); return }
    const mutations = { ...draft.mutations, mode: mode === "inherit" ? undefined : mode, writer: writerKind === "unconfigured" ? undefined : writerKind === "sameAsReader" ? { sameAsReader: true } : { sqlUser: writer?.sqlUser ?? "", passwordEnvVar: writer?.passwordEnvVar ?? "", sameAsReader: false } }
    onPending(true)
    try { await save.mutateAsync({ name: profile.name, expectedRevision: originalRevision, profile: { ...draft, vars: Object.fromEntries(variables.map(v => [v.name, v.rule])), mutations } }); onDirty(false); onSaved() }
    catch (e) { if (e instanceof FieldErrorsError) setErrors(Object.fromEntries(e.errors.map(item => [item.field.replace(/^profile\./, ""), item.message]))); else setError(e instanceof Error ? e.message : "Could not save profiles.") }
    finally { onPending(false) }
  }
  return <form onSubmit={submit} className="flex flex-col gap-4"><h3>Edit {profile.name}</h3><p>Profile name cannot be changed. Only environment variable names are saved; their values are never read.</p>
    <fieldset disabled={save.isPending}><FieldGroup>
      {select("engine", "Engine", draft.engine || "sqlserver", [["sqlserver", "SQL Server"], ["postgres", "PostgreSQL"]], v => change({ ...draft, engine: v, trustServerCertificate: false, sslMode: null, rootCertificate: null }))}
      {input("server", "Server")}{input("database", "Database or template")}
      {select("auth", "Authentication", draft.auth, [["sql", "SQL user"], ["integrated", "Integrated"], ["azure-cli", "Azure CLI"], ["ad-default", "AD default"]], v => change({ ...draft, auth: v, ...(v !== "sql" ? { sqlUser: null, passwordEnvVar: null } : {}) }))}
      {input("sqlUser", "SQL user")}{input("passwordEnvVar", "Password environment variable")}
      {draft.engine === "postgres" && <>{select("sslMode", "TLS mode", draft.sslMode ?? "", [["", "Legacy default"], ["disable", "Disable"], ["allow", "Allow"], ["prefer", "Prefer"], ["require", "Require"], ["verify-ca", "Verify CA"], ["verify-full", "Verify full"]], v => change({ ...draft, sslMode: v || null }))}{input("rootCertificate", "Root certificate")}</>}
      <Field><label><input type="checkbox" checked={draft.trustServerCertificate ?? false} onChange={e => change({ ...draft, trustServerCertificate: e.target.checked })} /> Trust server certificate (legacy TLS setting for PostgreSQL)</label></Field>
      {input("environment", "Environment")}
      <fieldset><legend>Variables</legend>{variables.map((v, i) => <div key={i} className="flex gap-2"><Input aria-label={`Variable ${i + 1} name`} value={v.name} onChange={e => change(draft, variables.map((item, n) => n === i ? { ...item, name: e.target.value } : item))} /><Input aria-label={`Variable ${i + 1} rule`} value={v.rule} onChange={e => change(draft, variables.map((item, n) => n === i ? { ...item, rule: e.target.value } : item))} /><Button type="button" variant="outline" onClick={() => change(draft, variables.filter((_, n) => n !== i))}>Remove variable {i + 1}</Button></div>)}<Button type="button" variant="outline" onClick={() => change(draft, [...variables, { name: "", rule: "" }])}>Add variable</Button>{errors.vars && <FieldError>{errors.vars}</FieldError>}</fieldset>
      {select("mode", "Mutation mode", mode, [["inherit", "Inherit global"], ["never", "Never"], ["operator", "Operator approval"], ["yolo", "YOLO"]], v => mutation({ mode: v === "inherit" ? null : v }), "mutations")}
      <p>Global mode: {globalMode ?? "unavailable"}. Effective mode: {effective ?? "unavailable"}. Source: {mode === "inherit" ? "global" : "profile"}.</p>
      {select("writer", "Writer", writerKind, [["unconfigured", "Unconfigured"], ["sameAsReader", "Same as reader"], ["separate", "Separate writer"]], v => mutation({ writer: v === "unconfigured" ? null : v === "sameAsReader" ? { sameAsReader: true } : { sameAsReader: false, ...separateWriter } }), "mutations")}
      {writerKind === "separate" && <><Field><FieldLabel htmlFor="writer-user">Writer SQL user</FieldLabel><Input id="writer-user" value={writer?.sqlUser ?? ""} onChange={e => { setSeparateWriter({ ...separateWriter, sqlUser: e.target.value }); mutation({ writer: { ...writer, sqlUser: e.target.value } }) }} /></Field><Field><FieldLabel htmlFor="writer-password">Writer password environment variable</FieldLabel><Input id="writer-password" value={writer?.passwordEnvVar ?? ""} onChange={e => { setSeparateWriter({ ...separateWriter, passwordEnvVar: e.target.value }); mutation({ writer: { ...writer, passwordEnvVar: e.target.value } }) }} /></Field></>}
      {effective !== "never" && writerKind === "unconfigured" && <p role="status">Mutations cannot run until a writer is configured.</p>}
      <p>YOLO skips operator approval only for DML. Schema, privilege and opaque changes still require approval.</p>
      <Field><label><input type="checkbox" checked={draft.mutations?.acknowledgeProductionYolo ?? false} onChange={e => mutation({ acknowledgeProductionYolo: e.target.checked })} /> Acknowledge production YOLO</label></Field>
      {effective === "yolo" && /^(prod|production|staging)$/i.test(draft.environment ?? "") && !draft.mutations?.acknowledgeProductionYolo && <p role="status">Production YOLO requires acknowledgement before DML can execute.</p>}
      {otherErrors.length > 0 && <div role="status" aria-label="Other validation errors"><p>Other validation errors:</p><ul>{otherErrors.map(([field, message]) => <li key={field}>{field}: {message}</li>)}</ul></div>}{error && <p role="alert">{error}</p>}
    </FieldGroup></fieldset><div className="flex gap-2"><Button type="submit" disabled={save.isPending}>Save</Button><Button type="button" variant="outline" onClick={onCancel} disabled={save.isPending}>Cancel</Button></div>
  </form>
}
