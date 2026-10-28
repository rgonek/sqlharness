import { screen } from "@testing-library/react"
import { expect, test } from "vitest"
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
  if (mode === "yolo") expect(screen.getByRole("alert")).toHaveTextContent("Mutations run without approval")
  else expect(screen.getByText(mode)).toBeInTheDocument()
})
