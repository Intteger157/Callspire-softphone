## MikoPBX dialplan patches (Callspire)

This folder contains **drop-in Asterisk dialplan fragments** that we use to patch / extend MikoPBX behaviour for Callspire-related integrations.

These files are **not** consumed by the Windows softphone directly. They are stored here so the team can:

- track the exact dialplan changes in source control,
- copy/paste them into the PBX (usually via the MikoPBX “custom dialplan” mechanism),
- and reproduce the same behaviour across test/stage/prod PBXs.

### Contents

- `telnyx-preserve-plus-e164.conf`: Fixes outbound routing where MikoPBX strips the leading `+` from an E.164 destination number (e.g. `+372…`). When the trunk/provider expects E.164, losing the `+` can cause the provider to “guess” the region and route the call incorrectly (e.g. prepending a default country code).

- `webrtc-originate-one-way-audio-fix.conf`: Fixes **one-way audio** on PBX-originated outbound calls to WebRTC (operator hears silence from callee after trunk answers). Adds `U(sub-beeline-answered)` on the Beeline `Dial()` so Asterisk sends `PJSIPSendRefresh` to the WebRTC leg when the trunk answers.

### Originate one-way audio (troubleshooting)

| Symptom | Likely cause |
|--------|----------------|
| Outbound works, inbound silent after callee answers | PBX `simple_bridge` not forwarding trunk RTP to WS after 200 OK |
| Early ringback/IVR from trunk audible, then silence | Same — early media (183) path works, post-answer bridge broken |
| Client log: `audioLevel=1 ❌ SILENCE`, `currentTime=0` | No inbound RTP packets at WebRTC peer |

**Miko log filter** (one extension / one call):

```bash
grep -E '52678419|791834f5|103\.45\.245\.132' /var/log/asterisk/full
```

Use `Call-ID` prefix from softphone debug (e.g. `791834f5-72b1-440b-bf3f-a840c2afdff6`).

**Apply fix:** merge `webrtc-originate-one-way-audio-fix.conf` into custom dialplan and add `U(sub-beeline-answered,s,1)` to your `beeline_dial` `Dial()` line (see file header).

