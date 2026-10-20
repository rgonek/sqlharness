import assert from "node:assert/strict"
import { dirname } from "node:path"
import { fileURLToPath, pathToFileURL } from "node:url"

const here = dirname(fileURLToPath(import.meta.url))
const playwrightPath = process.env.PLAYWRIGHT_CORE_PATH ?? "D:/Dev/CivicLens/frontend/node_modules/playwright-core/index.mjs"
const { chromium } = await import(pathToFileURL(playwrightPath))
const baseUrl = process.env.UI_FIXTURE_URL ?? "http://127.0.0.1:4177/browser-fixture.html"
const executablePath = process.env.CHROMIUM_PATH ?? "C:/Users/rgone/AppData/Local/ms-playwright/chromium-1228/chrome-win64/chrome.exe"
const browser = await chromium.launch({ headless: true, executablePath })
try {
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 }, reducedMotion: "reduce" })
  page.on("pageerror", error => process.stderr.write(`PAGE ERROR: ${error.message}\n`))
  page.on("console", message => { if (message.type() === "error") process.stderr.write(`BROWSER ERROR: ${message.text()}\n`) })
  page.on("response", response => { if (response.status() >= 400) process.stderr.write(`HTTP ${response.status()}: ${response.url()}\n`) })
  const triggers = page.getByRole("button")
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" })
  const initialButtons = await triggers.count()
  assert.equal(initialButtons, 2)
  const first = page.getByRole("button", { name: /failed/ })
  const second = page.getByRole("button", { name: /rejected/ })
  const firstTrigger = page.locator("button[data-base-ui-tooltip-trigger]").nth(0)
  const secondTrigger = page.locator("button[data-base-ui-tooltip-trigger]").nth(1)

  await first.hover()
  await page.waitForTimeout(1800)
  const tooltipAfterHover = (await page.locator("body").innerText()).includes("The database returned an error while running the SQL.")
  await first.focus()
  await page.waitForTimeout(800)
  const tooltipAfterFocus = (await page.locator("body").innerText()).includes("The database returned an error while running the SQL.")
  assert.ok(tooltipAfterFocus, "focus exposes the error explanation")

  await first.focus()
  await page.keyboard.press("Enter")
  const firstDialog = page.getByRole("dialog")
  await firstDialog.waitFor()
  assert.equal(await firstTrigger.getAttribute("aria-controls"), await firstDialog.getAttribute("id"))
  assert.match(await firstDialog.innerText(), /First failure details/)
  await page.keyboard.press("Escape")
  await firstDialog.waitFor({ state: "detached" })
  assert.equal(await firstTrigger.evaluate(element => document.activeElement === element), true)

  await second.click()
  const secondDialog = page.getByRole("dialog")
  await secondDialog.waitFor()
  assert.match(await secondDialog.innerText(), /Second rejection details/)
  assert.doesNotMatch(await secondDialog.innerText(), /First failure details/)
  assert.equal(await secondTrigger.getAttribute("aria-controls"), await secondDialog.getAttribute("id"))
  process.stdout.write(JSON.stringify({ initialButtons, tooltipAfterHover, tooltipAfterFocus, keyboardOpen: true, escapeFocusReturn: true, badgeIsolation: true, controlledPopupRelation: true }, null, 2) + "\n")
} finally {
  await browser.close()
}
