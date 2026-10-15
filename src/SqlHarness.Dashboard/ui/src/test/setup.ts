import "@testing-library/jest-dom/vitest"
import { cleanup, configure } from "@testing-library/react"
import { afterEach } from "vitest"

// Recharts' ResponsiveContainer and Base UI measure elements; neither jsdom nor
// happy-dom provides ResizeObserver.
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
globalThis.ResizeObserver ??= ResizeObserverStub as unknown as typeof ResizeObserver

// Theme sync reads the colour-scheme preference; the DOM environment does not
// implement matchMedia.
window.matchMedia ??= ((query: string) => ({
  matches: false,
  media: query,
  onchange: null,
  addEventListener: () => {},
  removeEventListener: () => {},
  addListener: () => {},
  removeListener: () => {},
  dispatchEvent: () => false,
})) as typeof window.matchMedia

// TanStack Router restores scroll on navigation; the DOM environment logs
// "Not implemented" for scrollTo.
window.scrollTo = (() => {}) as typeof window.scrollTo

// The Linux gate's jsdom renders the stats page slower than findBy's 1s default.
configure({ asyncUtilTimeout: 5_000 })

afterEach(() => cleanup())
