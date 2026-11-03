using System.Net.Sockets;
using System.Security.Cryptography;
using MicBridge.Core.Control;
using MicBridge.Core.Protocol;
using static MicBridge.Core.Tests.TrustTestSupport;

namespace MicBridge.Core.Tests;

public sealed class DeviceTrustTests
{
    [Fact]
    public async Task ExplicitApprovalWaitsForAckBeforeSavingAndAuthenticatesBothSides()
    {
        await using var fixture = await Fixture.StartAsync();
        using var phone = await ConnectAsync(fixture.Port);
        var stream = phone.GetStream();
        var windows = await SendHelloAsync(stream, Hello(true, null));
        var pending = await fixture.Request.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(pending, fixture.Core.PendingPairing);
        Assert.InRange((pending.ExpiresAt - DateTimeOffset.UtcNow).TotalSeconds, 58, 60);
        Assert.Null(fixture.Store.State.Device);
        Assert.False(fixture.Core.Snapshot.AndroidConnected);
        Assert.False(fixture.Core.DecidePairing("stale-request", "allow"));
        Assert.False(fixture.Core.DecidePairing(pending.RequestId, "invalid"));
        Assert.True(fixture.Core.DecidePairing(pending.RequestId, "allow"));
        var grantFrame = await ReadAsync(stream);
        Assert.Equal(MessageType.PairGrant, grantFrame.Type);
        var grant = FrameCodec.ReadJson<PairGrantPayload>(grantFrame);
        Assert.Equal(32, Convert.FromBase64String(grant.Secret.Replace('-', '+').Replace('_', '/') + "=").Length);
        Assert.Null(fixture.Store.State.Device);
        Assert.False(fixture.Core.Snapshot.AndroidConnected);
        await WriteAsync(stream, FrameCodec.Json(MessageType.PairAck, 2, new PairAckPayload(grant.PairingId)));
        await AuthenticateAsync(stream, windows, grant.Secret, grant.PairingId);
        Assert.Equal(grant.PairingId, fixture.Store.State.Device?.PairingId);
        Assert.True(fixture.Core.Snapshot.AndroidConnected);
        Assert.Null(fixture.Core.PendingPairing);
    }

    [Theory]
    [InlineData(false, null, "device_trust_v1", "unknown_device")]
    [InlineData(true, PairingId, "device_trust_v1", "unknown_device")]
    [InlineData(true, null, null, "auth_mode_unsupported")]
    public async Task DiscoveryOrStaleTrustNeverCreatesApproval(bool requested, string? pairingId, string? mode, string code)
    {
        await using var fixture = await Fixture.StartAsync();
        using var phone = await ConnectAsync(fixture.Port);
        await SendHelloAsync(phone.GetStream(), Hello(requested, pairingId) with { AuthMode = mode });
        Assert.Equal(code, await ErrorAsync(phone.GetStream()));
        Assert.Null(fixture.Core.PendingPairing);
        Assert.False(fixture.Request.Task.IsCompleted);
        Assert.Null(fixture.Store.State.Device);
    }

    [Fact]
    public async Task DenialClearsPendingAndRejectsLateApproval()
    {
        await using var fixture = await Fixture.StartAsync();
        using var phone = await ConnectAsync(fixture.Port);
        await SendHelloAsync(phone.GetStream(), Hello(true, null));
        var pending = await fixture.Request.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(fixture.Core.DecidePairing(pending.RequestId, "deny"));
        Assert.Equal("pairing_denied", await ErrorAsync(phone.GetStream()));
        Assert.Null(fixture.Core.PendingPairing);
        Assert.False(fixture.Core.DecidePairing(pending.RequestId, "allow"));
        Assert.Null(fixture.Store.State.Device);
    }

    [Fact]
    public async Task ApprovalDeadlineExpiresWithoutPersisting()
    {
        await using var fixture = await Fixture.StartAsync(timeout: TimeSpan.FromMilliseconds(100));
        using var phone = await ConnectAsync(fixture.Port);
        await SendHelloAsync(phone.GetStream(), Hello(true, null));
        var pending = await fixture.Request.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("pairing_expired", await ErrorAsync(phone.GetStream()));
        Assert.False(fixture.Core.DecidePairing(pending.RequestId, "allow"));
        Assert.Null(fixture.Core.PendingPairing);
        Assert.Null(fixture.Store.State.Device);
    }

    [Fact]
    public async Task DisconnectedCandidateReleasesPendingSlot()
    {
        await using var fixture = await Fixture.StartAsync();
        using var phone = await ConnectAsync(fixture.Port);
        await SendHelloAsync(phone.GetStream(), Hello(true, null));
        var pending = await fixture.Request.Task.WaitAsync(TimeSpan.FromSeconds(2));
        phone.Close();
        await UntilAsync(() => fixture.Core.PendingPairing is null);
        Assert.False(fixture.Core.DecidePairing(pending.RequestId, "allow"));
        Assert.Null(fixture.Store.State.Device);
    }

    [Fact]
    public async Task ApprovalInterfaceEofDeniesPendingAndFutureRequests()
    {
        await using var fixture = await Fixture.StartAsync();
        using var phone = await ConnectAsync(fixture.Port);
        await SendHelloAsync(phone.GetStream(), Hello(true, null));
        var pending = await fixture.Request.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Core.DisablePairingApproval();
        Assert.Equal("pairing_denied", await ErrorAsync(phone.GetStream()));
        Assert.False(fixture.Core.DecidePairing(pending.RequestId, "allow"));
        using var retry = await ConnectAsync(fixture.Port);
        await SendHelloAsync(retry.GetStream(), Hello(true, null));
        Assert.Equal("pairing_denied", await ErrorAsync(retry.GetStream()));
    }

    [Fact]
    public async Task SecondPendingCandidateIsRejectedWithoutReplacingFirst()
    {
        await using var fixture = await Fixture.StartAsync();
        using var first = await ConnectAsync(fixture.Port);
        await SendHelloAsync(first.GetStream(), Hello(true, null));
        var pending = await fixture.Request.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var second = await ConnectAsync(fixture.Port);
        await SendHelloAsync(second.GetStream(), Hello(true, null, OtherDeviceId));
        Assert.Equal("pairing_busy", await ErrorAsync(second.GetStream()));
        Assert.Equal(pending, fixture.Core.PendingPairing);
        fixture.Core.DecidePairing(pending.RequestId, "deny");
        Assert.Equal("pairing_denied", await ErrorAsync(first.GetStream()));
    }

    [Fact]
    public async Task WrongAckDoesNotSaveTrust()
    {
        await using var fixture = await Fixture.StartAsync();
        using var phone = await ConnectAsync(fixture.Port);
        await SendHelloAsync(phone.GetStream(), Hello(true, null));
        var pending = await fixture.Request.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Core.DecidePairing(pending.RequestId, "allow");
        Assert.Equal(MessageType.PairGrant, (await ReadAsync(phone.GetStream())).Type);
        await WriteAsync(phone.GetStream(), FrameCodec.Json(MessageType.PairAck, 2, new PairAckPayload("wrong")));
        Assert.Equal("pairing_ack_invalid", await ErrorAsync(phone.GetStream()));
        Assert.Null(fixture.Store.State.Device);
    }

    [Fact]
    public async Task SaveFailureNeverSendsChallengeOrMarksDeviceTrusted()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Store.FailSave = true;
        using var phone = await ConnectAsync(fixture.Port);
        await SendHelloAsync(phone.GetStream(), Hello(true, null));
        var pending = await fixture.Request.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Core.DecidePairing(pending.RequestId, "allow");
        var grant = FrameCodec.ReadJson<PairGrantPayload>(await ReadAsync(phone.GetStream()));
        await WriteAsync(phone.GetStream(), FrameCodec.Json(MessageType.PairAck, 2, new PairAckPayload(grant.PairingId)));
        Assert.Equal("trust_store_failed", await ErrorAsync(phone.GetStream()));
        Assert.Null(fixture.Core.TrustedDevice);
        Assert.False(fixture.Core.Snapshot.AndroidConnected);
        Assert.Equal("repair_required", fixture.Core.TrustState);
    }

    [Fact]
    public async Task LoadFailureNeverOffersFreshPairing()
    {
        await using var fixture = await Fixture.StartAsync(new MemoryTrustStore { FailLoad = true });
        using var phone = await ConnectAsync(fixture.Port);
        await SendHelloAsync(phone.GetStream(), Hello(true, null));
        Assert.Equal("trust_store_failed", await ErrorAsync(phone.GetStream()));
        Assert.False(fixture.Request.Task.IsCompleted);
    }

    [Fact]
    public async Task RememberedPhoneReconnectsAfterRestartWithoutApproval()
    {
        var store = MemoryTrustStore.Trusted();
        await using (var first = await Fixture.StartAsync(store))
        {
            using var phone = await ConnectAsync(first.Port);
            var windows = await SendHelloAsync(phone.GetStream());
            await AuthenticateAsync(phone.GetStream(), windows);
            Assert.False(first.Request.Task.IsCompleted);
        }
        await using var restarted = await Fixture.StartAsync(store);
        using var reconnect = await ConnectAsync(restarted.Port);
        var hello = await SendHelloAsync(reconnect.GetStream());
        await AuthenticateAsync(reconnect.GetStream(), hello);
        Assert.False(restarted.Request.Task.IsCompleted);
        Assert.True(restarted.Core.Snapshot.AndroidConnected);
    }

    [Fact]
    public async Task SecondDeviceCannotDisruptTrustedConnection()
    {
        await using var fixture = await Fixture.StartAsync(MemoryTrustStore.Trusted());
        using var phone = await ConnectAsync(fixture.Port);
        var windows = await SendHelloAsync(phone.GetStream());
        await AuthenticateAsync(phone.GetStream(), windows);
        using var other = await ConnectAsync(fixture.Port);
        await SendHelloAsync(other.GetStream(), Hello(true, null, OtherDeviceId));
        Assert.Equal("trust_slot_occupied", await ErrorAsync(other.GetStream()));
        await WriteAsync(phone.GetStream(), FrameCodec.Json(MessageType.Ping, 8, new CommandPayload("PING")));
        Assert.Equal(MessageType.Status, (await ReadAsync(phone.GetStream())).Type);
        Assert.True(fixture.Core.Snapshot.AndroidConnected);
        Assert.False(fixture.Request.Task.IsCompleted);
    }

    [Fact]
    public async Task ForgetStopsCaptureDisconnectsAndRetainsInstallationId()
    {
        await using var fixture = await Fixture.StartAsync(MemoryTrustStore.Trusted());
        var installation = fixture.Store.State.InstallationId;
        using var phone = await ConnectAsync(fixture.Port);
        var stream = phone.GetStream();
        var windows = await SendHelloAsync(stream);
        await AuthenticateAsync(stream, windows);
        var start = fixture.Core.DispatchAsync("START");
        Assert.Equal(MessageType.Start, (await ReadAsync(stream)).Type);
        await WriteAsync(stream, FrameCodec.Json(MessageType.Status, 4, new StatusPayload("streaming")));
        Assert.True((await start.WaitAsync(TimeSpan.FromSeconds(2))).Ok);
        Assert.True(fixture.Sink.IsOpen);
        await fixture.Core.ForgetDeviceAsync();
        Assert.Equal(MessageType.Stop, (await ReadAsync(stream)).Type);
        await Assert.ThrowsAsync<EndOfStreamException>(async () => { await ReadAsync(stream); });
        Assert.False(fixture.Sink.IsOpen);
        Assert.False(fixture.Core.Snapshot.AndroidConnected);
        Assert.Null(fixture.Core.TrustedDevice);
        Assert.Null(fixture.Store.State.Device);
        Assert.Equal(installation, fixture.Store.State.InstallationId);
    }

    [Fact]
    public async Task ForgetBetweenGrantAndAckCannotRestoreTrust()
    {
        await using var fixture = await Fixture.StartAsync();
        using var phone = await ConnectAsync(fixture.Port);
        await SendHelloAsync(phone.GetStream(), Hello(true, null));
        var pending = await fixture.Request.Task.WaitAsync(TimeSpan.FromSeconds(2));
        fixture.Core.DecidePairing(pending.RequestId, "allow");
        var grant = FrameCodec.ReadJson<PairGrantPayload>(await ReadAsync(phone.GetStream()));
        await fixture.Core.ForgetDeviceAsync();
        await WriteAsync(phone.GetStream(), FrameCodec.Json(MessageType.PairAck, 2, new PairAckPayload(grant.PairingId)));
        Assert.Equal("pairing_denied", await ErrorAsync(phone.GetStream()));
        Assert.Null(fixture.Store.State.Device);
        Assert.Null(fixture.Core.TrustedDevice);
    }

    [Fact]
    public async Task FailedForgetRevokesInMemoryTrustAndReportsStorageFailure()
    {
        await using var fixture = await Fixture.StartAsync(MemoryTrustStore.Trusted());
        fixture.Store.FailSave = true;
        await Assert.ThrowsAsync<IOException>(() => fixture.Core.ForgetDeviceAsync());
        Assert.Null(fixture.Core.TrustedDevice);
        Assert.Equal("repair_required", fixture.Core.TrustState);
        using var phone = await ConnectAsync(fixture.Port);
        await SendHelloAsync(phone.GetStream());
        Assert.Equal("trust_store_failed", await ErrorAsync(phone.GetStream()));
    }

    [Theory]
    [InlineData("bad-proof", Nonce, "auth_malformed")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", Nonce, "auth_proof_invalid")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "bad-nonce", "auth_malformed")]
    public async Task InvalidProofOrNonceNeverConnects(string proof, string nonce, string code)
    {
        await using var fixture = await Fixture.StartAsync(MemoryTrustStore.Trusted());
        using var phone = await ConnectAsync(fixture.Port);
        await SendHelloAsync(phone.GetStream());
        Assert.Equal(MessageType.AuthChallenge, (await ReadAsync(phone.GetStream())).Type);
        await WriteAsync(phone.GetStream(), FrameCodec.Json(MessageType.AuthProof, 2, new AuthProofPayload(proof, nonce)));
        Assert.Equal(code, await ErrorAsync(phone.GetStream()));
        Assert.False(fixture.Core.Snapshot.AndroidConnected);
    }

    [Fact]
    public async Task ProofCannotBeReplayedOnAnotherConnection()
    {
        await using var fixture = await Fixture.StartAsync(MemoryTrustStore.Trusted());
        using var first = await ConnectAsync(fixture.Port);
        var firstHello = await SendHelloAsync(first.GetStream());
        var challenge = FrameCodec.ReadJson<AuthChallengePayload>(await ReadAsync(first.GetStream()));
        var proof = FrameCodec.ComputeAuthProof(Secret, challenge.Challenge, firstHello.DeviceId!, AndroidId, firstHello.ConnectionId!, ConnectionId);
        first.Close();
        using var second = await ConnectAsync(fixture.Port);
        await SendHelloAsync(second.GetStream());
        Assert.Equal(MessageType.AuthChallenge, (await ReadAsync(second.GetStream())).Type);
        await WriteAsync(second.GetStream(), FrameCodec.Json(MessageType.AuthProof, 2, new AuthProofPayload(proof, Nonce)));
        Assert.Equal("auth_proof_invalid", await ErrorAsync(second.GetStream()));
    }

    [Fact]
    public void ServerProofMatchesIndependentSharedAndroidVector()
    {
        Assert.Equal("YDV738wX-8B_PNSxQN1zZFZajFfbMcOTvGlix6RpNrQ", FrameCodec.ComputeServerAuthProof(
            Secret, new string('0', 32), new string('1', 32), new string('2', 32), new string('3', 32),
            new string('4', 32), "challenge", "nonce", "phone-proof"));
    }

    [Fact]
    public void DpapiRoundTripEncryptsSecretAndForgetPreservesIdentity()
    {
        var folder = Path.Combine(Path.GetTempPath(), "micbridge-trust-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "trust.dat");
        try
        {
            var store = new DpapiDeviceTrustStore(path);
            var state = store.Load();
            var trusted = state with { Device = new DeviceTrustRecord(AndroidId, "Phone", PairingId, Secret) };
            store.Save(trusted);
            Assert.Equal(trusted, new DpapiDeviceTrustStore(path).Load());
            Assert.DoesNotContain(Secret, System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)));
            store.Save(state);
            Assert.Equal(state, new DpapiDeviceTrustStore(path).Load());
            File.WriteAllText(path, "corrupted ciphertext");
            Assert.Throws<CryptographicException>(() => store.Load());
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }

    [Theory]
    [InlineData("", ConnectionId)]
    [InlineData("11111111-1111-1111-1111-111111111111", ConnectionId)]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", ConnectionId)]
    [InlineData(AndroidId, "")]
    public async Task MalformedIdentityCannotCreateApproval(string device, string connection)
    {
        await using var fixture = await Fixture.StartAsync();
        using var phone = await ConnectAsync(fixture.Port);
        await SendHelloAsync(phone.GetStream(), Hello(true, null, device, connection));
        Assert.Equal("auth_malformed", await ErrorAsync(phone.GetStream()));
        Assert.Null(fixture.Core.PendingPairing);
    }

    [Fact]
    public async Task OutgoingConnectionCannotEnrollAnUnknownPhone()
    {
        await using var fixture = await Fixture.StartAsync();
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var accept = listener.AcceptTcpClientAsync();
        await fixture.Core.ConnectAsync("127.0.0.1", ((System.Net.IPEndPoint)listener.LocalEndpoint).Port);
        using var phone = await accept.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(MessageType.Hello, (await ReadAsync(phone.GetStream())).Type);
        await WriteAsync(phone.GetStream(), FrameCodec.Json(MessageType.Hello, 1, Hello(true, null)));
        Assert.Equal("unknown_device", await ErrorAsync(phone.GetStream()));
        Assert.False(fixture.Request.Task.IsCompleted);
    }

    [Fact]
    public async Task ApprovalUsesMonotonicDeadlineDespiteWallClockRollback()
    {
        var clock = new TestClock();
        await using var fixture = await Fixture.StartAsync(clock: clock);
        using var phone = await ConnectAsync(fixture.Port);
        await SendHelloAsync(phone.GetStream(), Hello(true, null));
        var pending = await fixture.Request.Task.WaitAsync(TimeSpan.FromSeconds(2));
        clock.Advance(TimeSpan.FromSeconds(61), TimeSpan.FromDays(-1));
        Assert.False(fixture.Core.DecidePairing(pending.RequestId, "allow"));
        Assert.Equal("pairing_expired", await ErrorAsync(phone.GetStream()));
        Assert.Null(fixture.Store.State.Device);
    }

    [Fact]
    public async Task ExpiredChallengeRejectsOtherwiseCorrectProof()
    {
        var clock = new TestClock();
        await using var fixture = await Fixture.StartAsync(MemoryTrustStore.Trusted(), clock: clock);
        using var phone = await ConnectAsync(fixture.Port);
        var windows = await SendHelloAsync(phone.GetStream());
        var challenge = FrameCodec.ReadJson<AuthChallengePayload>(await ReadAsync(phone.GetStream()));
        var proof = FrameCodec.ComputeAuthProof(Secret, challenge.Challenge, windows.DeviceId!, AndroidId, windows.ConnectionId!, ConnectionId);
        clock.Advance(TimeSpan.FromSeconds(11), TimeSpan.FromDays(-1));
        await WriteAsync(phone.GetStream(), FrameCodec.Json(MessageType.AuthProof, 2, new AuthProofPayload(proof, Nonce)));
        Assert.Equal("auth_expired", await ErrorAsync(phone.GetStream()));
        Assert.False(fixture.Core.Snapshot.AndroidConnected);
    }

    [Fact]
    public async Task UnauthenticatedCommandCannotStartCapture()
    {
        await using var fixture = await Fixture.StartAsync(MemoryTrustStore.Trusted());
        using var phone = await ConnectAsync(fixture.Port);
        await SendHelloAsync(phone.GetStream());
        Assert.Equal(MessageType.AuthChallenge, (await ReadAsync(phone.GetStream())).Type);
        await WriteAsync(phone.GetStream(), FrameCodec.Json(MessageType.Start, 2, new CommandPayload("START")));
        Assert.Equal("auth_required", await ErrorAsync(phone.GetStream()));
        Assert.False(fixture.Sink.IsOpen);
    }

    [Fact]
    public async Task DisconnectCleanupIsSerializedAndCannotStopReplacementCapture()
    {
        await using var fixture = await Fixture.StartAsync(MemoryTrustStore.Trusted());
        using var first = await ConnectAsync(fixture.Port);
        var firstHello = await SendHelloAsync(first.GetStream());
        await AuthenticateAsync(first.GetStream(), firstHello);
        const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var clientField = typeof(CoreController).GetField("android", flags)!;
        var oldClient = Assert.IsType<TcpClient>(clientField.GetValue(fixture.Core));
        var oldStream = Assert.IsType<NetworkStream>(typeof(CoreController).GetField("androidStream", flags)!.GetValue(fixture.Core));
        var gate = Assert.IsType<SemaphoreSlim>(typeof(CoreController).GetField("stateGate", flags)!.GetValue(fixture.Core));
        var cleanupMethod = typeof(CoreController).GetMethod("DisconnectClientAsync", flags)!;
        await gate.WaitAsync();
        Task cleanup;
        try
        {
            cleanup = (Task)cleanupMethod.Invoke(fixture.Core, [oldClient, oldStream])!;
            Assert.False(cleanup.IsCompleted);
            // Owner clearing must wait for the same gate used by admission and sink transitions.
            Assert.Same(oldClient, clientField.GetValue(fixture.Core));
        }
        finally { gate.Release(); }
        await cleanup.WaitAsync(TimeSpan.FromSeconds(2));
        first.Close();

        using var replacement = await ConnectAsync(fixture.Port);
        var stream = replacement.GetStream();
        var hello = await SendHelloAsync(stream);
        await AuthenticateAsync(stream, hello);
        var start = fixture.Core.DispatchAsync("START");
        Assert.Equal(MessageType.Start, (await ReadAsync(stream)).Type);
        await WriteAsync(stream, FrameCodec.Json(MessageType.Status, 3, new StatusPayload("streaming")));
        Assert.True((await start.WaitAsync(TimeSpan.FromSeconds(2))).Ok);

        // Delayed cleanup from the old reader must leave both the new sink and snapshot untouched.
        await ((Task)cleanupMethod.Invoke(fixture.Core, [oldClient, oldStream])!).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(fixture.Sink.IsOpen);
        Assert.True(fixture.Core.Snapshot.AndroidConnected);
        Assert.Equal(CoreState.Streaming, fixture.Core.Snapshot.State);
        await WriteAsync(stream, FrameCodec.Json(MessageType.Ping, 4, new CommandPayload("PING")));
        Assert.Equal("streaming", FrameCodec.ReadJson<StatusPayload>(await ReadAsync(stream)).State);
    }

    [Theory]
    [InlineData(20)]
    [InlineData(256)]
    public async Task AuthenticatedNameChangePersistsAndPublishesTrustedDevice(int length)
    {
        var store = MemoryTrustStore.Trusted();
        var before = store.State;
        var saves = store.Saves;
        var name = new string('新', length);
        await using var fixture = await Fixture.StartAsync(store);
        var published = new TaskCompletionSource<TrustedDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Core.SnapshotChanged += (_, _) =>
        {
            if (fixture.Core.TrustedDevice is { } device && device.DeviceName == name)
                published.TrySetResult(device);
        };
        using var phone = await ConnectAsync(fixture.Port);
        var windows = await SendHelloAsync(phone.GetStream(), Hello() with { DeviceName = name });
        Assert.Equal(before, store.State);
        await AuthenticateAsync(phone.GetStream(), windows);
        Assert.Equal(before with { Device = before.Device! with { DeviceName = name } }, store.Load());
        Assert.Equal(saves + 1, store.Saves);
        Assert.Equal(store.State.Device!.PublicDevice, fixture.Core.TrustedDevice);
        Assert.Equal(fixture.Core.TrustedDevice, await published.Task.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public async Task AuthenticatedArbitrationLoserPublishesPersistedNameWithoutReplacingConnection()
    {
        var store = MemoryTrustStore.Trusted();
        await using var fixture = await Fixture.StartAsync(store);
        using var active = await ConnectAsync(fixture.Port);
        var activeHello = await SendHelloAsync(active.GetStream(), Hello(connection: new string('0', 32)));
        await AuthenticateAsync(active.GetStream(), activeHello, connection: new string('0', 32));
        var before = store.State;
        var saves = store.Saves;
        const string name = "Renamed trusted phone";
        var published = new TaskCompletionSource<TrustedDevice>(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Core.SnapshotChanged += (_, _) =>
        {
            if (fixture.Core.TrustedDevice is { } device && device.DeviceName == name)
                published.TrySetResult(device);
        };

        // 全零连接标识保证现有连接胜过新候选；新候选仍必须完成认证。
        var candidateConnection = new string('f', 32);
        using var candidate = await ConnectAsync(fixture.Port);
        var stream = candidate.GetStream();
        var windows = await SendHelloAsync(stream, Hello(connection: candidateConnection) with { DeviceName = name });
        var challengeFrame = await ReadAsync(stream);
        Assert.Equal(MessageType.AuthChallenge, challengeFrame.Type);
        var challenge = FrameCodec.ReadJson<AuthChallengePayload>(challengeFrame);
        var proof = FrameCodec.ComputeAuthProof(Secret, challenge.Challenge, windows.DeviceId!, AndroidId,
            windows.ConnectionId!, candidateConnection);
        await WriteAsync(stream, FrameCodec.Json(MessageType.AuthProof, 2, new AuthProofPayload(proof, Nonce)));
        Assert.Equal(MessageType.AuthOk, (await ReadAsync(stream)).Type);
        await Assert.ThrowsAsync<EndOfStreamException>(async () => { await ReadAsync(stream); });

        Assert.Equal(before with { Device = before.Device! with { DeviceName = name } }, store.Load());
        Assert.Equal(saves + 1, store.Saves);
        Assert.Equal(store.State.Device!.PublicDevice, await published.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(fixture.Core.Snapshot.AndroidConnected);
        await WriteAsync(active.GetStream(), FrameCodec.Json(MessageType.Ping, 3, new CommandPayload("PING")));
        Assert.Equal(MessageType.Status, (await ReadAsync(active.GetStream())).Type);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrUnauthenticatedNameChangeLeavesTrustUnchanged(bool invalidProof)
    {
        var store = MemoryTrustStore.Trusted();
        var before = store.State;
        var saves = store.Saves;
        await using var fixture = await Fixture.StartAsync(store);
        using var phone = await ConnectAsync(fixture.Port);
        await SendHelloAsync(phone.GetStream(), Hello() with { DeviceName = "Unverified new name" });
        Assert.Equal(MessageType.AuthChallenge, (await ReadAsync(phone.GetStream())).Type);
        Assert.Equal(before, store.State);
        Assert.Equal(before.Device!.PublicDevice, fixture.Core.TrustedDevice);
        var frame = invalidProof
            ? FrameCodec.Json(MessageType.AuthProof, 2, new AuthProofPayload(new string('A', 43), Nonce))
            : FrameCodec.Json(MessageType.Start, 2, new CommandPayload("START"));
        await WriteAsync(phone.GetStream(), frame);
        Assert.Equal(invalidProof ? "auth_proof_invalid" : "auth_required", await ErrorAsync(phone.GetStream()));
        Assert.Equal(before, store.Load());
        Assert.Equal(saves, store.Saves);
        Assert.Equal(before.Device.PublicDevice, fixture.Core.TrustedDevice);
        Assert.False(fixture.Core.Snapshot.AndroidConnected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("phone")]
    public async Task AuthenticatedBlankOrUnchangedNameDoesNotRewriteTrust(string? name)
    {
        var store = MemoryTrustStore.Trusted();
        var before = store.State;
        var saves = store.Saves;
        await using var fixture = await Fixture.StartAsync(store);
        using var phone = await ConnectAsync(fixture.Port);
        var windows = await SendHelloAsync(phone.GetStream(), Hello() with { DeviceName = name });
        await AuthenticateAsync(phone.GetStream(), windows);
        Assert.Equal(before, store.Load());
        Assert.Equal(saves, store.Saves);
        Assert.Equal(before.Device!.PublicDevice, fixture.Core.TrustedDevice);
    }

    public static IEnumerable<object[]> InvalidDeviceNames()
    {
        foreach (var name in new[] { new string('x', 257), "phone\u0000name", "phone\nname", "phone\rname", "phone\tname", "phone\u007fname", "phone\u0085name" })
        {
            yield return [name, false];
            yield return [name, true];
        }
    }

    [Theory]
    [MemberData(nameof(InvalidDeviceNames))]
    public async Task InvalidHelloNameIsRejectedBeforeApprovalOrPersistence(string name, bool remembered)
    {
        var store = remembered ? MemoryTrustStore.Trusted() : new MemoryTrustStore();
        var before = store.State;
        var saves = store.Saves;
        await using var fixture = await Fixture.StartAsync(store);
        using var phone = await ConnectAsync(fixture.Port);
        await SendHelloAsync(phone.GetStream(), Hello(!remembered, remembered ? PairingId : null) with { DeviceName = name });
        Assert.Equal("hello_rejected", await ErrorAsync(phone.GetStream()));
        Assert.Equal(before, store.Load());
        Assert.Equal(saves, store.Saves);
        Assert.Equal(before.Device?.PublicDevice, fixture.Core.TrustedDevice);
        Assert.Null(fixture.Core.PendingPairing);
        Assert.False(fixture.Request.Task.IsCompleted);
        Assert.False(fixture.Core.Snapshot.AndroidConnected);
    }

    private sealed class TestClock : TimeProvider
    {
        private long ticks;
        private long utcTicks = DateTimeOffset.UtcNow.Ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref ticks);
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref utcTicks), TimeSpan.Zero);
        public void Advance(TimeSpan monotonic, TimeSpan wall)
        {
            Interlocked.Add(ref ticks, monotonic.Ticks);
            Interlocked.Add(ref utcTicks, wall.Ticks);
        }
    }

    private static async Task<string> ErrorAsync(Stream stream)
    {
        var frame = await ReadAsync(stream);
        Assert.Equal(MessageType.Error, frame.Type);
        return FrameCodec.ReadJson<ErrorPayload>(frame).Code;
    }
    private static async Task UntilAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition()) await Task.Delay(10, deadline.Token);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public int Port { get; } = FreePort();
        public MemoryTrustStore Store { get; }
        public TestSink Sink { get; } = new();
        public CoreController Core { get; }
        public TaskCompletionSource<PendingPairing> Request { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Fixture(MemoryTrustStore store, TimeSpan? timeout, TimeProvider? clock)
        {
            Store = store;
            Core = new CoreController(new CoreOptions(TcpPort: Port, DiscoveryPort: 0, PairingTimeout: timeout), Sink,
                sessionMonitor: new ManualSessionMonitor(), trustStore: store, timeProvider: clock);
            Core.PairingRequested += (_, pending) => Request.TrySetResult(pending);
        }
        public static async Task<Fixture> StartAsync(MemoryTrustStore? store = null, TimeSpan? timeout = null, TimeProvider? clock = null)
        {
            var fixture = new Fixture(store ?? new MemoryTrustStore(), timeout, clock);
            await fixture.Core.StartAsync();
            return fixture;
        }
        public ValueTask DisposeAsync() => Core.DisposeAsync();
    }
    private sealed class TestSink : IAudioSink
    {
        public string Name => "test";
        public bool IsOpen { get; private set; }
        public void Open() => IsOpen = true;
        public void Write(ReadOnlySpan<byte> data) { }
        public void Close() => IsOpen = false;
        public void Dispose() => Close();
    }
}
