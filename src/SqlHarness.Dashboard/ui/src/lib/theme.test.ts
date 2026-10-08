import { afterEach, expect, test, vi } from "vitest"
import { applyTheme, readThemeMode, writeThemeMode } from "@/lib/theme"

afterEach(() => {
  vi.restoreAllMocks()
  localStorage.clear()
  document.documentElement.classList.remove("dark")
})

test("mode round-trips through localStorage and defaults to system", () => {
  expect(readThemeMode()).toBe("system")
  writeThemeMode("dark")
  expect(localStorage.getItem("sqlharness.theme")).toBe("dark")
  expect(readThemeMode()).toBe("dark")
  localStorage.setItem("sqlharness.theme", "purple")
  expect(readThemeMode()).toBe("system")
})

test("storage failures fall back to system without throwing", () => {
  vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => { throw new Error("blocked") })
  vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => { throw new Error("blocked") })
  expect(() => writeThemeMode("light")).not.toThrow()
  expect(readThemeMode()).toBe("system")
})

test("applyTheme toggles the dark class", () => {
  applyTheme("dark")
  expect(document.documentElement).toHaveClass("dark")
  applyTheme("light")
  expect(document.documentElement).not.toHaveClass("dark")
  applyTheme("system") // the test setup's matchMedia reports light
  expect(document.documentElement).not.toHaveClass("dark")
})
