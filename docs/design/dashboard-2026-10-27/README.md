# Updated dashboard layout references

Source: the operator's Claude Design export from `D:\Dev\sqlharness-design-changes\2`,
imported on 2026-10-27. Files are preserved byte for byte from the export except
for the obsolete `Operation v2.dc.html` link in Mutation Approval, which now points
to the current `Operation.dc.html`.

This is the updated reference set for subsequent dashboard layout work:

- [Activity](Activity.dc.html): linked activity feed and navigation to sessions.
- [Session](Session.dc.html): session details, conversations, operations and optimization.
- [Operation](Operation.dc.html): operation details and evidence tabs.
- [Usage](Usage.dc.html).
- [Profiles](Profiles.dc.html).
- [Settings](Settings.dc.html).
- [Mutation Approval](Mutation%20Approval.dc.html).

Open an HTML file with the adjacent `support.js` available. The prototype uses
illustrative fixtures and external Google Fonts; its values are not product evidence.
The exported [screen map](github.md) maps the mock screens to React source files.
Its sync history describes the design project, not verified repository implementation.

Earlier references are available in Git history, not retained in separate folders.
The export's `archive/`, `dbg/` and `.thumbnail` were omitted.
This import updates design references only; it does
not change the shipped dashboard or mark the updated layouts as implemented.
