export const BUILT_IN_SOUNDS = [
  { id: "soft-ping", label: "Soft ping", src: "/sounds/soft-ping.wav" },
  { id: "warm-chime", label: "Warm chime", src: "/sounds/warm-chime.wav" },
  { id: "glass-drop", label: "Glass drop", src: "/sounds/glass-drop.wav" },
] as const
export type BuiltInSoundId = typeof BUILT_IN_SOUNDS[number]["id"]
export type SoundSelection = BuiltInSoundId | "custom"
export type SoundPreferences = { selection: SoundSelection; muted: boolean; customAudio: Blob | null }
export const MAX_CUSTOM_SOUND_BYTES = 2 * 1024 * 1024
const ACCEPTED_CUSTOM_AUDIO = new Set(["audio/mpeg", "audio/wav", "audio/x-wav", "audio/ogg", "application/ogg"])
const DATABASE = "sqlharness-dashboard-local"
const STORE = "mutation-sound"
const KEY = "preferences"
const DEFAULTS: SoundPreferences = { selection: "warm-chime", muted: false, customAudio: null }
let memoryPreferences: SoundPreferences = DEFAULTS
let preferencesLoaded = false

function openDatabase(): Promise<IDBDatabase> {
  return new Promise((resolve, reject) => {
    if (!globalThis.indexedDB) return reject(new Error("IndexedDB is unavailable"))
    const request = indexedDB.open(DATABASE, 1)
    request.onupgradeneeded = () => request.result.createObjectStore(STORE)
    request.onsuccess = () => resolve(request.result)
    request.onerror = () => reject(request.error)
  })
}

async function readPreferences(): Promise<SoundPreferences> {
  if (preferencesLoaded) return memoryPreferences
  try {
    const db = await openDatabase()
    const value = await new Promise<SoundPreferences | undefined>((resolve, reject) => {
      const request = db.transaction(STORE, "readonly").objectStore(STORE).get(KEY)
      request.onsuccess = () => resolve(request.result as SoundPreferences | undefined)
      request.onerror = () => reject(request.error)
    })
    db.close()
    if (value) memoryPreferences = value
  } catch { /* Browser-local playback still works with in-memory defaults. */ }
  preferencesLoaded = true
  return memoryPreferences
}

export async function saveSoundPreferences(preferences: SoundPreferences): Promise<void> {
  memoryPreferences = preferences
  preferencesLoaded = true
  try {
    const db = await openDatabase()
    await new Promise<void>((resolve, reject) => {
      const transaction = db.transaction(STORE, "readwrite")
      transaction.objectStore(STORE).put(preferences, KEY)
      transaction.oncomplete = () => resolve()
      transaction.onerror = () => reject(transaction.error)
    })
    db.close()
  } catch { /* Settings remain usable if a browser disables IndexedDB. */ }
}

export async function getSoundPreferences(): Promise<SoundPreferences> { return readPreferences() }

/** Validates a local custom MP3, WAV, or OGG before storing its bytes in IndexedDB. */
export async function validateCustomSound(file: File): Promise<string | null> {
  const extension = file.name.split(".").pop()?.toLowerCase()
  const mimeByExtension: Record<string, string[]> = {
    mp3: ["audio/mpeg", "audio/mp3"], wav: ["audio/wav", "audio/x-wav"], ogg: ["audio/ogg", "application/ogg"],
  }
  if (!extension || !mimeByExtension[extension] || !ACCEPTED_CUSTOM_AUDIO.has(file.type.toLowerCase()) || !mimeByExtension[extension].includes(file.type.toLowerCase()))
    return "Choose an MP3, WAV, or OGG audio file with a matching file type."
  if (file.size > MAX_CUSTOM_SOUND_BYTES) return "Audio files must be 2 MiB or smaller."
  if (typeof AudioContext === "undefined") return null
  try {
    const context = new AudioContext()
    await context.decodeAudioData(await file.arrayBuffer())
    await context.close()
  } catch { return "The selected file could not be decoded as audio." }
  return null
}

async function play(preview: boolean): Promise<void> {
  const preferences = await readPreferences()
  if (preferences.muted) return
  const builtin = BUILT_IN_SOUNDS.find(item => item.id === preferences.selection)
  if (!builtin && !preferences.customAudio) return
  const objectUrl = builtin ? null : URL.createObjectURL(preferences.customAudio!)
  const audio = new Audio(builtin?.src ?? objectUrl!)
  audio.volume = 0.22
  if (objectUrl) audio.addEventListener("ended", () => URL.revokeObjectURL(objectUrl), { once: true })
  try { await audio.play() } catch {
    if (objectUrl) URL.revokeObjectURL(objectUrl)
    /* Autoplay policy must never affect approval controls or the next user-gesture preview. */
  }
  void preview
}

/** Plays the selected notification sound at low volume; call after a newly observed live request. */
export function playApprovalSound(): Promise<void> { return play(false).catch(() => {}) }
/** Plays a user-requested preview. The caller should invoke this from a user gesture. */
export function previewApprovalSound(): Promise<void> { return play(true).catch(() => {}) }
