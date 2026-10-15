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
  // Neither spy form works in both engines: happy-dom's localStorage is not
  // Storage.prototype-backed, and jsdom's is not spy-able per instance. Replace
  // the storage object itself, which is what "storage is blocked" means.
  const blocked = {
    getItem: () => { throw new Error("blocked") },
    setItem: () => { throw new Error("blocked") },
    removeItem: () => { throw new Error("blocked") },
    clear: () => { throw new Error("blocked") },
    key: () => null,
    length: 0,
  }
  const original = Object.getOwnPropertyDescriptor(window, "localStorage")
  Object.defineProperty(window, "localStorage", { value: blocked, configurable: true })
  try {
    expect(() => writeThemeMode("light")).not.toThrow()
    expect(readThemeMode()).toBe("system")
  }
  finally {
    if (original) Object.defineProperty(window, "localStorage", original)
  }
})

test("applyTheme toggles the dark class", () => {
  applyTheme("dark")
  expect(document.documentElement).toHaveClass("dark")
  applyTheme("light")
  expect(document.documentElement).not.toHaveClass("dark")
  applyTheme("system") // the test setup's matchMedia reports light
  expect(document.documentElement).not.toHaveClass("dark")
})
