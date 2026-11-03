# Implementation notes

The vertical slice is implemented without claiming hardware compatibility:

- The Android route manager enumerates native input devices and rejects capture when no Ray-Ban/Meta input is present. It never falls back to the phone microphone.
- Android uses a microphone foreground service, `setCommunicationDevice()` on Android 12+, and one cleanup path for STOP, disconnect, read failure, and service destruction.
- The Go Windows sink uses WinMM and requires an installed VB-Cable `CABLE Input` endpoint. The driver is not bundled.
- Real <phone-model>/Ray-Ban route behavior and VB-Cable output still require a physical-device run. Until that run, the UI must display the explicit enumeration or sink error rather than claim compatibility.
- `<phone-model>` / Android API 36 real-device run completed: the app installed and launched, all four runtime permissions were granted through ADB for testing, and the UI reported `No input device has been enumerated. Start is blocked until Ray-Ban Meta appears.` No `AudioRecord` start was attempted and no phone-microphone fallback occurred.
- The Go core started on `127.0.0.1:17890` and `:17891`; the global hotkey was unavailable because `Ctrl+Shift+Space` was already registered by another process. Windows Flutter build remained blocked by local CMake/MSVC compiler detection (`ucrtd.lib` link failure), not by Go or Dart checks.
- The global hotkey is Ctrl+Shift+Space on Windows; it dispatches the same START/STOP path as the UI.

## Discovery and transport update

- Windows Go core now advertises `_rayban-voice-mic._tcp.local` on TCP `17891`; Android uses `NsdManager` and keeps manual host/port entry as fallback.
- TCP remains the default control/audio transport. `RAYBAN_AUDIO_TRANSPORT=udp` enables a negotiated UDP audio path on `17892`; control remains TCP, datagrams are capped at 1200 bytes, and the sender IP must match the Android TCP peer.
- Added protocol fields `HELLO.audio_transports`, `STATUS.audio_transport`, and optional `STATUS.audio_port`; Go protocol and control tests cover JSON round trips and UDP opt-in/fallback.
- `go test ./...`, `go test -race ./...`, Flutter `analyze`/`test`, and Android `assembleDebug` passed after the update. Android build reports only platform deprecation warnings for `NsdManager.resolveService` and `NsdServiceInfo.host`.
- The updated APK installed and launched on `<phone-model>` through the selected wireless ADB endpoint. The running activity was confirmed as `com.example.rayban_voice_mic/.MainActivity`; mDNS result rendering and real PCM transport were not independently asserted from the device.

## Current cleanup

- Windows discovery caches broadcast addresses and invalidates the cache only on `NetworkChange.NetworkAddressChanged`; this blocks the repeated Windows adapter-enumeration memory growth observed in the idle path.
- Windows now clears the displayed discovered peer when the discovery source reports removal.
- Android stops the previous UDP discovery instance before replacing it during service registration.
- Desktop tests: 16 passed. Android `:app:assembleDebug` passed, APK installed, and `MainActivity` launched on the <phone-model> test device without a fatal log.
- Flutter `analyze` and `test` both passed after the cleanup changes.

## Windows build and physical-device verification (2026-10-30)

- The installed MSVC/Windows SDK now passes both `flutter build windows --debug` and `flutter build windows --release`. The old `build/windows/x64` CMake cache retained Git's `link.exe` and obsolete SDK paths; it was backed up and regenerated using the Visual Studio developer environment. The normal release command then succeeded.
- The phone enumerated and selected `<glasses>`. Windows initiated a LAN connection to Android and completed authentication. The live verifier uses the production .NET `CoreController` and `NAudioSink`; it records counters only, never PCM files.
- The first physical run exposed a TCP receive defect: shared audio enqueue validation applied the 1180-byte UDP limit to Android's 4096-byte TCP frames, silently discarding them. Validation now uses the protocol payload limit. The UDP receiver still enforces its 1200-byte datagram limit before decoding.
- The existing TCP START/STOP regression now sends a 4096-byte PCM frame and asserts byte-for-byte delivery to the sink. All 16 desktop tests passed after the fix.
- A repeated 15-second physical run delivered 351 frames / 1,437,696 PCM bytes, including 636,745 nonzero samples, to VB-Cable. The CABLE Output input monitor received 1,495,680 bytes with 637,367 nonzero samples. No audio frames were dropped.
- START reported `streaming` with `<glasses>`. Android dumpsys confirmed an active Bluetooth SCO route during capture. STOP returned `connected_idle`, closed the Windows sink, and returned Android to `SCO_STATE_INACTIVE` with its A2DP connection retained. The user also heard the call-ended tone.
- The verifier was stopped and the updated Avalonia desktop application launched. Evidence is under ignored `build/live-verification-metrics.json` and `build/audio-*-verification.txt`.
- Remaining limits: this run does not verify Typeless transcription accuracy or long-duration capture. One reconnection attempt while the phone was not foreground timed out; a retry after waking the phone succeeded, so unattended reconnect reliability remains unverified.

## Flutter Windows integration (2026-10-30)

- Replaced the Windows Flutter `UnsupportedError` adapter with loopback JSON-lines IPC to the .NET core on `127.0.0.1:17890`.
- The adapter starts the repository-built `.NET` core automatically when the IPC port is unavailable; `RAYBAN_CORE_PATH` overrides the helper path for installed builds.
- The .NET core now exposes state, VB-Cable output, `START`, `STOP`, and input status through IPC. LAN control and PCM ownership remain in the existing core.
- `flutter analyze`, `flutter test`, `.NET` app build, and Flutter Windows Release build passed. Direct IPC smoke check returned the core status and `VB-Cable` input.
