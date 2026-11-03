# Technical Design

## 1. Boundaries

- Android presentation remains Flutter. Kotlin owns discovery, TCP, authentication, Bluetooth route activation, `AudioRecord`, foreground service, and cleanup.
- Windows presentation remains Avalonia + ShadUI. `.NET 10` `CoreController` owns TCP, optional UDP audio, discovery, state transitions, bounded audio queue, and `IAudioSink`.
- VB-CABLE remains an external Windows prerequisite. No driver or virtual-device implementation is added.
- The Go backend and Flutter Windows legacy adapter are removed if no current product caller is found during implementation. The .NET protocol implementation becomes the only Windows path.

## 2. Connection and authentication

- Keep discovery payloads unauthenticated and capability-only. A discovery result is never an authorization decision.
- Add a bounded challenge-response exchange after TCP connection and before normal control frames. The Windows side creates a short-lived session challenge; both sides compute an HMAC over protocol version, device IDs, connection IDs, and challenge using the user-entered pairing secret.
- The pairing secret is entered through the redesigned UI and held in memory for the active session. It is never logged or written to the repository. Failed attempts are rate-limited and the socket is closed after an invalid proof.
- Keep manual host/port entry. Authentication failure must return a typed protocol error and a Chinese UI message.
- Use standard cryptography available in .NET and Android; do not add a crypto dependency.

## 3. Audio state and failure flow

- Extend the existing status/error path rather than adding a second event system.
- Android capture accepts an error callback from `ControlClient`/`MicrophoneService`. Route activation failure, `AudioRecord` initialization failure, read failure, and send failure produce one error frame and close local resources.
- Windows does not publish long-lived `Streaming` until the Android side confirms capture. A failed Android status closes the sink and publishes `Error`.
- Stop, disconnect, route failure, service destruction, and authentication failure all use one idempotent cleanup path.
- Keep the existing “no phone microphone fallback” invariant.

## 4. Discovery and reconnection

- Preserve mDNS and UDP as independent best-effort mechanisms.
- Windows UDP discovery sends multicast, global broadcast, and each usable interface broadcast. Receivers join the multicast group on usable IPv4 interfaces and continue to answer unicast announcements.
- Add peer timestamps/TTL and explicit removal notifications. A changed address replaces the old endpoint for the same device ID.
- Windows auto-connect stores the current endpoint, clears the retry guard after failure/disconnect, and performs bounded retries. It never retries an invalid role or malformed capability record.
- Manual TCP remains available when every discovery mechanism fails.

## 5. Queue and command correctness

- Keep the bounded 32-frame queue. Change the write/drop accounting so every discarded frame increments `DroppedAudioFrames` exactly once.
- Windows view-model commands consume `ResponsePayload`, publish returned state/error, and refresh command enablement. UI state never becomes streaming merely because a button was pressed.

## 6. UI structure

### Android

- Dark Material 3 surface with one status header and three compact sections: `1 连接电脑`, `2 麦克风路由`, `3 采集控制`.
- Discovery rows fill host/port. Manual host/port and pairing code remain visible as fallback.
- Route rows are explicitly labeled `自动选择（不可手动切换）`; show the selected Ray-Ban device or the exact reason Start is disabled.
- One prominent primary action changes between `连接`, `开始采集`, and `停止并释放`; secondary actions are visually subordinate.
- Show connection/auth/retry errors inline near the failed step, not only in a bottom card.

### Windows

- Dark ShadUI surface with a dominant status header, a connection card containing pairing code and peer state, an audio-output card, and one primary Start/Stop action.
- Show listener/discovery ports as secondary diagnostics. Keep them available without competing with the main action.
- Start is disabled unless the authoritative snapshot is connected and idle. Stop remains available during starting/streaming/stopping where cleanup is meaningful.

## 7. Build and maintenance

- Add release signing placeholders/configuration that read from environment or local untracked properties; never commit secrets.
- Remove deprecated Android Gradle flags after the current AGP/Kotlin setup is migrated.
- Remove duplicate README instructions and document pairing, firewall scope, discovery fallback, and the real-device smoke flow.

## 8. Compatibility and rollback

- Keep protocol version `1` for the existing framed transport; add optional fields and a clear authentication capability/error so old clients fail closed rather than silently streaming unauthenticated.
- Roll back UI independently from protocol only before authentication is enabled in the Windows/Android pair. During implementation, keep each protocol change covered by cross-language frame tests.
- No data migration is required because the application does not persist audio or pairing secrets.
