import assert from "node:assert/strict"
import { dirname, join } from "node:path"
import { fileURLToPath, pathToFileURL } from "node:url"
const here = dirname(fileURLToPath(import.meta.url))
const playwrightPath = process.env.PLAYWRIGHT_CORE_PATH ?? "D:/Dev/CivicLens/frontend/node_modules/playwright-core/index.mjs"
const { chromium } = await import(pathToFileURL(playwrightPath))

const baseUrl = process.env.UI_FIXTURE_URL ?? "http://127.0.0.1:4173/task-4-browser-fixture.html"
const executablePath = process.env.CHROMIUM_PATH ?? "C:/Users/rgone/AppData/Local/ms-playwright/chromium-1228/chrome-win64/chrome.exe"
const browser = await chromium.launch({ headless: true, executablePath })
try {
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 }, reducedMotion: "reduce" })
  const requests = []
  await page.route("**/api/sessions/5/sql?**", async route => {
    const url = new URL(route.request().url())
    requests.push(url.searchParams.get("hash"))
    await route.fulfill({ status: 200, contentType: "application/json", body: JSON.stringify({ hash: url.searchParams.get("hash"), text: "SELECT 1", tooLarge: false }) })
  })
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" })
  const from = page.getByRole("combobox", { name: "From version" })
  const to = page.getByRole("combobox", { name: "To version" })
  const listFor = async trigger => page.locator(`#${await trigger.getAttribute("aria-controls")}`)
  await from.waitFor()
  assert.match(await from.innerText(), /v1 · baseline/)
  assert.doesNotMatch(await from.innerText(), /sha256|\["v1"/)

  await from.focus()
  await page.keyboard.press("Enter")
  await (await listFor(from)).getByRole("option", { name: /v1 · baseline/ }).waitFor()
  await page.keyboard.press("ArrowDown")
  await page.keyboard.press("Enter")
  assert.match(await from.innerText(), /v2 · feature branch/)
  assert.ok(requests.includes("sha256:" + "2".repeat(64)), "ArrowDown + Enter selects the v2 hash")

  await to.focus()
  await page.keyboard.press("Enter")
  await (await listFor(to)).getByRole("option", { name: /v3 · release/ }).waitFor()
  await page.keyboard.press("Escape")
  assert.equal(await to.getAttribute("aria-expanded"), "false")
  assert.equal(await page.evaluate(() => document.activeElement?.getAttribute("aria-label")), "To version", "Escape returns focus to the trigger")

  await from.focus()
  await page.keyboard.press("Enter")
  await page.keyboard.type("v3")
  await page.keyboard.press("Enter")
  assert.match(await from.innerText(), /v3 · release/)

  await page.setViewportSize({ width: 320, height: 800 })
  await from.evaluate(element => element.scrollIntoView())
  const layout = await page.evaluate(() => ({
    viewport: innerWidth,
    document: document.documentElement.scrollWidth,
    triggers: Array.from(document.querySelectorAll('[role="combobox"]')).map(element => {
      const bounds = element.getBoundingClientRect()
      return { width: bounds.width, right: bounds.right, text: element.textContent?.trim() }
    }),
  }))
  assert.ok(layout.document <= layout.viewport, `page overflows horizontally: ${JSON.stringify(layout)}`)
  assert.ok(layout.triggers.every(trigger => trigger.right <= layout.viewport), `trigger overflows viewport: ${JSON.stringify(layout)}`)
  await page.screenshot({ path: join(here, "task-4-mobile.png"), fullPage: true })
  process.stdout.write(JSON.stringify({ keyboardArrowEnter: "v2/hash2", escapeFocusReturn: true, typeaheadEnter: "v3/hash3", longLabel: true, layout }, null, 2) + "\n")
} finally {
  await browser.close()
}
