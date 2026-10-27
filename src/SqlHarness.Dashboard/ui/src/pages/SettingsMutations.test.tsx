import { fireEvent, screen, waitFor, within } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { expect, test, vi } from "vitest"
import { renderApp, stubFetch } from "@/test/render"
import * as sound from "@/mutations/sound"

const initial = {
  status: "valid", path: "config.json", settings: {
    journal: { enabled: true, storeSensitive: false, operationAnnotations: false, retention: { enabled: false, maxAgeDays: 30, maxSizeMb: 500 } },
    dashboard: { autoStart: false, port: 47800, idleShutdownHours: 8, safetyNotifications: false, agentTranscripts: { enabled: false } },
    compare: { requireContext: true }, diagnostics: { enabled: false },
    connection: { timeoutSeconds: 30 },
    execution: { defaultTimeoutSeconds: 30 },
    mutations: { defaultMode: "never", verification: "click", approvalTimeoutSeconds: 120 },
  },
}

function routes(onPut?: (body: unknown) => Response) {
  const puts: unknown[] = []
  stubFetch({
    "/api/settings": initial,
    "PUT /api/settings": (_url: URL, init?: RequestInit) => {
      const body = JSON.parse(String(init?.body))
      puts.push(body)
      return onPut?.(body) ?? new Response(JSON.stringify({ ...initial, settings: body }), { status: 200 })
    },
    "/api/profiles": { status: "valid", message: null, profiles: [] },
    "/api/transcripts": { enabled: false, matched: 0, unmatched: 0, readErrors: 0, lastCycleAt: null },
    "/api/prices": { source: "models.dev", fetchedAt: "2026-10-16T00:00:00Z", origin: "embedded" },
  })
  return puts
}

test("mutation settings expose click-only verification and preserve mutations on other saves", async () => {
  const puts = routes()
  renderApp("/settings")
  expect(await screen.findByText("click", { selector: "code" })).toBeInTheDocument()
  const mode = await screen.findByRole("radiogroup", { name: "Default mutation mode" })
  expect(within(mode).getByRole("radio", { name: "Never" })).toBeChecked()
  await userEvent.click(within(mode).getByRole("radio", { name: "Operator approval" }))
  expect(within(mode).getByRole("radio", { name: "Operator approval" })).toBeChecked()
  await waitFor(() => expect(puts.length).toBe(1))
  expect((puts[0] as typeof initial.settings).mutations).toEqual({ defaultMode: "operator", verification: "click", approvalTimeoutSeconds: 120 })

  const invalid = { ...initial, settings: { ...initial.settings, mutations: { ...initial.settings.mutations, verification: "webauthn" } } }
  stubFetch({ "/api/settings": invalid, "/api/profiles": { status: "valid", message: null, profiles: [] },
    "/api/transcripts": { enabled: false, matched: 0, unmatched: 0, readErrors: 0, lastCycleAt: null },
    "/api/prices": { source: "models.dev", fetchedAt: "2026-10-16T00:00:00Z", origin: "embedded" } })
  renderApp("/settings")
  expect(await screen.findByRole("alert")).toHaveTextContent(/webauthn.*unsupported.*click/i)
  expect(screen.getByText("webauthn", { selector: "code" })).toBeInTheDocument()
})

test("approval timeout commits on blur and surfaces the server validation for 10", async () => {
  const puts = routes(() => new Response(JSON.stringify({ errors: [{ field: "mutations.approvalTimeoutSeconds", message: "Must be between 30 and 600." }] }), { status: 400 }))
  renderApp("/settings")
  const field = await screen.findByRole("spinbutton", { name: "Approval timeout (seconds)" })
  await userEvent.clear(field)
  await userEvent.type(field, "10")
  await userEvent.tab()
  await waitFor(() => expect(puts.length).toBe(1))
  expect((puts[0] as typeof initial.settings).mutations.approvalTimeoutSeconds).toBe(10)
  expect(await screen.findByText("Must be between 30 and 600.")).toBeInTheDocument()
})

test("sound selection offers previews, mute, and an inline IndexedDB persistence error", async () => {
  const preview = vi.spyOn(sound, "previewApprovalSound").mockResolvedValue(undefined)
  vi.spyOn(sound, "getSoundPreferences").mockResolvedValue({ selection: "warm-chime", muted: false, customAudio: null })
  vi.spyOn(sound, "saveSoundPreferences").mockRejectedValue(new Error("IndexedDB is unavailable"))
  routes()
  renderApp("/settings")
  const select = await screen.findByRole("combobox", { name: "Approval sound" })
  expect(select).toHaveTextContent("Warm chime")
  expect(screen.getByRole("button", { name: "Preview Warm chime" })).toBeInTheDocument()
  await userEvent.click(screen.getByRole("button", { name: "Preview Warm chime" }))
  expect(preview).toHaveBeenCalledOnce()
  await userEvent.click(select)
  await userEvent.click(screen.getByRole("option", { name: /^Glass drop$/ }))
  expect(select).toHaveTextContent("Glass drop")
  expect(sound.saveSoundPreferences).toHaveBeenCalledWith(expect.objectContaining({ selection: "glass-drop" }))
  expect(await screen.findByRole("alert")).toHaveTextContent(/active for this page.*could not be saved for reload/i)
  expect(screen.queryByRole("status")).not.toBeInTheDocument()
})

test("shows a readable custom sound preference when its local audio is missing", async () => {
  vi.spyOn(sound, "getSoundPreferences").mockResolvedValue({ selection: "custom", muted: false, customAudio: null })
  routes()
  renderApp("/settings")
  const select = await screen.findByRole("combobox", { name: "Approval sound" })
  expect(select).toHaveTextContent("Custom audio")
  await userEvent.click(select)
  expect(screen.queryByRole("option", { name: /^Custom audio$/ })).not.toBeInTheDocument()
})

test("custom sound rejects wrong type and oversized files inline without a server upload", async () => {
  vi.spyOn(sound, "getSoundPreferences").mockResolvedValue({ selection: "warm-chime", muted: false, customAudio: null })
  const put = routes()
  renderApp("/settings")
  const input = await screen.findByLabelText("Custom audio file")
  const invalidType = new File(["text"], "alert.txt", { type: "text/plain" })
  fireEvent.change(input, { target: { files: [invalidType] } })
  expect(await screen.findByRole("alert")).toHaveTextContent(/choose an MP3, WAV, or OGG/i)
  const oversized = new File([new Uint8Array(2 * 1024 * 1024 + 1)], "alert.wav", { type: "audio/wav" })
  fireEvent.change(input, { target: { files: [oversized] } })
  expect(await screen.findByRole("alert")).toHaveTextContent(/2 MiB or smaller/i)
  expect(put).toHaveLength(0)
})

test("a valid custom audio file is passed to browser-local persistence", async () => {
  const file = new File([new Uint8Array([1, 2, 3])], "alert.ogg", { type: "audio/ogg" })
  vi.spyOn(sound, "validateCustomSound").mockResolvedValue(null)
  const save = vi.spyOn(sound, "saveSoundPreferences").mockResolvedValue(undefined)
  const puts = routes()
  renderApp("/settings")
  fireEvent.change(await screen.findByLabelText("Custom audio file"), { target: { files: [file] } })
  await waitFor(() => expect(save).toHaveBeenCalled())
  expect(save).toHaveBeenCalledWith(expect.objectContaining({ selection: "custom", customAudio: file }))
  expect(puts).toHaveLength(0)
})

test("notification permission is requested only from its button", async () => {
  const requestPermission = vi.fn().mockResolvedValue("granted")
  Object.defineProperty(window, "Notification", { configurable: true, value: { permission: "default", requestPermission } })
  vi.spyOn(sound, "getSoundPreferences").mockResolvedValue({ selection: "warm-chime", muted: false, customAudio: null })
  routes()
  renderApp("/settings")
  await screen.findByRole("button", { name: "Enable browser notifications" })
  expect(requestPermission).not.toHaveBeenCalled()
  await userEvent.click(screen.getByRole("button", { name: "Enable browser notifications" }))
  await waitFor(() => expect(requestPermission).toHaveBeenCalledOnce())
})
