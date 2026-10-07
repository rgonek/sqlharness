import { describe, expect, test } from "vitest"
import { formatAge, formatDuration, formatKb, formatNumber, formatPercent, formatTimestamp, shortHash } from "./format"

describe("format", () => {
  test.each([
    [null, "—"], [undefined, "—"], [0, "0 ms"], [999, "999 ms"], [1200, "1.2 s"], [59_949, "59.9 s"], [61_000, "1 min 1 s"],
  ])("duration %s", (value, expected) => expect(formatDuration(value)).toBe(expected))

  test("numbers group thousands and render null as dash", () => {
    expect(formatNumber(1234567)).toBe("1,234,567")
    expect(formatNumber(null)).toBe("—")
  })

  test.each([[null, "—"], [512, "512 KB"], [8192, "8.0 MB"], [3 * 1024 * 1024, "3.0 GB"]])("kb %s", (value, expected) =>
    expect(formatKb(value)).toBe(expected))

  test("timestamps render in local time as yyyy-mm-dd hh:mm:ss", () => {
    const local = new Date(2026, 9, 7, 9, 5, 3)
    expect(formatTimestamp(local.toISOString())).toBe("2026-10-07 09:05:03")
    expect(formatTimestamp(null)).toBe("—")
  })

  test("age is relative to now", () => {
    const now = Date.parse("2026-10-07T10:00:00Z")
    expect(formatAge("2026-10-07T09:59:48Z", now)).toBe("12 s ago")
    expect(formatAge("2026-10-07T09:55:00Z", now)).toBe("5 min ago")
    expect(formatAge("2026-10-07T07:00:00Z", now)).toBe("3 h ago")
    expect(formatAge("2026-10-05T10:00:00Z", now)).toBe("2 d ago")
  })

  test("hashes are shortened without the algorithm prefix", () => {
    expect(shortHash("sha256:0123456789abcdef0123")).toBe("0123456789ab")
    expect(shortHash(null)).toBe("—")
  })

  test("percent", () => {
    expect(formatPercent(0.9)).toBe("90%")
    expect(formatPercent(null)).toBe("—")
  })
})
