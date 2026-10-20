import assert from "node:assert/strict"
import { dirname, join } from "node:path"
import { fileURLToPath, pathToFileURL } from "node:url"

const here = dirname(fileURLToPath(import.meta.url))
const playwrightPath = process.env.PLAYWRIGHT_CORE_PATH ?? "D:/Dev/CivicLens/frontend/node_modules/playwright-core/index.mjs"
const { chromium } = await import(pathToFileURL(playwrightPath))
const baseUrl = process.env.UI_FIXTURE_URL ?? "http://127.0.0.1:4174/browser-fixture.html"
const executablePath = process.env.CHROMIUM_PATH ?? "C:/Users/rgone/AppData/Local/ms-playwright/chromium-1228/chrome-win64/chrome.exe"
const hashes = ["a", "b", "c"].map(char => char.repeat(64))
const longCaseLabel = "Case Alpha with a deliberately long retained display label for the plan picker on a narrow phone screen"
const occurrences = [1, 2].flatMap(repetition => [
  { variant: "measure", parameterSet: null, caseId: "case-a", caseLabel: longCaseLabel, repetition, documentOrdinal: 0, statementIndex: 0, spanStart: null, spanLength: null, statementId: 1, statementCompId: 1, documentSha256: hashes[0], shapeHash: "shape", format: "showplan-xml", matchKind: "exact", omitted: false, stored: true },
  { variant: "measure", parameterSet: null, caseId: "case-a", caseLabel: longCaseLabel, repetition, documentOrdinal: 1, statementIndex: 0, spanStart: null, spanLength: null, statementId: 1, statementCompId: 1, documentSha256: hashes[1], shapeHash: "shape", format: "showplan-xml", matchKind: "exact", omitted: false, stored: true },
  { variant: "measure", parameterSet: null, caseId: "case-a", caseLabel: longCaseLabel, repetition, documentOrdinal: 1, statementIndex: 1, spanStart: null, spanLength: null, statementId: 1, statementCompId: 1, documentSha256: hashes[1], shapeHash: "shape", format: "showplan-xml", matchKind: "exact", omitted: false, stored: true },
])
const node = (id, statementIndex) => ({ id, statementIndex, showplanNodeId: 1, physicalOperator: "Index Seek", logicalOperator: "Index Seek", object: null, tableReferenceId: null, predicates: [], definedValues: [], estimatedRows: 10, actualRows: 5, executions: 1, costFraction: 1, warnings: [] })
const graph = { engine: "sqlserver", statements: [{ index: 0, statementId: 1, statementCompId: 1, rootNodeId: "0:0" }, { index: 1, statementId: 1, statementCompId: 1, rootNodeId: "1:0" }], nodes: [node("0:0", 0), node("1:0", 1)], edges: [] }
const json = (data) => ({ status: 200, contentType: "application/json", body: JSON.stringify(data) })
const browser = await chromium.launch({ headless: true, executablePath })
try {
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 }, reducedMotion: "reduce" })
  page.on("pageerror", error => process.stderr.write(`PAGE ERROR: ${error.message}\n`))
  page.on("console", message => { if (message.type() === "error") process.stderr.write(`BROWSER ERROR: ${message.text()}\n`) })
  page.on("response", response => { if (response.status() >= 400) process.stderr.write(`HTTP ${response.status()}: ${response.url()}\n`) })
  await page.route("**/*", async route => {
    const url = new URL(route.request().url())
    if (!url.pathname.startsWith("/api/")) return route.continue()
    if (url.pathname === "/api/operations/10/plans") return route.fulfill(json({ operationId: 10, state: "stored", occurrences }))
    if (url.pathname === "/api/operations/10/source-map") return route.fulfill(json({ operationId: 10, state: "stored", engine: "sqlserver", variant: "measure", parameterSet: null, caseId: "case-a", caseLabel: longCaseLabel, repetition: Number(url.searchParams.get("repetition")), executedText: "SELECT 1;", algorithmVersion: 1, links: [{ documentSha256: hashes[0], documentOrdinal: 0, link: { nodeId: "0:0", spanStart: 0, spanLength: 8, kind: "statement", state: "exact", rule: "statement" } }], omittedDocuments: [], errors: [] }))
    if (url.pathname.endsWith("/graph")) return route.fulfill(json({ engine: "sqlserver", graph, error: null }))
    if (url.pathname.endsWith("/properties")) return route.fulfill(json({ engine: "sqlserver", nodeId: "0:0", tree: null, error: null }))
    return route.fulfill({ status: 404, contentType: "application/json", body: JSON.stringify({ message: "unmapped fixture API" }) })
  })
  await page.goto(baseUrl, { waitUntil: "domcontentloaded" })
  await page.evaluate(theme => document.documentElement.classList.toggle('dark', theme === 'dark'), process.env.THEME ?? 'light')
  const casePicker = page.getByRole("combobox", { name: "Case" })
  await casePicker.waitFor({ timeout: 5000 })
  assert.match(await casePicker.innerText(), /Case Alpha with a deliberately long retained display label/)
  assert.doesNotMatch(await casePicker.innerText(), /aaaaaaaaaaaaaaaa|case-a/)

  await casePicker.click()
  const listbox = page.locator(`#${await casePicker.getAttribute("aria-controls")}`)
  const longOption = listbox.getByRole("option", { name: /Case Alpha with a deliberately long/ })
  await longOption.waitFor()
  const popup = await listbox.evaluate(element => {
    const bounds = element.getBoundingClientRect()
    const option = element.querySelector('[role="option"]')
    const optionBounds = option.getBoundingClientRect()
    return { x: bounds.x, right: bounds.right, width: bounds.width, clientWidth: element.clientWidth, scrollWidth: element.scrollWidth,
      optionWidth: optionBounds.width, optionTextWidth: option.scrollWidth, optionTextClientWidth: option.clientWidth }
  })
  assert.ok(popup.right <= 1280, `popup exceeds desktop viewport: ${JSON.stringify(popup)}`)
  await page.keyboard.press("Escape")

  await page.setViewportSize({ width: 320, height: 800 })
  await casePicker.scrollIntoViewIfNeeded()
  const mobileTrigger = await casePicker.boundingBox()
  assert.ok(mobileTrigger && mobileTrigger.x >= 0 && mobileTrigger.x + mobileTrigger.width <= 320, `trigger exceeds mobile viewport: ${JSON.stringify(mobileTrigger)}`)
  await casePicker.click()
  const mobilePopup = await listbox.evaluate(element => {
    const bounds = element.getBoundingClientRect()
    const option = element.querySelector('[role="option"]')
    return { x: bounds.x, right: bounds.right, width: bounds.width, clientWidth: element.clientWidth, scrollWidth: element.scrollWidth,
      optionTextWidth: option.scrollWidth, optionTextClientWidth: option.clientWidth }
  })
  assert.ok(mobilePopup.x >= 0 && mobilePopup.right <= 320, `popup exceeds mobile viewport: ${JSON.stringify(mobilePopup)}`)
  await page.keyboard.press("Escape")
  const sourceMatch = page.getByRole("button", { name: /Index Seek; exact source match/ })
  await sourceMatch.scrollIntoViewIfNeeded()
  await sourceMatch.click()
  await page.waitForFunction(() => document.querySelector('[aria-label="Source match rule"]') !== null)
  assert.equal(await sourceMatch.getAttribute("aria-pressed"), "true", "pointer source-match selection was not reflected on the real PlanWorkbench node")
  assert.ok(await page.getByText("statement", { exact: true }).count(), "selected source-match rule was not shown")
  await page.locator(".plan-source-highlight").first().waitFor({ timeout: 10000 })
  const reveal = await page.locator(".plan-source-highlight").count()
  assert.ok(reveal > 0, "pointer source-match selection did not highlight/reveal its SQL span")
  await page.screenshot({ path: join(here, "task11-7-theme.png"), fullPage: true })
  process.stdout.write(JSON.stringify({ longLabelInitial: true, popupDesktop: popup, popupMobile: mobilePopup, triggerMobile: mobileTrigger }, null, 2) + "\n")
} finally {
  await browser.close()
}
