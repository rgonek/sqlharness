import { cleanup, render, screen, waitFor } from "@testing-library/react"
import userEvent from "@testing-library/user-event"
import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import { afterEach, expect, test, vi } from "vitest"
import { versionOptions } from "@/lib/compare"
import { stubFetch } from "@/test/render"
import { SessionVersionDiff } from "./SessionVersionDiff"

vi.mock("@/components/SqlEditors", () => ({
  LazySqlMergeView: ({ baseline, candidate, mode }: { baseline: string; candidate: string; mode: string }) =>
    <pre aria-label={`merge ${mode}`}>{baseline}|{candidate}</pre>,
  loadUnifiedDiffText: async () => (await import("@/lib/unifiedDiff")).unifiedDiffText,
}))

afterEach(() => {
  cleanup()
  localStorage.clear()
})

function renderDiff(options: { version: string; hash: string }[], engine: string | null = "postgres") {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Infinity } } })
  return render(<QueryClientProvider client={client}><SessionVersionDiff sessionId={5} engine={engine} options={options} /></QueryClientProvider>)
}

const hash0 = `sha256:${"0".repeat(64)}`
const hash1 = `sha256:${"1".repeat(64)}`
const hash3 = `sha256:${"3".repeat(64)}`
const options = [{ version: "v0", hash: hash0 }, { version: "v1", hash: hash1 }, { version: "v3", hash: hash3 }]

test("builds distinct hash-backed versions in numeric order", () => {
  const versions = [
    { version: "v10", hash: "h10" }, { version: "v11", hash: "h11" },
    { version: "v1", hash: "h1" }, { version: "v10", hash: "h10" },
  ]
  expect(versionOptions(versions)).toEqual([
    { version: "v1", hash: "h1" }, { version: "v10", hash: "h10" }, { version: "v11", hash: "h11" },
  ])
})

test("adds hash suffixes when retained versions share a label", async () => {
  const labeled = versionOptions([{ version: "v2", hash: "sha256:aaaaaaaa" }, { version: "v2", hash: "sha256:bbbbbbbb" }])
  stubFetch({
    "/api/sessions/5/sql": (url: URL) => {
      const hash = url.searchParams.get("hash")
      return new Response(JSON.stringify({ hash, text: "SELECT 1", tooLarge: false }), { status: 200 })
    },
  })
  renderDiff(labeled)

  expect(screen.getByRole("combobox", { name: "From version" })).toHaveTextContent("v2 (aaaaaaaa)")
  expect(screen.getByRole("combobox", { name: "To version" })).toHaveTextContent("v2 (bbbbbbbb)")
  await userEvent.click(screen.getByRole("combobox", { name: "From version" }))
  expect(screen.getByRole("option", { name: /^v2 \(aaaaaaaa\)$/ })).toBeInTheDocument()
  expect(screen.getByRole("option", { name: /^v2 \(bbbbbbbb\)$/ })).toBeInTheDocument()
})

test("defaults to retained endpoints, fetches each selected hash, and keeps mode keyboard accessible", async () => {
  const calls = stubFetch({
    "/api/sessions/5/sql": (url: URL) => {
      const hash = url.searchParams.get("hash")
      const text = hash === hash0 ? "SELECT 0" : hash === hash1 ? "SELECT 1" : "SELECT 3"
      return new Response(JSON.stringify({ hash, text, tooLarge: false }), { status: 200 })
    },
  })
  const user = userEvent.setup()
  renderDiff(options)

  expect(await screen.findByLabelText("merge side-by-side")).toHaveTextContent("SELECT 0|SELECT 3")
  expect(screen.getByRole("combobox", { name: "From version" })).toHaveTextContent("v0")
  expect(screen.getByRole("combobox", { name: "To version" })).toHaveTextContent("v3")
  expect(calls).toEqual([
    `/api/sessions/5/sql?hash=${encodeURIComponent(hash0)}`,
    `/api/sessions/5/sql?hash=${encodeURIComponent(hash3)}`,
  ])

  const unified = screen.getByRole("radio", { name: "Unified" })
  screen.getByRole("radio", { name: "Side by side" }).focus()
  await user.keyboard("{ArrowRight}")
  expect(unified).toBeChecked()
  expect(await screen.findByLabelText("merge unified")).toBeInTheDocument()

  await user.click(screen.getByRole("combobox", { name: "To version" }))
  await user.click(screen.getByRole("option", { name: /^v1$/ }))
  expect(await screen.findByLabelText("merge unified")).toHaveTextContent("SELECT 0|SELECT 1")
  expect(screen.getByRole("combobox", { name: "To version" })).toHaveTextContent("v1")
  await waitFor(() => expect(calls).toContain(`/api/sessions/5/sql?hash=${encodeURIComponent(hash1)}`))
  expect(calls.filter(call => call.includes(encodeURIComponent(hash0)))).toHaveLength(1)
})

test("shows an empty state and recovers when retained versions are missing", () => {
  renderDiff([])
  expect(screen.getByRole("combobox", { name: "From version" })).toHaveTextContent("No versions available")
  expect(screen.getByRole("combobox", { name: "To version" })).toHaveTextContent("No versions available")
  expect(screen.queryByRole("option")).not.toBeInTheDocument()
})

test("explains missing text and oversized stored versions", async () => {
  stubFetch({
    "/api/sessions/5/sql": (url: URL) => {
      const hash = url.searchParams.get("hash")
      return new Response(JSON.stringify(hash === hash0
        ? { hash, text: null, tooLarge: false }
        : { hash, text: null, tooLarge: true }), { status: 200 })
    },
  })
  renderDiff(options.slice(0, 2))

  expect(await screen.findByText("SQL text for v0 is not stored (journal.storeSensitive was off)." )).toBeInTheDocument()
  expect(await screen.findByText("v1 is larger than 256 KiB; open a compare to see a bounded preview.")).toBeInTheDocument()
})

test("explains a version that has left retained history", async () => {
  stubFetch({
    "/api/sessions/5/sql": (url: URL) => url.searchParams.get("hash") === hash0
      ? new Response("", { status: 404 })
      : new Response(JSON.stringify({ hash: hash3, text: "SELECT 3", tooLarge: false }), { status: 200 }),
  })
  renderDiff([options[0], options[2]])

  expect(await screen.findByText("v0 is no longer in the retained history.")).toBeInTheDocument()
})

test("falls back to both raw texts when either formatter fails", async () => {
  localStorage.setItem("sqlharness.sqlDiff.ignoreFormatting", "true")
  stubFetch({
    "/api/sessions/5/sql": (url: URL) => {
      const hash = url.searchParams.get("hash")
      return new Response(JSON.stringify({ hash, text: hash === hash0 ? "SELECT (" : "SELECT )", tooLarge: false }), { status: 200 })
    },
  })
  renderDiff([options[0], options[2]])

  expect(await screen.findByText("Formatting failed; showing the SQL as written.")).toBeInTheDocument()
  expect(await screen.findByLabelText("merge side-by-side")).toHaveTextContent("SELECT (|SELECT )")
})

test("formatting expansion cannot bypass the combined diff size limit", async () => {
  localStorage.setItem("sqlharness.sqlDiff.ignoreFormatting", "true")
  const compactSql = `SELECT ${Array.from({ length: 55_000 }, () => "a").join(",")}`
  expect(new TextEncoder().encode(compactSql).byteLength * 2).toBeLessThan(256 * 1024)
  stubFetch({
    "/api/sessions/5/sql": (url: URL) => {
      const hash = url.searchParams.get("hash")
      return new Response(JSON.stringify({ hash, text: compactSql, tooLarge: false }), { status: 200 })
    },
  })
  renderDiff([options[0], options[2]])

  expect(await screen.findByText("Diff unavailable: SQL exceeds the 256 KiB or 4,000 line limit.")).toBeInTheDocument()
  expect(screen.getByLabelText("From bounded SQL preview")).toBeInTheDocument()
  expect(screen.getByLabelText("To bounded SQL preview")).toBeInTheDocument()
})
