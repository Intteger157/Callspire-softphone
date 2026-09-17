# macOS smoke checklist (SwiftUI + sidecar)

After `Callspire.Mac/Scripts/package-dmg.sh --arch arm64`:

1. Dialer — sidebar active state; numpad; Call button; status rows reconnect.
2. Place a SIP call (if a line is configured) — Call window mute/hold/hangup.
3. WebRTC — main line registers; hidden WKWebView created; incoming/outgoing WebRTC call.
4. Settings → Connection / Second line / Audio (codec + devices) / Kommo (OAuth + gateway) / Appearance (light/dark) / Advanced / About.
5. History → open details → play recording (if any) → Kommo retry.
6. Statistics filters + CSV export + KPI drill-down to history.
7. `callspire://call?number=123` from a browser focuses the app and fills the dialer.
8. Sleep/wake — SIP reconnects; WebRTC host reset does not crash.
9. Logs window tails sidecar output.
10. Update sheet appears if the update server reports a newer version.
