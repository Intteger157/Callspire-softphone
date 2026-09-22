# Callspire.Service

Headless telephony sidecar for the macOS SwiftUI app ([Callspire-softphone](https://github.com/Intteger157/Callspire-softphone)). Hosts `DesktopAppController` and speaks NDJSON over a Unix domain socket (`~/Library/Application Support/Callspire/service.sock`).

```
Callspire.Service [--socket PATH] [--parent-pid PID] [--headless]
```

See `ServiceHost.cs` for the method table and `Callspire.Mac/README.md` for how the Swift client launches this process.
