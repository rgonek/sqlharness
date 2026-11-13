import { render, screen, waitFor } from "@testing-library/react"
import { QueryClient, QueryClientProvider } from "@tanstack/react-query"
import userEvent from "@testing-library/user-event"
import { expect, test, vi } from "vitest"
import { ReproScriptButtons } from "./ReproScriptButtons"

const renderButtons = (side: string, caseId: string) => render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}><ReproScriptButtons operationId={10} side={side} caseId={caseId} /></QueryClientProvider>)

test("copies both reproduction script forms returned by the endpoint", async () => {
  const writeText = vi.fn().mockResolvedValue(undefined)
  Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText } })
  const fetchMock = vi.spyOn(globalThis, "fetch").mockImplementation(async () => new Response(JSON.stringify({ text: "SELECT 1", completeness: "Complete", label: "As executed" }), { status: 200 }))
  renderButtons("candidate", "case/1")

  await waitFor(() => expect(screen.getByRole("button", { name: "Copy script (as executed)" })).toBeEnabled())
  await userEvent.click(screen.getByRole("button", { name: "Copy script (as executed)" }))
  await waitFor(() => expect(writeText).toHaveBeenCalledWith("SELECT 1"))
  expect(fetchMock).toHaveBeenCalledWith("/api/operations/10/repro?side=candidate&case=case%2F1&form=as-executed", expect.any(Object))

  await userEvent.click(screen.getByRole("button", { name: "Copy script (local variables)" }))
  await waitFor(() => expect(writeText).toHaveBeenCalledTimes(2))
  expect(fetchMock).toHaveBeenLastCalledWith("/api/operations/10/repro?side=candidate&case=case%2F1&form=local-variables", expect.any(Object))
  fetchMock.mockRestore()
})

test("warns when values are missing and disables unavailable scripts with the API label as tooltip", async () => {
  const fetchMock = vi.spyOn(globalThis, "fetch")
    .mockResolvedValueOnce(new Response(JSON.stringify({ text: "SELECT @p", completeness: "ValuesMissing", label: "Incomplete — values not stored" }), { status: 200 }))
    .mockResolvedValueOnce(new Response(JSON.stringify({ text: null, completeness: "Unavailable", label: "Script not available for this engine" }), { status: 200 }))
  renderButtons("query", "0")

  expect(screen.getAllByRole("button")).toHaveLength(2)
  await waitFor(() => expect(screen.getAllByRole("button")[0]).toBeEnabled())
  expect(await screen.findByText("Incomplete — values not stored")).toBeInTheDocument()
  expect(screen.getAllByRole("button")[1]).toBeDisabled()
  expect(screen.getAllByRole("button")[1]).toHaveAttribute("title", "Script not available for this engine")
  fetchMock.mockRestore()
})

test("named reproduction action offers exact returned text for manual copy when the clipboard is denied", async () => {
  Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText: vi.fn().mockRejectedValue(new Error("denied")) } })
  const fetchMock = vi.spyOn(globalThis, "fetch").mockImplementation(async () => new Response(JSON.stringify({ text: "SELECT @p; -- candidate case/2", completeness: "Complete", label: "As executed" }), { status: 200 }))
  render(<QueryClientProvider client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>
    <ReproScriptButtons operationId={10} side="candidate" caseId="case/2" labelPrefix="candidate case/2" />
  </QueryClientProvider>)
  const action = await screen.findByRole("button", { name: "Copy candidate case/2 script (as executed)" })
  await waitFor(() => expect(action).toBeEnabled())
  await userEvent.click(action)
  expect(await screen.findByRole("textbox", { name: "Text to copy" })).toHaveValue("SELECT @p; -- candidate case/2")
  expect(fetchMock).toHaveBeenCalledWith("/api/operations/10/repro?side=candidate&case=case%2F2&form=as-executed", expect.any(Object))
})
