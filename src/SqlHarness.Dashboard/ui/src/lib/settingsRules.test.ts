import { expect, test } from "vitest"
import type { Settings } from "@/api/types"
import { isDestructive } from "@/lib/settingsRules"

const make = (enabled: boolean, maxAgeDays = 30, maxSizeMb = 500): Settings => ({
  journal: { enabled: true, storeSensitive: false, retention: { enabled, maxAgeDays, maxSizeMb } },
  dashboard: { autoStart: false, port: 47800, idleShutdownHours: 8 },
  compare: { requireContext: true },
})

test("enabling retention is destructive", () => expect(isDestructive(make(false), make(true))).toBe(true))
test("lowering max age with retention on is destructive", () => expect(isDestructive(make(true), make(true, 7))).toBe(true))
test("lowering max size with retention on is destructive", () => expect(isDestructive(make(true), make(true, 30, 100))).toBe(true))
test("lowering limits with retention off is not destructive", () => expect(isDestructive(make(false), make(false, 7, 100))).toBe(false))
test("raising limits is not destructive", () => expect(isDestructive(make(true), make(true, 60, 1000))).toBe(false))
test("disabling retention is not destructive", () => expect(isDestructive(make(true), make(false))).toBe(false))
