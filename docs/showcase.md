# SQLHarness listing preparation

Prepared for Tiny Tool Town; **not submitted**.

## Proposed listing

- **Name:** SQLHarness
- **Tagline:** Measure whether your AI agent's SQL rewrite is actually better.
- **Repository:** https://github.com/rgonek/sqlharness
- **Releases:** https://github.com/rgonek/sqlharness/releases
- **Author:** Robert Gonek (`rgonek`)
- **Language:** C#
- **License:** MIT
- **Tags:** sql, mcp, cli, postgresql, sql-server

SQLHarness gives engineers and coding agents a repeatable way to compare SQL changes. Measure a baseline and a candidate, check result equivalence for the tested inputs, and inspect the evidence in a local dashboard. It supports SQL Server, Azure SQL, and PostgreSQL through a CLI and MCP server.

## Visual package

The README embeds three genuine dashboard screenshots generated from a synthetic local journal:

1. `images/comparison.png`: final query compared with the original.
2. `images/optimization.png`: three measured SQL versions in one session.
3. `images/statement-io.png`: the per-statement/table IO explanation.

All images show demo data, not measured benchmark results. See [the reproduction instructions](../scripts/showcase/README.md).

## Before submitting

- Publish the reviewed README and images so the directory can retrieve them from GitHub.
- Publish a verified release matching the documented features; this preparation creates a draft only.
- Check the public README image rendering and the directory's chosen screenshot, especially because the README also contains a logo.
- Add a brief personal account of why the tool was built, in the author's own words.
- Optionally record a short agent-to-dashboard walkthrough using this same demo session.

Submission is through [Tiny Tool Town's form](https://www.tinytooltown.com/submit/). This document does not authorize submitting it.
