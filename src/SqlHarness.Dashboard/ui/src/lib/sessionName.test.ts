import { expect, test } from "vitest"
import { session } from "@/test/fixtures"
import { sessionDisplayName } from "@/lib/sessionName"

test("title wins over the session key", () => {
  expect(sessionDisplayName(session({ title: "BSI-1 orders", sessionKey: "cli:a" }))).toBe("BSI-1 orders")
  expect(sessionDisplayName(session({ title: null, agentTitle: null, sessionKey: "cli:a" }))).toBe("cli:a")
})

test("user title wins over agent title and session key", () => {
  expect(sessionDisplayName(session({ title: "Saved title", agentTitle: "Agent title" }))).toBe("Saved title")
})

test("agent title is used when there is no user title", () => {
  expect(sessionDisplayName(session({ agentTitle: "Optimize orders" }))).toBe("Optimize orders")
})

test("session key is the final fallback", () => {
  expect(sessionDisplayName(session({ sessionKey: "cli:abc", agentTitle: null }))).toBe("cli:abc")
})
