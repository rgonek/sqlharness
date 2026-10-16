import { expect, test } from "vitest"
import { BUILT_IN_SOUNDS, getSoundPreferences, saveSoundPreferences } from "./sound"

test("Warm chime is the default among the three approved notification sounds", async () => {
  const preferences = await getSoundPreferences()
  expect(preferences.selection).toBe("warm-chime")
  expect(BUILT_IN_SOUNDS.map(sound => sound.label)).toEqual(["Soft ping", "Warm chime", "Glass drop"])
  expect(BUILT_IN_SOUNDS.map(sound => sound.src)).toEqual([
    "/sounds/soft-ping.wav", "/sounds/warm-chime.wav", "/sounds/glass-drop.wav",
  ])
})

test("a failed IndexedDB write rejects while keeping the in-memory preference available", async () => {
  const preferences = { selection: "glass-drop" as const, muted: true, customAudio: null }
  const previousIndexedDb = Object.getOwnPropertyDescriptor(globalThis, "indexedDB")
  Object.defineProperty(globalThis, "indexedDB", { configurable: true, value: undefined })
  try {
    await expect(saveSoundPreferences(preferences)).rejects.toThrow("IndexedDB is unavailable")
    await expect(getSoundPreferences()).resolves.toEqual(preferences)
  } finally {
    if (previousIndexedDb) Object.defineProperty(globalThis, "indexedDB", previousIndexedDb)
    else Reflect.deleteProperty(globalThis, "indexedDB")
  }
})
