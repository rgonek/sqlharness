# Dashboard layout references

Source: the operator's Claude Design export from `D:\Dev\sqlharness-design-changes`.

Selected by the operator on 2026-10-25:

- Activity: 3a, including the session-grouped feed from 1c.
- Operation: Operation v2.
- Session Optimization: turn 4 (4a), including its final IO tab derived from Table IO 2a.
- Settings: the six-section layout in Settings.dc.html.

Implement these layouts in the existing React dashboard, preserving real API data,
pagination, live updates, accessible controls, plan navigation and operator approval.
Reference fixtures are illustrative, never a source of measurements or capabilities.

Implementation order: Activity and shared navigation; operation tabs; session
comparison tabs and IO; settings regrouping; focused UI checks and `just verify`.

The HTML exports and support.js are design references only and are not shipped as
the application. Older alternatives and debug backups were not imported.
