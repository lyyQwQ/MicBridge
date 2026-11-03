# Implementation Plan

## Ordered checklist

1. Re-read the final PRD, design, backend spec, frontend state spec, and affected source files. Confirm the current implementation has no uncommitted product edits from the audit.
2. Define the authentication frame/fields and failure codes in the shared protocol contract. Implement the smallest challenge-response flow in .NET and Kotlin, with bounded payloads, rate limits, and no secret logging.
3. Add cross-language protocol tests for authentication success, wrong secret, replay/expired challenge, malformed proof, and legacy unauthenticated rejection.
4. Refactor Android capture error propagation so route failure, `AudioRecord` failure, read failure, send failure, stop, disconnect, and service destruction share idempotent cleanup and notify the Windows peer.
5. Fix Windows state transitions so Android capture confirmation controls `Streaming`; failed commands update `CoreSnapshot`, close the sink, and preserve idempotent START/STOP behavior.
6. Fix bounded audio queue accounting and add a test that fills the queue and asserts the dropped-frame count.
7. Repair discovery: join multicast, send per-interface directed broadcasts, preserve independent failure handling, expire/remove stale peers, and add tests for update/removal/manual fallback.
8. Fix Windows auto-connect retry state and add a test for initial connection failure followed by a later successful endpoint.
9. Remove the unused Go backend and Flutter Windows legacy adapter only after whole-tree reference search confirms no active caller. Remove only the Go dependency and docs that become obsolete.
10. Add Android tests for device selection, communication-route cleanup, and capture error propagation. Keep a real-device smoke checklist for Ray-Ban route activation and release.
11. Configure Android release signing through untracked/environment configuration. Keep the two Flutter/AGP compatibility flags until Flutter's plugin no longer casts the AGP 9.1 extension to `AbstractAppExtension`; document the upgrade condition and verify no credentials enter the repository.
12. Redesign Android Flutter and Windows Avalonia screens using the approved dark, compact, status-first direction. Keep manual IP, pairing code, explicit route status, disabled-action reasons, and Chinese labels.
13. Update README and protocol/spec documentation for pairing, firewall scope, discovery fallback, release signing, and the smoke flow.
14. Run full validation and the actual surface smoke checks before implementation completion.

## Validation commands

- `go test ./...` if the Go backend remains during intermediate work; otherwise verify no remaining Go references.
- `dotnet test desktop/RaybanVoiceMic.slnx -c Release --no-restore`
- `flutter analyze`
- `flutter test`
- `cmd /c gradlew.bat assembleDebug --no-daemon --console=plain`
- Release configuration build without a committed signing secret.
- Real-device smoke: discover or manually connect, authenticate, show Ray-Ban input, start, confirm VB-CABLE activity, stop, confirm route release, disconnect, and retry.
- Negative smoke: wrong pairing code, no Ray-Ban input, Bluetooth route loss, Windows firewall blocked, stale endpoint, repeated START/STOP.

## Risky files and rollback points

- Protocol: `desktop/src/RaybanVoiceMic.Core/Protocol/Frame.cs`, Android `Protocol.kt`, and the corresponding tests. Roll back only as a cross-language set.
- Session lifecycle: `CoreController.cs`, `ControlClient.kt`, `MicrophoneService.kt`, and `AudioCaptureController.kt`. Preserve cleanup behavior before changing UI.
- Discovery: .NET `UdpDiscovery.cs`/`MdnsBrowser.cs` and Android `UdpDiscovery.kt`/`LanDiscovery.kt`. Keep manual TCP working if discovery changes fail.
- UI: `lib/main.dart`, `lib/platform_bridge.dart`, `MainWindow.axaml`, and `MainWindowViewModel.cs`. Do not move protocol parsing into widgets.
- Release config: `android/app/build.gradle.kts` and untracked signing properties. Stop if a secret would be written into the repository.

## Review gates before start

- Authentication threat model and pairing UX are explicit.
- No active caller depends on the Go or Flutter Windows legacy path before deletion.
- Acceptance criteria map to tests or named real-device smoke checks.
- UI keeps the existing single-session and no-phone-microphone invariants.
