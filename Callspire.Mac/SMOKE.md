# macOS smoke checklist (SwiftUI + sidecar)

After `Callspire.Mac/Scripts/package-dmg.sh --arch arm64`:

1. Main window — sidebar (account avatar + Dialer / Call History / Call Statistics, Settings & Logs at the bottom); toolbar shows account + online dot.
2. Dialer — line status rows (● text, reconnect ↻), AmoCRM row, number field with backspace, Caller ID picker, keypad, green Call (split buttons when both lines are up).
3. Call window — caller / status / timer / transport badge; mute, hold, speaker (audio device sheet: WebRTC enumerate+switch, SIP PortAudio), keypad (inline DTMF), red hang-up; incoming shows Answer/Reject; window closes itself when the call ends; closing it manually keeps the call (`callViewClosed`).
4. Call History — cards with direction icon, badge, time, duration, coloured status, green call-back; "Clear History" asks for confirmation; drill-down chip with "Show all".
5. Call Details sheet — Call Information (+Copy), Timing Information, AmoCRM (lead link, contact name lookup, upload status), Send result to CRM (Play / Retry upload via Gateway), Technical Details tabs All/AmoCRM/WebRTC/SIP + Copy, Close.
6. Statistics — Period / Connection / Direction filters (custom range pickers), KPI cards (click → history drill-down), Calls by day + Activity by hour charts, Outcome breakdown, SIP vs WebRTC, AmoCRM lines (clickable), Outbound Caller ID, Top numbers, Export CSV (save panel → Finder reveal).
7. Settings window (⌘,) — Connection (Primary card, transport segmented, Test Connection, Save and Connect, Turn settings disclosure, Add additional connection), Audio (devices / codec / ringtone + preview, Save Audio Settings), General (Call Recording switch, Open Recordings Folder), Appearance (theme → Apply Theme live), Advanced (diagnostics), Integrations (Kommo switch, Gateway/Local source, OAuth authorize, Upload recordings, Manual lead; PBX Gateway URL/extension, Authorize → browser → `callspire://cdr-auth?token=…` → ● Connected, Clear settings), About (version, Check for Updates). Unsaved edits are not clobbered by `settingsChanged`; Close warns when dirty.
8. `callspire://call?number=123` from a browser focuses the app and fills the dialer; `tel:` links work too.
9. Sleep/wake — SIP reconnects; WebRTC host reset does not crash.
10. Logs window (⇧⌘L) — snapshot on open, live tail, filter, Copy, Clear, Open Folder; streaming stops when closed.
11. Update sheet appears if the update server reports a newer version; message alerts render as native alerts.
