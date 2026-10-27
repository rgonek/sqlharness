# UI information and editor resize

Product candidate: `58a29997`, branch `dashboard-shadcn-fixes`; isolated worktree retained. Earlier standard Plan radio / Advanced / document popup fixes are included from `aaa346e0`. No generated shadcn primitive or backend changes in this scope.

- Conversations moved into the existing top Details, with full conversation IDs; Session ID also remains in Details.
- Add note precedes Optimization in both ordinary and agent sessions; editing and child-history membership are retained.
- Redundant Select comparison removed. Explicit A/B, case, aggregate and view selection remain; the former action only pinned/reset URL context.
- Native vertical resize handles cover Monaco single-side SQL, both diff modes and Plan SQL. Default heights remain 320/250px, minimum 128px, maximum 1200px. Monaco `automaticLayout` follows the frame; no new package or primitive edits.
- Removed duplicate pair/context/help text and collapsed empty statement metric labels. Important evidence limitations remain. [Full information audit](information-audit.md).

## Checks

- TypeScript and lint passed before the full gate.
- Focused tests: 3 files, **39/39 passed**, including note/title editing, multi-history A/B navigation and hidden-then-visible Conversations. An intermediate run caught the expected additional hidden Session ID; the test now checks visibility rather than raw DOM count.
- Independent scoped source review: no findings; checked information retention, annotation controls, A/B behavior and automatic editor layout.
- [Browser harness](../../visual/harness/ui-information-check.mjs) runs against built production assets with isolated browser-only fixtures. [Results](results.json): real pointer drag increases side-by-side 320→460, unified 460→600, plan 250→390 and single-side SQL 320→460px; Monaco follows within the 2px frame border. Details/placement/removal checks pass; 390px document has no horizontal overflow; no page errors.
- [Screenshots](index.html) inspected visually at 1280 and 390px. Browser fixture journal/database writes: none.

The pre-existing native Monaco ResizeObserver window ErrorEvent was already reproduced on the earlier installed baseline (see previous mock-alignment verification). These checks monitor Playwright page errors; they do not claim that baseline native warning was eliminated.

## Final gate and delivery

`just verify -LogDirectory D:/temp/sqlharness-ui-information-final-gate` passed on 58a29997: 5 stages / 12m59s. UI 386 (68 files), Core 4722 (11 skipped), MCP 535 (4 skipped); TypeScript/lint passed; build zero warnings/errors. Format correctly skipped for zero changed C# files. [Raw logs](gate/ui-check.log). Published locally from 58a29997 and ran `sqlharness dashboard --restart` successfully. Dashboard PID 11820 / port 47800. Installed EXE SHA256 matches the published binary; all 20 served UI assets match the build, including fonts and editor worker. The complete pointer-resize/Details/placement/390px fixture browser check also passed on the installed server. [Delivery receipt](delivery.json), [asset hashes](installed-assets.json), [installed checks](installed/results.json). The prior dashboard endpoint file was absent before restart; old PID is intentionally null in the receipt. No merge or push.
