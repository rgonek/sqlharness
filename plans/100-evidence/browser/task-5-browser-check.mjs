import assert from "node:assert/strict"
import { chromium } from "file:///D:/Dev/CivicLens/frontend/node_modules/playwright-core/index.mjs"

const browser = await chromium.launch({ headless: true, executablePath: process.env.CHROMIUM_PATH ?? "C:/Users/rgone/AppData/Local/ms-playwright/chromium-1228/chrome-win64/chrome.exe" })
try {
  const page = await browser.newPage({ viewport: { width: 390, height: 844 }, reducedMotion: "reduce" })
  await page.route(url => new URL(url).pathname.startsWith("/api/"), route => route.fulfill({ status: 200, contentType: "application/json", body: "{}" }))
  await page.goto(process.env.UI_FIXTURE_URL ?? "http://127.0.0.1:4173/task-5-browser-fixture.html", { waitUntil: "domcontentloaded" })
  await page.getByRole("heading", { name: "Dimension controls browser fixture" }).waitFor()
  const initialWidth = await page.evaluate(() => ({ viewport: innerWidth, document: document.documentElement.scrollWidth }))
  assert.ok(initialWidth.document <= initialWidth.viewport, JSON.stringify(initialWidth))
  await page.getByRole("combobox", { name: "region" }).click()
  await page.getByRole("option", { name: "All values" }).waitFor()
  await page.getByRole("option", { name: "Unknown (missing)" }).waitFor()
  await page.getByRole("option", { name: "(empty)" }).waitFor()
  await page.keyboard.press("Escape")
  await page.getByRole("combobox", { name: "Columns" }).click()
  const listbox = page.locator('[role="listbox"]:visible').last()
  await listbox.waitFor()
  const options = await listbox.locator('[role="option"]').evaluateAll(nodes => nodes.map(node => ({ text: node.textContent?.trim(), disabled: node.getAttribute("aria-disabled") })))
  const disabled = options.find(option => option.text === "component")
  assert.equal(disabled?.disabled, "true", JSON.stringify(options))
  await page.screenshot({ path: process.env.SCREENSHOT_PATH ?? ".superpowers/sdd/100-shadcn-primitive-adoption/scratch/task-5/narrow-layout.png", fullPage: true })
  process.stdout.write(JSON.stringify({ viewport: 390, document: initialWidth.document, filterChoices: ["All values", "Unknown (missing)", "(empty)"], disabledColumnChoice: disabled }) + "\n")
} finally { await browser.close() }
