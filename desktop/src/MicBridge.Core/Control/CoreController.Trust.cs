using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using MicBridge.Core.Protocol;

namespace MicBridge.Core.Control;

public sealed partial class CoreController
{
    private readonly IDeviceTrustStore trustStore;
    private readonly TimeProvider clock;
    private DeviceTrustState trustState;
    private bool trustStoreFailed;
    private bool forgetting;
    private bool approvalAvailable = true;
    private long trustEpoch;
    private PendingPairing? pendingPairing;
    private TaskCompletionSource<bool>? pairingDecision;
    private long pendingStartedAt;
    private TimeSpan PairingLifetime => options.PairingTimeout ?? TimeSpan.FromSeconds(60);

    public bool DecidePairing(string requestId, string decision)
    {
        if (decision is not ("allow" or "deny")) return false;
        lock (pairingGate)
        {
            if (!approvalAvailable || pendingPairing is null || pendingPairing.RequestId != requestId ||
                clock.GetElapsedTime(pendingStartedAt) >= PairingLifetime) return false;
            if (pairingDecision?.Task.IsCompletedSuccessfully == true) return pairingDecision.Task.Result == (decision == "allow");
            return pairingDecision?.TrySetResult(decision == "allow") == true;
        }
    }

    public void DisablePairingApproval()
    {
        lock (pairingGate)
        {
            approvalAvailable = false;
            pairingDecision?.TrySetResult(false);
            pendingPairing = null;
        }
        Publish(Snapshot);
    }

    public async Task ForgetDeviceAsync(CancellationToken cancellationToken = default)
    {
        lock (pairingGate)
        {
            if (forgetting) throw new InvalidOperationException("Device removal is already in progress");
            forgetting = true;
            trustEpoch++;
            pairingDecision?.TrySetResult(false);
            pendingPairing = null;
            lock (connectionGate) captureStartConfirmation?.TrySetResult(false);
        }
        try
        {
            // Stop while the authenticated socket still exists, then revoke it even if STOP fails.
            try { await StopCaptureAsync(cancellationToken, notifyAndroid: true); }
            finally
            {
                CloseAndroid();
                CloseSink();
                await SetConnectedAsync(false, CancellationToken.None);
            }
        }
        finally
        {
            try
            {
                lock (pairingGate)
                {
                    trustState = trustState with { Device = null };
                    try { trustStore.Save(trustState); trustStoreFailed = false; }
                    catch { trustStoreFailed = true; throw; }
                    finally { forgetting = false; }
                }
            }
            finally { Publish(Snapshot); }
        }
    }

    private async Task<long> AuthenticateTrustAsync(TcpClient client, NetworkStream stream, HelloPayload hello,
        string connectionId, CancellationTokenSource handshakeTimeout, CancellationToken cancellationToken, bool outgoing)
    {
        if (hello.AuthMode != "device_trust_v1") throw new AuthenticationException("auth_mode_unsupported", "device trust authentication is required");
        if (hello.Peer != "android") throw new AuthenticationException("hello_rejected", "only Android devices can be trusted");
        if (hello.DeviceName is { } name && (name.Length > 256 || name.Any(char.IsControl)))
            throw new AuthenticationException("hello_rejected", "device name must be control-free and at most 256 UTF-16 units");
        ValidateAuthField(hello.DeviceId ?? "", "device_id");
        ValidateAuthField(hello.ConnectionId ?? "", "connection_id");
        if (!string.IsNullOrEmpty(hello.PairingId)) ValidateAuthField(hello.PairingId, "pairing_id");
        DeviceTrustRecord? device;
        long epoch;
        lock (pairingGate)
        {
            if (trustStoreFailed) throw new AuthenticationException("trust_store_failed", "device trust storage is unavailable");
            if (forgetting) throw new AuthenticationException("unknown_device", "device trust is being removed");
            epoch = trustEpoch;
            device = trustState.Device;
            if (device is not null && device.DeviceId != hello.DeviceId)
                throw new AuthenticationException("trust_slot_occupied", "forget the remembered phone before pairing another device");
            if (device is not null && device.PairingId != hello.PairingId)
                throw new AuthenticationException("unknown_device", "device trust does not match; forget the device on both ends");
            if (device is null && (outgoing || !string.IsNullOrEmpty(hello.PairingId) || !hello.PairingRequested))
                throw new AuthenticationException("unknown_device", "explicit phone pairing is required; clear stale trust on the phone");
        }
        if (device is null)
        {
            var duration = PairingLifetime;
            var startedAt = clock.GetTimestamp();
            var request = new PendingPairing(Guid.NewGuid().ToString("N"), hello.DeviceId!,
                string.IsNullOrWhiteSpace(hello.DeviceName) ? hello.DeviceId! : hello.DeviceName, clock.GetUtcNow() + duration);
            var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (pairingGate)
            {
                if (!approvalAvailable) throw new AuthenticationException("pairing_denied", "approval interface is unavailable");
                if (pendingPairing is not null || pairingDecision is not null) throw new AuthenticationException("pairing_busy", "another pairing request is pending");
                if (epoch != trustEpoch || forgetting || trustState.Device is not null) throw new AuthenticationException("unknown_device", "device trust changed");
                pendingPairing = request;
                pairingDecision = decision;
                pendingStartedAt = startedAt;
            }
            handshakeTimeout.CancelAfter(duration + TimeSpan.FromSeconds(10));
            try
            {
                Publish(Snapshot);
                PairingRequested?.Invoke(this, request);
                while (!decision.Task.IsCompleted)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (clock.GetElapsedTime(startedAt) >= duration) throw new AuthenticationException("pairing_expired", "pairing approval expired");
                    // No frames are permitted before approval. Read-ready includes EOF, so a dead candidate cannot retain the slot.
                    if (client.Client.Poll(0, SelectMode.SelectRead)) throw new AuthenticationException("pairing_denied", "phone disconnected or sent data before approval");
                    await Task.WhenAny(decision.Task, Task.Delay(TimeSpan.FromMilliseconds(50), clock, cancellationToken));
                }
                if (!await decision.Task) throw new AuthenticationException("pairing_denied", "pairing was denied");
                if (clock.GetElapsedTime(startedAt) >= duration) throw new AuthenticationException("pairing_expired", "pairing approval expired");
                if (client.Client.Poll(0, SelectMode.SelectRead)) throw new AuthenticationException("pairing_denied", "phone disconnected or sent data before approval");
                lock (pairingGate)
                {
                    if (epoch != trustEpoch || forgetting || !approvalAvailable) throw new AuthenticationException("pairing_denied", "pairing was revoked");
                    pendingPairing = null;
                }
                Publish(Snapshot);
                handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                device = new DeviceTrustRecord(hello.DeviceId!, request.DeviceName, Guid.NewGuid().ToString("N"),
                    Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('='));
                // Explicitly approved enrollment sends the secret in cleartext on the trusted home LAN.
                // This design does not protect first pairing against a LAN observer or active MITM.
                await WriteAsync(stream, FrameCodec.Json(MessageType.PairGrant, 1, new PairGrantPayload(device.PairingId, device.Secret)), cancellationToken);
                var ack = await FrameCodec.ReadAsync(stream, cancellationToken);
                if (ack.Type != MessageType.PairAck || FrameCodec.ReadJson<PairAckPayload>(ack).PairingId != device.PairingId)
                    throw new AuthenticationException("pairing_ack_invalid", "phone must acknowledge the granted pairing");
                lock (pairingGate)
                {
                    if (epoch != trustEpoch || forgetting || !approvalAvailable) throw new AuthenticationException("pairing_denied", "pairing was revoked");
                    var next = trustState with { Device = device };
                    try { trustStore.Save(next); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
                    {
                        trustStoreFailed = true;
                        throw new AuthenticationException("trust_store_failed", "device trust could not be saved");
                    }
                    trustState = next;
                }
                Publish(Snapshot);
            }
            finally
            {
                lock (pairingGate)
                {
                    if (ReferenceEquals(pairingDecision, decision)) { pendingPairing = null; pairingDecision = null; }
                }
                Publish(Snapshot);
            }
        }
        handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(10));
        var challenge = FrameCodec.CreateAuthChallenge(deviceId, connectionId, clock.GetUtcNow().ToUnixTimeMilliseconds());
        var challengeStartedAt = clock.GetTimestamp();
        await WriteAsync(stream, FrameCodec.Json(MessageType.AuthChallenge, 1, challenge), cancellationToken);
        var proofFrame = await FrameCodec.ReadAsync(stream, cancellationToken);
        if (proofFrame.Type != MessageType.AuthProof) throw new AuthenticationException("auth_required", "authentication proof is required before commands");
        AuthProofPayload proof;
        try { proof = FrameCodec.ReadJson<AuthProofPayload>(proofFrame); }
        catch (Exception error) when (error is JsonException or InvalidDataException)
        { throw new AuthenticationException("auth_malformed", "malformed authentication proof"); }
        if (proof.Proof is not { Length: 43 } || proof.Nonce is not { Length: 32 } ||
            !proof.Nonce.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new AuthenticationException("auth_malformed", "malformed proof or nonce");
        if (clock.GetElapsedTime(challengeStartedAt) >= TimeSpan.FromMilliseconds(FrameCodec.AuthChallengeLifetimeMilliseconds))
            throw new AuthenticationException("auth_expired", "authentication challenge expired");
        var expected = FrameCodec.ComputeAuthProof(device.Secret, challenge.Challenge, deviceId, hello.DeviceId!, connectionId, hello.ConnectionId!);
        if (!FrameCodec.FixedTimeEquals(expected, proof.Proof)) throw new AuthenticationException("auth_proof_invalid", "device authentication failed");
        var nameChanged = false;
        lock (pairingGate)
        {
            if (epoch != trustEpoch || forgetting || trustStoreFailed || trustState.Device?.PairingId != device.PairingId)
                throw new AuthenticationException("unknown_device", "device trust was revoked");
            if (!string.IsNullOrWhiteSpace(hello.DeviceName) && trustState.Device!.DeviceName != hello.DeviceName)
            {
                var next = trustState with { Device = trustState.Device with { DeviceName = hello.DeviceName } };
                try { trustStore.Save(next); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException)
                {
                    trustStoreFailed = true;
                    throw new AuthenticationException("trust_store_failed", "device name could not be saved");
                }
                trustState = next;
                nameChanged = true;
            }
        }
        if (nameChanged) Publish(Snapshot);
        var serverProof = FrameCodec.ComputeServerAuthProof(device.Secret, deviceId, hello.DeviceId!, connectionId,
            hello.ConnectionId!, device.PairingId, challenge.Challenge, proof.Nonce, proof.Proof);
        await WriteAsync(stream, FrameCodec.Json(MessageType.AuthOk, proofFrame.Sequence, new AuthOkPayload(serverProof)), cancellationToken);
        return epoch;
    }
}
