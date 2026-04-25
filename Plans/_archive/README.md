# Plans archive

Old session-end snapshots and intermediate research docs from the early April 2026 SpawnDev.RTC build-out. Retained for historical reference — they record the state of the project at specific moments that are now ancient history relative to the shipped product.

- `SESSION-END-2026-04-15.md` — end-of-day snapshot when Phases 1-3 (data channel) were complete.
- `SESSION-END-2026-04-16.md` — end-of-day snapshot after initial media tests landed.
- `TRACKER-COMPARISON-2026-04-16.md` — research notes comparing the minimal `RTCTrackerClient` vs the full WebTorrent `WebSocketTracker` before the migration. Outcome is captured in `PLAN-Tracker-Signaling-Migration.md` (now alongside this README in the same archive dir).
- `PLAN-SpawnDev-RTC-v0.1.0.md` — original v0.1.0 scoping doc. Self-admitted as superseded by 1.1.x; retained as a reference for "where we started." Archived 2026-04-25.
- `PLAN-Tracker-Signaling-Migration.md` — design doc for the 2026-04-22 migration that moved tracker client + server out of `SpawnDev.WebTorrent` into `SpawnDev.RTC` / `SpawnDev.RTC.Server`. SHIPPED. Archived 2026-04-25 per docs-hygiene pass; the design "why" is preserved here, day-to-day docs live at `../../Docs/signaling-overview.md` + `../../Docs/run-a-tracker.md` + `../../Docs/use-cases.md`.

For the current state of the library read the top-level `Plans/` files or the `Docs/` folder. These archive files are **not** current; don't use them to reason about today's codebase.
