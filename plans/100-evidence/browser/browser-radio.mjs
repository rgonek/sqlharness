import assert from "node:assert/strict"
import { chromium } from "file:///D:/Dev/CivicLens/frontend/node_modules/playwright-core/index.mjs"
const theme = process.env.THEME ?? "light"
const browser = await chromium.launch({ headless: true, executablePath: "C:/Users/rgone/AppData/Local/ms-playwright/chromium-1228/chrome-win64/chrome.exe" })
try {
  const page = await browser.newPage({ viewport: { width: 320, height: 800 }, reducedMotion: "reduce" })
  await page.goto("http://127.0.0.1:4173/task11-radio.html", { waitUntil: "domcontentloaded" })
  await page.evaluate(theme => document.documentElement.classList.toggle("dark", theme === "dark"), theme)
  const a0 = page.getByRole("radio", { name: "A v0" })
  const a1 = page.getByRole("radio", { name: "A v1" })
  const a2 = page.getByRole("radio", { name: "A v2" })
  const b2 = page.getByRole("radio", { name: "B v2" })
  assert.equal(await a0.isChecked(), true)
  await a1.check()
  assert.equal(await a1.isChecked(), true)
  assert.equal(await a0.isChecked(), false)
  await a1.focus()
  await page.keyboard.press("ArrowDown")
  assert.equal(await a2.isChecked(), true)
  assert.equal(await b2.isChecked(), true)
  await page.screenshot({ path: `.superpowers/sdd/100-shadcn-primitive-adoption/scratch/task-11/browser-radio-${theme}.png`, fullPage: true })
  process.stdout.write(JSON.stringify({ theme, nativeRadioGroups: true, arrowSelection: true, independentABGroups: true, viewport: 320 }) + "\n")
} finally { await browser.close() }
