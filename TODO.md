# To do

## Watchdog tab (saved for later, 2026-11-06)

Today the Watchdog has only a checkbox in Settings, alerts in Activity and tray notifications. What it watches can only be changed by editing `settings.json` with the app closed. A Watchdog tab in the sidebar would show:

1. **Status:** on or off, the port it listens on, the last check's time, and the error if it can't listen.
2. **Connected agents:** the session ids listening now.
3. **What it watches:** each repo with its listed files, and each server with its last reading. Add, edit and remove them here instead of in `settings.json`.
4. **Recent alerts:** the last 20 or so, with kind and time.
5. **Problems:** anything it can't watch, shown until fixed.

Most of it is already in `watchdog.json` (`Sessions`, `RecentAlerts`, `Memory.Servers`, `Memory.Repos`, `Error`) and the settings. Editing the watch list is the largest part. The read-only view (1, 2, 4, 5) could come first. Estimate: 2 to 3 hours with tests.
