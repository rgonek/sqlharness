import { expect, test } from "vitest"
import { parseRouteId } from "./routeId"

test.each([
  ["10", 10], ["0", 0], ["007", 7],
  ["abc", null], ["0x10", null], ["1e3", null], ["-1", null], ["1.5", null], [" 1", null], ["", null], ["99999999999999999999", null],
])("route id %j", (raw, expected) => expect(parseRouteId(raw)).toBe(expected))
