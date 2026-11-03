# Error Handling

## Scope / Trigger

This contract applies to the .NET 10 Windows `CoreController` and its TCP boundary with the Android service. The core must fail closed: it must not report `streaming` unless the VB-Cable sink is open and a compatible Android control connection is available.

## Signatures

- `CoreController.StartAsync()` starts TCP `17891`, optional UDP audio `17892`, UDP discovery `17893`, mDNS advertisement/browser, hotkey handling, and the bounded audio consumer.
- `CoreController.ConnectAsync()` accepts a discovered or manually supplied peer endpoint; it is equivalent to the TCP listener path after HELLO.
- `CoreController.DispatchAsync("START" | "STOP" | "STATUS" | "PING")` returns a typed `ResponsePayload`.
- `IAudioSink.Open()`, `Write(ReadOnlySpan<byte>)`, and `Close()` own the Windows audio resource; `NAudioSink` selects the VB-Cable endpoint.
- `HELLO` requires installation `device_id`, per-attempt `connection_id`, and `auth_mode: device_trust_v1`; `pairing_requested` is only set by an explicit foreground Android connect action.

## Contracts

- `FrameCodec.Decode` rejects an invalid magic, protocol version, payload length over 1 MiB, or CRC-32 mismatch.
- Non-audio payloads over 64 KiB are rejected.
- mDNS and UDP discovery advertise only protocol version, stable instance ID, display name, capabilities, and transports. They are never authentication.
- Windows mDNS and UDP discovery use IPv4-only, enabled physical/local interfaces and exclude loopback, tunnel, VPN, Tailscale, Clash, VMware, and virtual adapters. Discovery failure is diagnostic only; TCP and manual IP remain usable.
- Both endpoints may initiate TCP. Simultaneous attempts use the same lexicographic `min(device_id):min(connection_id)` key. The losing socket follows normal disconnect cleanup.
- Legacy pairing-code clients are rejected with an upgrade diagnostic; device-trust authentication never falls back to legacy authentication.
- A reader loop must check its connection generation/current client before HELLO authentication, command dispatch, status/error publication, and any writer access; stale readers may only close their own socket.
- Incoming Android server handlers must bind state, status/error publication, audio callbacks, and cleanup to the exact active client socket; a replaced handler must never use the global writer or stop the replacement capture.
- `BluetoothRouteManager.selectInput()` resolves the persisted explicit device by type/address/name, or follows the system when no preference exists or the device is absent. Missing devices retain their saved preference; Android IDs are not stable across reconnects. Follow System first matches `AudioManager.communicationDevice` after clearing this app's override in communication mode; available communication devices have no documented priority order. The documented headset-first compatibility fallback applies only when the current route cannot be matched. No usable input raises `input_device_not_found`.
- `AutoCapturePolicy.CaptureFailed()` releases capture ownership without clearing the attempted-recording latch. An authenticated connection in `Error` allows manual START or one automatic START after the next debounced recording rising edge; sustained Active never retries itself.
- First pairing requires Windows approval within 60 seconds. Pending metadata is recoverable from the IPC status snapshot; stdin EOF, disconnect, denial, or timeout rejects it.
- Scheme A sends a random 32-byte key in plaintext on the approved TCP connection, relying on a trusted home LAN. Android commits Keystore-encrypted storage before ACK; Windows commits DPAPI CurrentUser storage before normal authentication. Persistence failure must not report successful pairing.
- Subsequent authentication requires both phone proof and a domain-separated Windows proof. Only the authenticated winner may replace an active connection. One trusted peer is retained; replacing it requires explicit forgetting.
- Forgetting stops capture and disconnects before removing the peer key while preserving installation identity. A stale peer key produces a repair instruction and never silently grants a fresh pairing.

### Android UI command dispatch

- `MicrophoneService.onStartCommand(ACTION_START_CAPTURE | ACTION_STOP_CAPTURE)` may receive a command while `ControlClient` is connecting. `ControlClient.sendCommand` MUST publish an `ERROR` snapshot with a Chinese detail when authentication is incomplete or frame sending fails; returning `false` alone is not observable by Flutter.
- When authentication then succeeds, the connection callback MAY transition that transient command error back to `IDLE` while preserving the detail so the user can retry without a second state system.
- On Android, the first meaningful capture, send, or control-socket failure detail MUST survive idempotent `stop()` and the final `DISCONNECTED` snapshot; cleanup must not replace it with a blank idle state.
- On Windows, `StopCaptureAsync()` MUST save an existing `Error` detail before publishing `Stopping`, and successful reconnection MUST clear stale detail.

## Validation & Error Matrix

| Condition | Result |
| --- | --- |
| No compatible Android client on `START` | `disconnected` response with connection error |
| VB-Cable cannot open | `error` response; no audio is accepted as streaming |
| Android route unavailable | Android sends an error; .NET closes the sink and reports `error` |
| Repeated `START` | Current streaming state; no second recorder or sink |
| Repeated `STOP` | Current idle state; no fatal error |
| Invalid HELLO or incompatible audio parameters | Connection is rejected and closed; current session is unchanged |
| Wrong, expired, malformed, or replayed authentication | Candidate is closed; active session is unchanged |
| Android has not confirmed `streaming` within 5 seconds | Sink closes and Windows reports `error` |
| Audio queue is full | Incoming frame is dropped and `DroppedAudioFrames` increases; network reader continues |
| mDNS or UDP discovery failure | Diagnostic log and empty/partial peer list; listener and manual TCP remain usable |
| TCP disconnect or arbitration loss | Stop/close path runs before the state becomes disconnected |
| Capture/send failure followed by disconnect | Android and Windows clean resources, end disconnected, and retain the first diagnostic detail |

## Tests Required

- State-machine tests assert idempotent `START`/`STOP` and exactly one sink open/close.
- Protocol tests assert device-trust HELLO fields, mutual proof binding, and checksum rejection.
- Discovery tests assert capability TXT parsing, interface filtering, deduplication, and mDNS failure isolation.
- Device tests assert default Follow System, explicit preference persistence, temporary missing-device fallback without erasing the preference, and restoration when that device returns. Capture-route loss after startup must release resources and report an error.
- Recovery tests assert that startup/runtime capture failures retain authentication, do not repeat START during sustained Active, and retry on the next debounced rising edge; Windows timeout/sink failure must send a bounded STOP.
- Authentication tests assert matching proof success, wrong secret rejection, challenge expiry, malformed proof, and unauthenticated command rejection.
- Trust tests exercise approval, denial, timeout, persistence failure, reconnection with the saved key, second-device rejection, and forgetting. Test storage must be isolated from user trust records.
- Queue tests fill the bounded buffer and assert the dropped-frame counter without blocking the reader.

## Microphone Selection Cases

- Good: a saved headset is available; START resolves its current Android ID and captures it.
- Base: no preference exists, or a saved device is disconnected; START follows the current system communication route, falling back to an available input when necessary.
- Bad: no usable input exists; report `input_device_not_found` and release capture resources.
- Wrong: erase an unavailable saved device, require an explicit selection on every launch, or clear the automatic-attempt latch after failure.
- Correct: retain the preference, publish temporary system fallback, and rearm automatic capture only after a new recording interval.


## Scenario: LAN Discovery and Optional UDP Audio

- mDNS service: `_micbridge._tcp.local` on TCP `17891`.
- UDP discovery: `224.0.0.167:17893`, directed/broadcast IPv4 announcements, validated JSON payload, and unicast response; no pairing or audio data.
- Each discovery destination is attempted independently; a failed multicast send must not suppress directed/broadcast delivery.
- Optional UDP audio: `MICBRIDGE_AUDIO_TRANSPORT=udp`; UDP port `17892`.
- `HELLO.audio_transports`: string array; Android advertises `["tcp", "udp"]`.
- `STATUS.audio_transport`: selected `tcp` or `udp`; `STATUS.audio_port` is present only for UDP.
- TCP remains the control channel and default audio transport. UDP is selected only when requested and advertised by both ends.
- mDNS/UDP discovery failure yields an empty/partial peer list while manual TCP entry remains usable.
- Windows discovery keys peers by `role:id`, replaces endpoint changes, and expires peers after 15 seconds; optional `Action<DiscoveredPeer>` removal callbacks notify the parent layer.
- Android `LanDiscovery.onChanged` publishes the current deduplicated list after endpoint replacement or 15-second expiry; mDNS and UDP failures do not clear peers from the other mechanism.

### Good / Base / Bad Cases

- Good: a valid protocol-1 UDP announcement produces a deduplicated peer candidate and a unicast response.
- Base: mDNS and UDP discovery are unavailable; TCP listener and manual IP connection still work.
- Bad: malformed JSON, invalid role/ID/port, or incompatible HELLO is ignored or rejected without changing the current session.

### Wrong vs Correct

#### Wrong

Treating a discovery announcement as authenticated or letting a discovery failure stop the TCP listener.

#### Correct

Validate the bounded discovery payload, use it only as a TCP candidate, require HELLO, and keep manual TCP fallback available.
