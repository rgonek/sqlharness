import assert from "node:assert/strict"
import { dirname, join } from "node:path"
import { fileURLToPath, pathToFileURL } from "node:url"
const here = dirname(fileURLToPath(import.meta.url))
const playwrightPath = process.env.PLAYWRIGHT_CORE_PATH ?? "D:/Dev/CivicLens/frontend/node_modules/playwright-core/index.mjs"
const { chromium } = await import(pathToFileURL(playwrightPath))
const executablePath = process.env.CHROMIUM_PATH ?? "C:/Users/rgone/AppData/Local/ms-playwright/chromium-1228/chrome-win64/chrome.exe"
const baseUrl = process.env.UI_FIXTURE_URL ?? "http://127.0.0.1:4173/task11-pages.html"
const theme = process.env.THEME ?? "light"
const browser = await chromium.launch({ headless: true, executablePath })
try {
  const page = await browser.newPage({ viewport: { width: 320, height: 900 }, reducedMotion: "reduce" })
  await page.route('**/api/live**', route => route.fulfill({ status: 200, headers: { 'Content-Type': 'text/event-stream' }, body: '' }))
  await page.route('**/api/mutations/events**', route => route.fulfill({ status: 200, headers: { 'Content-Type': 'text/event-stream' }, body: '' }))
  await page.route('**/favicon.ico', route => route.fulfill({ status: 204, body: '' }))
  page.on("pageerror", error => process.stderr.write(`PAGE ERROR: ${error.message}\n`))
  page.on("console", message => { if (message.type() === "error") process.stderr.write(`BROWSER ERROR: ${message.text()}\n`) })
  page.on("response", response => { if (response.status() >= 400) process.stderr.write(`HTTP ${response.status()}: ${response.url()}\n`) })
  await page.goto(`${baseUrl}?page=settings`, { waitUntil: "domcontentloaded" })
  await page.evaluate(theme => document.documentElement.classList.toggle("dark", theme === "dark"), theme)
  await page.getByRole("heading", { name: "Settings" }).waitFor()
  const sound = page.locator("#approval-sound")
  await sound.waitFor()
  await page.waitForFunction(() => document.querySelector("#approval-sound")?.textContent?.includes("Warm chime"))
  const soundBounds = await sound.boundingBox()
  assert.ok(soundBounds && soundBounds.x >= 0 && soundBounds.x + soundBounds.width <= 320, `Settings sound select overflows at 320px: ${JSON.stringify(soundBounds)}`)
  await page.screenshot({ path: join(here, `browser-settings-${theme}.png`), fullPage: true })
  const settings = { heading: "Settings", approvalSound: await sound.innerText(), bounds: soundBounds, documentWidth: await page.evaluate(() => document.documentElement.scrollWidth) }

  await page.goto(`${baseUrl}?page=stats`, { waitUntil: "domcontentloaded" })
  await page.evaluate(theme => document.documentElement.classList.toggle("dark", theme === "dark"), theme)
  await page.getByRole("heading", { name: "Usage" }).waitFor()
  const profile = page.getByRole("combobox", { name: "Statistics profile" })
  await page.waitForFunction(() => document.querySelector("#stats-profile")?.textContent?.includes("Production Profile"))
  const profileText = await profile.innerText()
  assert.match(profileText, /Production Profile with a deliberately long retained label for narrow screens/)
  const profileBounds = await profile.boundingBox()
  assert.ok(profileBounds && profileBounds.x >= 0 && profileBounds.x + profileBounds.width <= 320, `Stats profile select overflows at 320px: ${JSON.stringify(profileBounds)}`)
  const profileMetrics = await profile.evaluate(element => ({ clientWidth: element.clientWidth, scrollWidth: element.scrollWidth, text: element.textContent?.trim() }))
  assert.ok(profileMetrics.clientWidth > 0, "Stats profile select has no usable width")
  await page.screenshot({ path: join(here, `browser-stats-${theme}.png`), fullPage: true })
  process.stdout.write(JSON.stringify({ theme, viewport: 320, settings, stats: { profileText, bounds: profileBounds, metrics: profileMetrics } }, null, 2) + "\n")
} finally { await browser.close() }
