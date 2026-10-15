import { expect, test } from "vitest"
import { sessionDisplayName } from "./sessionName"

test("title wins over the session key", () => {
  expect(sessionDisplayName({ title: "BSI-1 orders", sessionKey: "cli:a" })).toBe("BSI-1 orders")
  expect(sessionDisplayName({ title: null, sessionKey: "cli:a" })).toBe("cli:a")
})
