# SQLHarness agent contract

## Repository changes

- **Plans:** read [plans/README.md](plans/README.md) before plan-backed work; update the plan and index when work lands.
- **Security:** protect resolved credentials and persistent-mutation classification. Keep caller SQL, parameters, plans, results and target names readable; bound output size instead of masking them.
- **Documentation:** README serves humans. Keep interface contracts in tool descriptions/schemas and capabilities; update each affected surface when behavior changes.
- **Verification:** before declaring implementation complete, follow [verification](docs/agents/verification.md). Documentation-only changes use its scoped checks.

## Conditional context

- **Database execution:** before using SQLHarness against a database, read [database work](docs/agents/database-work.md) for scope, approval and failure boundaries, and [the user skill](skills/sqlharness/SKILL.md) for the evidence workflow.
- **MCP:** before changing server startup, request targets or file inputs, read [the MCP contract](docs/mcp.md).

## User-facing skills

The bundled skill and derived use-case skills serve ordinary users without repository access. Keep workflow, task decisions and evidence completion criteria in the skill; put interface details in tool descriptions/schemas and runtime policy in capabilities. Each meaning has one authoritative home. Essential user guidance must be reachable through the installed skill or interface. Minimize tokens across these surfaces.
