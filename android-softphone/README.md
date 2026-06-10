# Callspire Android (Kotlin)

Android softphone built with Kotlin + Jetpack Compose; auth and APIs go through **Callspire PBX Gateway** (same HTTP API as the Windows app’s gateway URL).

## Scope

- Bottom navigation: Dialpad / History / Settings
- Auth against PBX Gateway (`/auth/login`)
- CallerID list (`/api/my-callerids`)
- Outbound originate (`/api/originate`)
- Optional direct SIP mode from Android (built-in Android SIP API)
- Call history from CDR (`/api/cdr`)
- Local settings/token storage (DataStore)
- No call recording
- No amoCRM integration

## Architecture

- `domain/` - models and `SipClient` contract
- `data/` - `ServerBackedSipClient` and `SipConfigStore`
- `viewmodel/` - orchestration for registration and call actions
- `ui/` - Compose screen

All server endpoints are abstracted behind `SipClient`, so integration logic can evolve without UI rewrites.
Direct SIP path is implemented in `DirectSipClient` and uses the same `Host/Extension/Password` fields.

## Run

1. Open `android-softphone` as Android Studio project.
2. Sync Gradle.
3. Run `app` on device/emulator (Android 8+).

## Next step

- Add secure token refresh flow
- Add websocket/AMI-based live call state updates
- Add TLS certificate pinning
