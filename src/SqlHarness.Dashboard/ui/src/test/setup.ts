import "@testing-library/jest-dom/vitest"
import { cleanup } from "@testing-library/react"
import { afterEach } from "vitest"

// Recharts' ResponsiveContainer and Base UI measure elements; jsdom has no ResizeObserver.
class ResizeObserverStub {
  observe() {}
  unobserve() {}
  disconnect() {}
}
globalThis.ResizeObserver ??= ResizeObserverStub as unknown as typeof ResizeObserver

// Theme sync reads the colour-scheme preference; jsdom does not implement matchMedia.
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

// TanStack Router restores scroll on navigation; jsdom logs "Not implemented" for scrollTo.
window.scrollTo = (() => {}) as typeof window.scrollTo

afterEach(() => cleanup())
