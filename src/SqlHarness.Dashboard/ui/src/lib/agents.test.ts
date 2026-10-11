import { expect, test } from "vitest"
import { agentLabel, agentTabs } from "@/lib/agents"

test("known kinds have product labels and unknown kinds are shown raw", () => {
  expect(agentLabel("claude")).toBe("Claude Code")
  expect(agentLabel("codex")).toBe("Codex")
  expect(agentLabel("copilot")).toBe("Copilot")
  expect(agentLabel("opencode")).toBe("opencode")
  expect(agentLabel("grok")).toBe("Grok")
  expect(agentLabel("unknown")).toBe("Unknown")
  expect(agentLabel("cursor")).toBe("cursor")
})

test("tabs list All plus only present kinds, known first, client names next, unknown last", () => {
  expect(agentTabs(["codex"]).map(t => t.label)).toEqual(["All", "Codex"])
  expect(agentTabs(["copilot", "claude"]).map(t => t.label)).toEqual(["All", "Claude Code", "Copilot"])
  expect(agentTabs(["unknown", "zed", "cursor", "codex"]).map(t => t.value)).toEqual(["all", "codex", "cursor", "zed", "unknown"])
  expect(agentTabs([]).map(t => t.value)).toEqual(["all"])
})
