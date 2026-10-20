import assert from "node:assert/strict"
import { chromium } from "playwright-core"
import { fileURLToPath } from "node:url"
import { dirname, join } from "node:path"

const here = dirname(fileURLToPath(import.meta.url))
const baseUrl = process.env.UI_FIXTURE_URL ?? "http://127.0.0.1:4173/task-3-browser-fixture.html"
const executablePath = process.env.CHROMIUM_PATH ?? "C:/Users/rgone/AppData/Local/ms-playwright/chromium-1228/chrome-win64/chrome.exe"
const browser = await chromium.launch({ headless: true, executablePath })
try {
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 }, reducedMotion: "reduce" })
  await page.route(url => new URL(url).pathname.startsWith("/api/"), route =>
    route.fulfill({ status: 200, contentType: "application/json", body: "{}" }))
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" })
  await page.getByRole("heading", { name: "Compare detail browser fixture" }).waitFor()
  const trigger = name => page.getByRole("button", { name, exact: true })
  const sql = trigger("SQL diff & conditions")
  const plans = trigger("Plans")
  const cases = trigger("Per-case & per-run details")
  assert.deepEqual(await Promise.all([sql, plans, cases].map(x => x.getAttribute("aria-expanded"))), ["false", "false", "false"])

  await sql.focus()
  await page.keyboard.press("Tab")
  assert.equal(await page.evaluate(() => document.activeElement.textContent.trim()), "Plans")
  await page.keyboard.press("Shift+Tab")
  await page.keyboard.press("Enter")
  await plans.click()
  await cases.click()
  assert.deepEqual(await Promise.all([sql, plans, cases].map(x => x.getAttribute("aria-expanded"))), ["true", "true", "true"])

  const more = trigger("More")
  assert.equal(await more.getAttribute("aria-expanded"), "false")
  await more.click()
  assert.equal(await more.getAttribute("aria-expanded"), "true")
  assert.equal(await plans.getAttribute("aria-expanded"), "true")
  await plans.click()
  assert.equal(await plans.getAttribute("aria-expanded"), "false")
  await plans.click()
  assert.equal(await more.getAttribute("aria-expanded"), "true")

  await page.getByRole("tab", { name: "Diff" }).waitFor()
  const panes = await page.locator(".monaco-editor").evaluateAll(nodes => nodes.map(node => {
    const rect = node.getBoundingClientRect()
    return { width: rect.width, height: rect.height, display: getComputedStyle(node).display }
  }))
  assert.ok(panes.filter(pane => pane.width > 0 && pane.height > 0).length >= 2)
  assert.equal(await page.evaluate(() => matchMedia("(prefers-reduced-motion: reduce)").matches), true)
  await page.screenshot({ path: join(here, "reproduced-browser-check.png"), fullPage: true })
  process.stdout.write(JSON.stringify({ initial: ["false", "false", "false"], focusAfterTab: "Plans", allComparePanelsOpen: true, moreRetainedAfterPlansReopen: true, reducedMotion: true, panes }, null, 2) + "\n")
} finally {
  await browser.close()
}
