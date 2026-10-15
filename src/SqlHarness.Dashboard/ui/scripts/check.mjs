// Runs the three checks `npm run check` composes, concurrently instead of in sequence.
//
// They are independent: typecheck reads the tsconfig projects, oxlint reads the sources,
// vitest runs the suite. Chaining them with `&&` meant the gate waited for the slowest
// one to start only after the previous one had finished, which on the Windows gate cost
// about eleven seconds per run (measured: 9.2s + 0.6s + 19.7s serial against a ~20s floor).
//
// Each check keeps its own npm script, so running one on its own still works. This file
// only decides when they run; the exit code is non-zero when any of them fails, and the
// failing script's own output is what the reader sees.
import { spawn } from "node:child_process"

const checks = [
  { name: "typecheck", command: "npm", args: ["run", "typecheck"] },
  { name: "lint", command: "npm", args: ["run", "lint"] },
  { name: "test", command: "npm", args: ["run", "test"] },
]

const stdio = "inherit"

const run = (check) =>
  new Promise((resolve) => {
    const child = spawn(check.command, check.args, { stdio, shell: process.platform === "win32" })
    child.on("error", (error) => {
      console.error(`${check.name}: could not start (${error.message})`)
      resolve({ name: check.name, code: 1 })
    })
    child.on("close", (code, signal) => {
      if (signal) {
        console.error(`${check.name}: killed by ${signal}`)
        resolve({ name: check.name, code: 1 })
        return
      }
      resolve({ name: check.name, code: code ?? 1 })
    })
  })

const results = await Promise.all(checks.map(run))
const failed = results.filter((result) => result.code !== 0)

if (failed.length > 0) {
  console.error(`check: FAILED (${failed.map((result) => result.name).join(", ")})`)
  process.exit(1)
}