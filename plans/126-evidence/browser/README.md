# Browser evidence index

Current product:5f05ca07. [Accepted run](acceptance-5f05ca07/report.md):72/72 checks, twelve axe scans without violations, five passing20-sample performance series. [Reviewable summary](acceptance-5f05ca07/acceptance-summary.json); complete raw records are in acceptance.json.gz, with SHA-256 in packed-json-manifest.json.

[Native report](native-accessibility/report.md) covers actual200% browser zoom and sampled control/graph contrast. Required NVDA remains pending an unlocked Windows desktop.

## Historical diagnostics

These runs are superseded; they do not establish final acceptance. Historical PNGs are retained at the local archive path recorded per folder. Raw JSON is losslessly gzip-compressed; manifests identify original bytes.

| Run | Checks passed | Report |
|---|---:|---|
| final | 67/72 | [Report](final/report.md) |
| final-v3 | 67/72 | [Report](final-v3/report.md) |
| final-functional | 65/67 | [Report](final-functional/report.md) |
| final-acceptance | 68/72 | [Report](final-acceptance/report.md) |

Archive reproducibility: Python gzip.decompress or any standard gzip reader restores the exact JSON. The checked-in harness writes uncompressed raw files; archival compression is performed after a run. No SQL workload or real mutation decision was sent.
