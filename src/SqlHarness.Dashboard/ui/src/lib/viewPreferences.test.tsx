import { act, renderHook } from "@testing-library/react"
import { expect, test, vi } from "vitest"
import { isViewBoolean, isViewString, useViewPreference } from "./viewPreferences"

test("view preferences survive remount but reject malformed and oversized stored data", () => {
  const first = renderHook(() => useViewPreference("probe", "", isViewString))
  act(() => first.result.current[1]("Orders"))
  first.unmount()
  const restored = renderHook(() => useViewPreference("probe", "", isViewString))
  expect(restored.result.current[0]).toBe("Orders")
  restored.unmount()
  sessionStorage.setItem("sqlharness.view.probe", "{broken")
  const malformed = renderHook(() => useViewPreference("probe", "initial", isViewString))
  expect(malformed.result.current[0]).toBe("initial")
  malformed.unmount()
  sessionStorage.setItem("sqlharness.view.probe", JSON.stringify("x".repeat(20_000)))
  expect(renderHook(() => useViewPreference("probe", "initial", isViewString)).result.current[0]).toBe("initial")
})

test("unavailable storage leaves view changes usable", () => {
  vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => { throw new Error("Storage blocked") })
  vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => { throw new Error("Storage blocked") })
  const hook = renderHook(() => useViewPreference("probe", false, isViewBoolean))
  act(() => hook.result.current[1](true))
  expect(hook.result.current[0]).toBe(true)
})
