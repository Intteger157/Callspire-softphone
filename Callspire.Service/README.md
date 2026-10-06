# Callspire.Service

Headless telephony sidecar for the macOS SwiftUI app ([Callspire-softphone](https://github.com/Intteger157/Callspire-softphone)). Hosts `DesktopAppController` and speaks NDJSON over a Unix domain socket (`~/Library/Application Support/Callspire/service.sock`).

```
Callspire.Service [--socket PATH] [--parent-pid PID] [--headless]
```

All JSON is camelCase; enums are strings; `DateTime` values are written as local time **with offset**
(`2026-10-06T12:46:39.123+03:00`, see `Ipc/IpcJson.cs`) so Swift's `IpcDate` can round-trip them.

## Swift → service requests

| Area | Methods |
|---|---|
| Core | `ping`, `getState`, `shutdown`, `log` |
| Dialer | `setPhoneNumber`, `placeCall {number, slot?}`, `selectCallerId {number}`, `reconnectSlot {slot}`, `reconnectFromSettings` |
| Call window | `answer`, `reject`, `hangup`, `toggleMute`, `toggleHold`, `toggleKeypad`, `sendDtmf {digit}`, `callViewClosed`, `getActiveCall`, `callAudioDevices`, `switchCallAudioDevices {inputId?, outputId?}` (all take `sessionId`) |
| History | `refreshHistory`, `clearHistory`, `clearHistoryFilter`, `setHistoryDrillDown {drill, phoneNumber?}` — drill keys: `all/answered/unanswered/missed/failed/cancelled/incoming/outgoing/crm*` |
| Call details | `getCallDetails`, `prepareRecording`, `retryKommo`, `getKommoContactName {phoneNumber}` (`{phoneNumber, callTime}` key) |
| Statistics | `refreshStatistics`, `setStatisticsFilter {periodIndex?, connectionIndex?, directionIndex?, customFrom?, customTo?}`, `exportStatisticsCsv {targetPath}` |
| Settings | `getSettings`, `saveSettings <SettingsFields>`, `testConnection {secondary, fields}`, `refreshAudioDevices`, `previewRingtone {fields}`, `stopRingtone`, `setTheme {theme}`, `checkUpdates`, `openUpdateUrl`, `openLogsFolder`, `openRecordingsFolder`, `openExternal {target}` |
| Kommo / Gateway | `kommoAuthorize {clientId, clientSecret, redirectUri}`, `kommoOAuthStatus`, `gatewayAuthorize {url, extension}` (opens browser login, token returns via `callspire://cdr-auth?token=…`), `gatewayClear`, `gatewayStatus` |
| Logs | `getLogSnapshot` (also enables streaming), `setLogStreaming {enabled}`, `clearLog` |
| System | `handleProtocolUrl {url}`, `systemWillSleep`, `systemDidWake`, `networkAvailable`, `webRtcEngineEvent {json}`, `webRtcHostReset` |

## Service → Swift

Events: `serviceReady`, `stateSnapshot` (MainStateDto), `showCallWindow`, `callStateChanged`, `callClosed`, `bringCallWindowToFront`,
`settingsChanged`, `showSettings`, `showMessage`, `showUpdateAvailable`, `bringToForeground`, `logLines {lines}`, `webRtcDestroyHost`.

Requests (Swift must answer): `showConnectionSelection`, `showLeadSelection`, `showKommoLeadPicker`, `webRtcCreateHost {url, enableDevTools}`, `webRtcInvokeScript {script}`.

See `ServiceHost.cs`, `CallSessionHost.cs`, `SettingsSession.cs`, `CallDetailsProvider.cs`, `LogRelay.cs` for the handlers and
`Callspire.Mac/README.md` for how the Swift client launches this process.
