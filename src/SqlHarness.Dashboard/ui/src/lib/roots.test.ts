import { expect, test } from "vitest"
import { clientLabel, rootLabel } from "@/lib/roots"

test("file URIs show as local paths", () => {
  expect(rootLabel("file:///D:/Dev/sqlharness")).toBe("D:/Dev/sqlharness")
  expect(rootLabel("file:///home/me/work")).toBe("/home/me/work")
  expect(rootLabel("file:///D:/Projekty/Za%C5%BC%C3%B3%C5%82%C4%87%20g%C4%99%C5%9Bl%C4%85")).toBe("D:/Projekty/Zażółć gęślą")
})

test("other URIs and malformed escapes show raw", () => {
  expect(rootLabel("https://example.invalid/x")).toBe("https://example.invalid/x")
  expect(rootLabel("file:///bad%E0%A4%A")).toBe("file:///bad%E0%A4%A")
})

test("client label prefers the title", () => {
  expect(clientLabel({ clientTitle: "Claude Code", clientName: "claude-code", clientVersion: "2.1.0" })).toBe("Claude Code 2.1.0")
  expect(clientLabel({ clientTitle: null, clientName: "claude-code", clientVersion: null })).toBe("claude-code")
  expect(clientLabel({ clientTitle: null, clientName: null, clientVersion: "1" })).toBeNull()
})