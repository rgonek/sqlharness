import { expect, test } from "vitest"
import { BUILT_IN_SOUNDS, getSoundPreferences } from "./sound"

test("Warm chime is the default among the three approved notification sounds", async () => {
  const preferences = await getSoundPreferences()
  expect(preferences.selection).toBe("warm-chime")
  expect(BUILT_IN_SOUNDS.map(sound => sound.label)).toEqual(["Soft ping", "Warm chime", "Glass drop"])
  expect(BUILT_IN_SOUNDS.map(sound => sound.src)).toEqual([
    "/sounds/soft-ping.wav", "/sounds/warm-chime.wav", "/sounds/glass-drop.wav",
  ])
})
