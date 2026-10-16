import { expect, test, vi } from "vitest"
import { BUILT_IN_SOUNDS, getSoundPreferences, saveSoundPreferences, validateCustomSound } from "./sound"

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

test("accepts audio/mp3 when the selected custom file has an mp3 extension", async () => {
  const file = new File([new Uint8Array([1, 2, 3])], "alert.mp3", { type: "audio/mp3" })
  const previousAudioContext = Object.getOwnPropertyDescriptor(globalThis, "AudioContext")
  Object.defineProperty(globalThis, "AudioContext", { configurable: true, value: undefined })
  try {
    await expect(validateCustomSound(file)).resolves.toBeNull()
  } finally {
    if (previousAudioContext) Object.defineProperty(globalThis, "AudioContext", previousAudioContext)
    else Reflect.deleteProperty(globalThis, "AudioContext")
  }
})

test("closes AudioContext when custom audio decoding fails", async () => {
  const close = vi.fn().mockResolvedValue(undefined)
  class FailingAudioContext {
    decodeAudioData() { return Promise.reject(new Error("decode failed")) }
    close = close
  }
  const previousAudioContext = Object.getOwnPropertyDescriptor(globalThis, "AudioContext")
  Object.defineProperty(globalThis, "AudioContext", { configurable: true, value: FailingAudioContext })
  try {
    const file = new File([new Uint8Array([1, 2, 3])], "alert.wav", { type: "audio/wav" })
    await expect(validateCustomSound(file)).resolves.toMatch(/could not be decoded/)
    expect(close).toHaveBeenCalledOnce()
  } finally {
    if (previousAudioContext) Object.defineProperty(globalThis, "AudioContext", previousAudioContext)
    else Reflect.deleteProperty(globalThis, "AudioContext")
  }
})

test("sound choice and custom audio bytes survive a fresh module load from IndexedDB", async () => {
  const stored = new Map<string, unknown>()
  const db = {
    createObjectStore: () => ({}),
    close: () => {},
    transaction: () => {
      const transaction: { oncomplete?: () => void; onerror?: () => void; onabort?: () => void } = {}
      return { ...transaction, objectStore: () => ({
        put: (value: unknown, key: string) => { stored.set(key, value); setTimeout(() => transaction.oncomplete?.(), 0) },
        get: (key: string) => {
          const request: { result?: unknown; onsuccess?: () => void; onerror?: () => void } = {}
          setTimeout(() => { request.result = stored.get(key); request.onsuccess?.() }, 0)
          return request
        },
      }), get oncomplete() { return transaction.oncomplete }, set oncomplete(value) { transaction.oncomplete = value },
      get onerror() { return transaction.onerror }, set onerror(value) { transaction.onerror = value },
      get onabort() { return transaction.onabort }, set onabort(value) { transaction.onabort = value }, }
    },
  }
  const factory = { open: () => {
    const request: { result: typeof db; onupgradeneeded?: () => void; onsuccess?: () => void; onerror?: () => void } = { result: db }
    setTimeout(() => { request.onupgradeneeded?.(); request.onsuccess?.() }, 0)
    return request
  } }
  const previousIndexedDb = Object.getOwnPropertyDescriptor(globalThis, "indexedDB")
  Object.defineProperty(globalThis, "indexedDB", { configurable: true, value: factory as unknown as IDBFactory })
  try {
    const audio = new Blob([new Uint8Array([1, 2, 3])], { type: "audio/ogg" })
    await saveSoundPreferences({ selection: "custom", muted: true, customAudio: audio })
    vi.resetModules()
    const freshSound = await import("./sound")
    const restored = await freshSound.getSoundPreferences()
    expect(restored).toEqual({ selection: "custom", muted: true, customAudio: audio })
  } finally {
    if (previousIndexedDb) Object.defineProperty(globalThis, "indexedDB", previousIndexedDb)
    else Reflect.deleteProperty(globalThis, "indexedDB")
  }
})
