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
        passwordEnvVar: "PG_PASSWORD", sslMode: "verify-full", trustServerCertificate: false, rootCertificate: null,
        vars: [{ name: "env", rule: "uat|test" }],
      }],
    },
  })
  renderApp("/profiles")

  expect(await screen.findByRole("cell", { name: "pg" })).toBeInTheDocument()
  expect(screen.getByText("app_{env}")).toBeInTheDocument()
  expect(screen.getByText("PG_PASSWORD")).toBeInTheDocument()
  expect(screen.getByText("env: uat|test")).toBeInTheDocument()
  expect(screen.queryByRole("button", { name: /edit/i })).not.toBeInTheDocument()
})

test("missing and invalid files show their states", async () => {
  stubFetch({ "/api/profiles": { status: "missing", message: null, profiles: [] } })
  renderApp("/profiles")
  expect(await screen.findByText(/No targets.json/)).toBeInTheDocument()
})

test("invalid file shows the server message", async () => {
  stubFetch({ "/api/profiles": { status: "invalid", message: "targets.json could not be read. Run `sqlharness doctor` for details.", profiles: [] } })
  renderApp("/profiles")
  expect(await screen.findByText(/could not be read/)).toBeInTheDocument()
})
