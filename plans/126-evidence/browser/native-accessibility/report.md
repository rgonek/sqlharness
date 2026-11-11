# Native accessibility evidence

Focused Plan 126 evidence. This does not establish a category score, a complete accessibility audit or a full verification gate.

## Screen reader: pending

Actual portable NVDA 2026.3.1 AMD64 started with a dedicated temporary configuration and the silence synthesizer. Its speech pipeline logged `Secure Desktop`; the foreground was `LockScreenControllerProxyWindow`, rather than the owned synthetic browser. The locked Windows session prevented a reliable screen-reader workflow. Native input and the owned NVDA instance were stopped. No unlock or bypass was attempted.

[Sanitized native log evidence](screenreader-blocked.json) records the version, verified installer hash/signature, relevant messages and pending workflows. Raw logs stay outside the repository. No audible listening, braille or blind-user testing occurred. DOM focus checks cannot replace this missing evidence. Resume requires the operator to report an unlocked desktop; the harness also verifies native foreground ownership before every native key.

Portable options/configuration follow the official [NVDA user guide](https://api.nvaccess.org/documentation/userGuide.html) and [2026.3.1 configuration schema](https://github.com/nvaccess/nvda/blob/release-2026.3.1/source/config/configSpec.py). The [official release](https://www.nvaccess.org/post/nvda-2026-3-1/) supplied the installer. Only a new temporary portable directory was created; no machine installation or startup setting changed.

## Non-text contrast

[Computed measurements](current-static/contrast.json) cover actual Settings controls, the session checkbox, selected star and keyboard focus in both themes. Chromium canvas resolves computed CSS colours to sRGB; alpha is composited through ancestor backgrounds before the WCAG luminance ratio. HTML backgrounds are measured separately from SVG fills/strokes. Borders are measured against both adjoining surfaces. Rounded anti-aliasing is not sampled.

| Sample | Light | Dark |
|---|---:|---:|
| Unchecked radio border, minimum adjoining surface | 3.96 | 4.07 |
| Input border, minimum adjoining surface | 3.15 | 4.07 |
| Unchecked switch track against surface | 3.96 | 4.41 |
| Unchecked switch thumb against track | 3.96 | 4.32 |
| Unchecked session checkbox, minimum adjoining surface | 3.96 | 3.47 |
| Selected session star, fill and stroke | 5.02 | 13.80 |
| Focused input's solid 2px outline | 19.90 | 19.06 |
| Five chart colour roles, minimum at full opacity | 3.35 | 5.06 |

Before the shared token changes, unchecked borders measured 1.27 light / approximately 1.46 dark; the selected light star's stroke was 2.13. The table records the compiled changes, rather than those superseded values. Disabled controls and decorative dividers are not assigned a 3:1 control-boundary requirement.

[Graph measurements](current-static/graph.json) use explicitly intercepted synthetic exact, inferred and ambiguous source links. Rendered edges measure 6.31 light / 7.59 dark. Minimum node-border contrast against either adjoining surface is 3.08 light / 4.75 dark. Labels and solid/dashed patterns also identify confidence. This establishes rendered sample contrast, not a retained-plan backend contract.

Chart role measurements cover five full-opacity palette tokens against the main surface. They do not establish every rendered chart, heatmap alpha level, adjacent-series pair or graph state. The sampled controls and graph nodes meet 3:1 against their relevant adjoining surfaces; a complete manual visual-contrast audit remains broader than these samples.

## Native 200% browser zoom

[Native zoom observations](current-static/zoom.json) use a temporary unpacked Chrome extension's `chrome.tabs.setZoom(tabId, 2)` in a headed browser with a dedicated temporary profile. Each view records 1440 CSS pixels / DPR 1 changing to 720 CSS pixels / DPR 2. Visual viewport scale remains 1, as expected for browser page zoom rather than pinch zoom.

Activity, session comparison, operation comparison and Settings require route-specific loaded-state assertions before capture. An initial early screenshot run caught loading placeholders and was rejected as visual acceptance. Page-only CDP capture avoids Playwright's clipping with native page zoom. The recorded JSON and screenshots identify the actual final observations and any errors.

## Reproduction and limits

Use an isolated synthetic journal and its current `dashboard.json`, never an operator home. The local cookie is read without placing it in URLs or artifacts. Persistent API methods are blocked. When inspecting a separately served static UI, GETs forward to the isolated backend. Live EventSource is held open locally because reconnection is outside this style audit. Graph mode explicitly intercepts synthetic plan APIs. No SQL execution or mutation decision is sent.

```powershell
$env:NATIVE_HOME = 'D:/temp/<isolated-synthetic-home>'
$env:NATIVE_ORIGIN = 'http://127.0.0.1:<dashboard-port>'
$env:NATIVE_MODE = 'contrast' # also graph or zoom
$env:SQLHARNESS_VISUAL_ROOT = (Get-Location).Path
node plans/126-evidence/harness/native-accessibility.mjs plans/126-evidence/browser/native-accessibility/current-static
```

The harness uses the existing Playwright toolkit and temporary extension/fixture helper under `D:/temp/sqlharness-dashboard-audit`. Each JSON records timestamps, browser version, Git revision, current source-diff SHA256 and loaded compiled asset hashes. The hashes identify the actual UI exercised. This evidence does not replace final release integration or Windows `just verify`. Screen-reader acceptance remains pending.

## Final Release repetition

The final isolated Release `5f05ca07` repeats the control measurements (eight checks), graph samples (two checks) and actual browser zoom200% (four routes). All three runs have no harness/forwarding errors and an empty source diff. [Current controls](final-5f05ca07/contrast.json), [current graph](final-5f05ca07/graph.json) and [current zoom](final-5f05ca07/zoom.json) are the current evidence; current-static is a historical iteration.

Activity, session comparison, operation comparison and Settings all report nativeZoom=true and reflow=true. Focus outline measurements repeat19.90:1 light/19.06:1 dark. The main final browser matrix additionally passes twelve axe scans across six views and both themes, including the actual SQL diff. This closes the native-zoom requirement in the browser report's generic limitations; it does not close the separate NVDA requirement or establish a universal accessibility certification.
