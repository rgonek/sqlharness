import { screen, waitFor } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test, vi } from "vitest"
import { renderApp, stubFetch } from "@/test/render"

test("lists profiles with the password variable name only", async () => {
  stubFetch({
    "/api/profiles": {
      status: "valid",
      message: null,
      profiles: [{
        name: "pg", engine: "postgres", server: "pg.example", database: "app_{env}", auth: "sql", sqlUser: "reader",
        passwordEnvVar: "PG_PASSWORD", sslMode: "verify-full", trustServerCertificate: false, tls: "verify-full", rootCertificate: "C:\\certs\\pg.crt",
        vars: [{ name: "env", rule: "uat|test" }], environment: "staging", mutationMode: "operator", mutationWriterKind: "sameAsReader", acknowledgeProductionYolo: false,
      }],
    },
  })
  renderApp("/profiles")

  expect(await screen.findByRole("heading", { name: "pg" })).toBeInTheDocument()
  expect(screen.getByText("app_{env}")).toBeInTheDocument()
  expect(screen.getByText("PG_PASSWORD")).toBeInTheDocument()
  expect(screen.getByText("uat|test")).toBeInTheDocument()
  expect(screen.getByText("Verify full identity")).toBeInTheDocument()
  expect(screen.getByText("root: C:\\certs\\pg.crt")).toBeInTheDocument()
  expect(screen.getByText("staging", { selector: "[data-slot=badge]" })).toBeInTheDocument()
  expect(screen.getByRole("cell", { name: /Operator approval/ })).toBeInTheDocument()
  expect(screen.getByRole("cell", { name: /Same as reader/ })).toBeInTheDocument()
  expect(screen.queryByRole("button", { name: /edit/i })).not.toBeInTheDocument()
})

test("missing file shows its state", async () => {
  stubFetch({ "/api/profiles": { status: "missing", message: null, profiles: [] } })
  renderApp("/profiles")
  expect(await screen.findByText(/No targets.json/)).toBeInTheDocument()
})

test("invalid file shows the server message", async () => {
  stubFetch({ "/api/profiles": { status: "invalid", message: "targets.json could not be read. Run `sqlharness doctor` for details.", profiles: [] } })
  renderApp("/profiles")
  expect(await screen.findByText(/could not be read/)).toBeInTheDocument()
})

test.each(["yolo", "future-mode"])("keeps unknown profile values readable: %s", async mode => {
  stubFetch({ "/api/profiles": { status: "valid", message: null, profiles: [{
    name: "future", engine: "postgres", server: "localhost", database: "app", auth: "sql", sqlUser: null,
    passwordEnvVar: null, sslMode: null, trustServerCertificate: false, tls: "future-tls", rootCertificate: null,
    vars: [], environment: null, mutationMode: mode, mutationWriterKind: "future-writer", acknowledgeProductionYolo: false,
  }] } })
  renderApp("/profiles")
  expect(await screen.findByText("future-tls")).toBeInTheDocument()
  expect(screen.getByText("future-writer")).toBeInTheDocument()
  if (mode === "yolo") expect(screen.getByRole("alert")).toHaveTextContent("YOLO DML runs without approval")
  else expect(screen.getByText(mode)).toBeInTheDocument()
})

test("edits an existing profile while retaining inherited mode and writer", async () => {
  const user = userEvent.setup()
  const editableProfile = { server: "localhost", database: "app", vars: {}, auth: "sql", sqlUser: "reader", passwordEnvVar: "PASS", engine: "postgres", mutations: { mode: null, writer: { sameAsReader: true }, acknowledgeProductionYolo: false } }
  const response = { status: "valid", message: null, revision: "rev1", editingSupported: true, editingUnavailableReason: null, profiles: [{ name: "pg", engine: "postgres", server: "localhost", database: "app", auth: "sql", sqlUser: "reader", passwordEnvVar: "PASS", sslMode: null, trustServerCertificate: false, tls: "require", rootCertificate: null, vars: [], environment: null, mutationMode: "operator", mutationWriterKind: "sameAsReader", acknowledgeProductionYolo: false, configuredMode: null, effectiveMode: "operator", modeSource: "global", writerConfigured: true, productionAcknowledgementRequired: false, editableProfile }] }
  let body: unknown
  stubFetch({ "/api/profiles": response, "PUT /api/profiles": (_url: URL, init?: RequestInit) => {
    body = JSON.parse(init?.body as string)
    return new Response(JSON.stringify(response), { headers: { "Content-Type": "application/json" } })
  } })
  renderApp("/profiles")
  await user.click(await screen.findByRole("button", { name: "Edit" }))
  expect(screen.getByLabelText("Mutation mode")).toHaveValue("inherit")
  expect(screen.getByLabelText("Writer")).toHaveValue("sameAsReader")
  await user.clear(screen.getByLabelText("Server"))
  await user.type(screen.getByLabelText("Server"), "newhost")
  await user.click(screen.getByRole("button", { name: "Save" }))
  await waitFor(() => expect(body).toMatchObject({ name: "pg", expectedRevision: "rev1", profile: { server: "newhost", mutations: { writer: { sameAsReader: true } } } }))
  expect((body as { profile: { mutations: { mode?: string } } }).profile.mutations.mode).toBeUndefined()
})


test("retains edits after conflict and protects dirty selection", async () => {
  const user = userEvent.setup()
  const editableProfile = { server: "host", database: "db", vars: { env: "test|uat" }, auth: "sql", sqlUser: "reader", passwordEnvVar: "PASS", engine: "postgres", environment: "production", mutations: { mode: null, writer: null, acknowledgeProductionYolo: false } }
  const view = { name: "pg", engine: "postgres", server: "host", database: "db", auth: "sql", sqlUser: "reader", passwordEnvVar: "PASS", sslMode: null, trustServerCertificate: false, tls: "require", rootCertificate: null, vars: [{ name: "env", rule: "test|uat" }], environment: "production", mutationMode: "yolo", mutationWriterKind: null, acknowledgeProductionYolo: false, configuredMode: null, effectiveMode: "yolo", modeSource: "global", writerConfigured: false, productionAcknowledgementRequired: true, editableProfile }
  stubFetch({ "/api/profiles": { status: "valid", message: null, revision: "r", editingSupported: true, profiles: [view, { ...view, name: "other" }] }, "PUT /api/profiles": () => new Response(JSON.stringify({ error: "Profiles changed; reload before saving." }), { status: 409 }) })
  const { router } = renderApp("/profiles")
  await user.click(await screen.findByRole("button", { name: "Edit" }))
  expect(screen.getByText(/Mutations cannot run until/)).toBeInTheDocument()
  expect(screen.getByText(/Production YOLO requires acknowledgement/)).toBeInTheDocument()
  expect(screen.getByLabelText("Variable 1 rule")).toHaveValue("test|uat")
  await user.clear(screen.getByLabelText("Server"))
  await user.type(screen.getByLabelText("Server"), "modified")
  const confirm = vi.fn().mockReturnValue(false)
  vi.stubGlobal("confirm", confirm)
  await user.click(screen.getByRole("button", { name: /other/ }))
  expect(confirm).toHaveBeenCalled()
  void router.navigate({ to: "/stats" })
  await waitFor(() => expect(confirm).toHaveBeenCalledTimes(2))
  expect(router.state.location.pathname).toBe("/profiles")
  expect(screen.getByLabelText("Server")).toHaveValue("modified")
  await user.click(screen.getByRole("button", { name: "Save" }))
  expect(await screen.findByRole("alert")).toHaveTextContent("Profiles changed")
  expect(screen.getByLabelText("Server")).toHaveValue("modified")
  confirm.mockReturnValue(true)
  await user.click(screen.getByRole("button", { name: "Cancel" }))
  expect(screen.queryByLabelText("Server")).not.toBeInTheDocument()
  vi.unstubAllGlobals()
})


test("shows field validation and removes inactive writer credentials on save", async () => {
  const user = userEvent.setup()
  const editableProfile = { server: "host", database: "db", vars: {}, auth: "sql", sqlUser: "reader", passwordEnvVar: "PASS", engine: "sqlserver", mutations: { mode: "operator", writer: { sqlUser: "writer", passwordEnvVar: "WRITER_PASS", sameAsReader: false }, acknowledgeProductionYolo: false } }
  const view = { name: "pg", engine: "sqlserver", server: "host", database: "db", auth: "sql", sqlUser: "reader", passwordEnvVar: "PASS", sslMode: null, trustServerCertificate: false, tls: "verify", rootCertificate: null, vars: [], environment: null, mutationMode: "operator", mutationWriterKind: "separate", acknowledgeProductionYolo: false, configuredMode: "operator", effectiveMode: "operator", modeSource: "profile", writerConfigured: true, productionAcknowledgementRequired: false, editableProfile }
  let submitted: { profile: { mutations: { writer: unknown } } } | undefined
  stubFetch({ "/api/profiles": { status: "valid", message: null, revision: "r", editingSupported: true, profiles: [view] }, "PUT /api/profiles": (_url: URL, init?: RequestInit) => { submitted = JSON.parse(init!.body as string); return new Response(JSON.stringify({ errors: [{ field: "server", message: "Server is required." }] }), { status: 400 }) } })
  renderApp("/profiles")
  await user.click(await screen.findByRole("button", { name: "Edit" }))
  expect(screen.getByLabelText("Server")).toHaveFocus()
  expect(screen.getByLabelText("Writer SQL user")).toHaveValue("writer")
  await user.selectOptions(screen.getByLabelText("Writer"), "sameAsReader")
  expect(screen.queryByLabelText("Writer SQL user")).not.toBeInTheDocument()
  await user.selectOptions(screen.getByLabelText("Writer"), "separate")
  expect(screen.getByLabelText("Writer SQL user")).toHaveValue("writer")
  await user.selectOptions(screen.getByLabelText("Writer"), "sameAsReader")
  await user.click(screen.getByRole("button", { name: "Save" }))
  expect(await screen.findByText("Server is required.")).toBeInTheDocument()
  expect(screen.getByLabelText("Server")).toHaveAttribute("aria-invalid", "true")
  expect(submitted?.profile.mutations.writer).toEqual({ sameAsReader: true })
})

test("edits connection, TLS and variable rules and retains them on server failure", async () => {
  const user = userEvent.setup()
  const editableProfile = { server: "host", database: "db", vars: {}, auth: "integrated", engine: "sqlserver", mutations: { mode: "never", writer: { sameAsReader: true } } }
  const view = { name: "pg", engine: "sqlserver", server: "host", database: "db", auth: "integrated", sqlUser: null, passwordEnvVar: null, sslMode: null, trustServerCertificate: false, tls: "verify", rootCertificate: null, vars: [], environment: null, mutationMode: "never", mutationWriterKind: "sameAsReader", acknowledgeProductionYolo: false, configuredMode: "never", effectiveMode: "never", modeSource: "profile", writerConfigured: true, productionAcknowledgementRequired: false, editableProfile }
  let submitted: { profile: { vars: Record<string, string>; mutations: { mode?: string; writer: unknown } } } | undefined
  stubFetch({ "/api/profiles": { status: "valid", message: null, revision: "r", editingSupported: true, profiles: [view] }, "PUT /api/profiles": (_url: URL, init?: RequestInit) => { submitted = JSON.parse(init!.body as string); return new Response("failed", { status: 500 }) } })
  renderApp("/profiles")
  await user.click(await screen.findByRole("button", { name: "Edit" }))
  await user.selectOptions(screen.getByLabelText("Engine"), "postgres")
  await user.selectOptions(screen.getByLabelText("Authentication"), "sql")
  await user.selectOptions(screen.getByLabelText("TLS mode"), "verify-full")
  await user.type(screen.getByLabelText("Root certificate"), "old.pem")
  await user.selectOptions(screen.getByLabelText("Engine"), "sqlserver")
  await user.selectOptions(screen.getByLabelText("Engine"), "postgres")
  expect(screen.getByLabelText("Root certificate")).toHaveValue("")
  expect(screen.getByLabelText("TLS mode")).toHaveValue("")
  await user.selectOptions(screen.getByLabelText("TLS mode"), "verify-full")
  await user.type(screen.getByLabelText("Root certificate"), "ca.pem")
  await user.selectOptions(screen.getByLabelText("Mutation mode"), "inherit")
  expect(screen.getByLabelText("Writer")).toHaveValue("sameAsReader")
  await user.click(screen.getByRole("button", { name: "Add variable" }))
  await user.type(screen.getByLabelText("Variable 1 name"), "env")
  await user.type(screen.getByLabelText("Variable 1 rule"), "test|uat")
  await user.click(screen.getByRole("button", { name: "Save" }))
  expect(await screen.findByRole("alert")).toHaveTextContent("500")
  expect(screen.getByLabelText("TLS mode")).toHaveValue("verify-full")
  expect(screen.getByLabelText("Authentication")).toHaveValue("sql")
  expect(screen.getByLabelText("Root certificate")).toHaveValue("ca.pem")
  expect(screen.getByLabelText("Variable 1 rule")).toHaveValue("test|uat")
  expect(submitted?.profile.vars).toEqual({ env: "test|uat" })
  expect(submitted?.profile.mutations).toEqual({ writer: { sameAsReader: true } })
})


test("explains when profile editing is unavailable", async () => {
  stubFetch({ "/api/profiles": { status: "valid", message: null, revision: "r", editingSupported: false, editingUnavailableReason: "Editing is unavailable on this platform.", profiles: [{ name: "pg", engine: "postgres", server: "host", database: "db", auth: "sql", sqlUser: null, passwordEnvVar: null, sslMode: null, trustServerCertificate: false, tls: "require", rootCertificate: null, vars: [], environment: null, mutationMode: "never", mutationWriterKind: null, acknowledgeProductionYolo: false }] } })
  renderApp("/profiles")
  expect(await screen.findByText("Editing is unavailable on this platform.")).toBeInTheDocument()
  expect(screen.queryByRole("button", { name: "Edit" })).not.toBeInTheDocument()
})
