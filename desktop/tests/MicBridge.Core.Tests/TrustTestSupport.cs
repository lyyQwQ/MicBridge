using System.Net;
using System.Net.Sockets;
using MicBridge.Core.Control;
using MicBridge.Core.Protocol;

namespace MicBridge.Core.Tests;

internal sealed class MemoryTrustStore : IDeviceTrustStore
{
    public DeviceTrustState State { get; private set; } = new(Guid.NewGuid().ToString("N"), null);
    public bool FailSave { get; set; }
    public bool FailLoad { get; set; }
    public int Saves { get; private set; }
    public DeviceTrustState Load() => FailLoad ? throw new IOException("test read failure") : State;
    public void Save(DeviceTrustState state)
    {
        if (FailSave) throw new IOException("test write failure");
        State = state;
        Saves++;
    }
    public static MemoryTrustStore Trusted()
    {
        var store = new MemoryTrustStore();
        store.Save(store.State with { Device = new DeviceTrustRecord(TrustTestSupport.AndroidId, "phone", TrustTestSupport.PairingId, TrustTestSupport.Secret) });
        return store;
    }
}

internal static class TrustTestSupport
{
    public const string Secret = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8";
    public const string WindowsId = "00000000000000000000000000000000";
    public const string AndroidId = "11111111111111111111111111111111";
    public const string ConnectionId = "33333333333333333333333333333333";
    public const string OtherDeviceId = "55555555555555555555555555555555";
    public const string PairingId = "44444444444444444444444444444444";
    public const string Nonce = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    public static HelloPayload Hello(bool requested = false, string? pairingId = PairingId, string device = AndroidId, string connection = ConnectionId) =>
        new("android", "phone", FrameCodec.AudioRate, FrameCodec.AudioBits, FrameCodec.AudioChannels,
            ["tcp"], device, connection, ["micbridge_mic"], [], "device_trust_v1", requested, pairingId);
    public static async Task<TcpClient> ConnectAsync(int port)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        return client;
    }
    public static async Task<HelloPayload> SendHelloAsync(Stream stream, HelloPayload? hello = null)
    {
        await WriteAsync(stream, FrameCodec.Json(MessageType.Hello, 1, hello ?? Hello()));
        var frame = await ReadAsync(stream);
        Assert.Equal(MessageType.Hello, frame.Type);
        return FrameCodec.ReadJson<HelloPayload>(frame);
    }
    public static async Task AuthenticateAsync(Stream stream, HelloPayload windows, string secret = Secret,
        string pairingId = PairingId, string device = AndroidId, string connection = ConnectionId)
    {
        var challengeFrame = await ReadAsync(stream);
        Assert.Equal(MessageType.AuthChallenge, challengeFrame.Type);
        var challenge = FrameCodec.ReadJson<AuthChallengePayload>(challengeFrame);
        var proof = FrameCodec.ComputeAuthProof(secret, challenge.Challenge, windows.DeviceId!, device, windows.ConnectionId!, connection);
        await WriteAsync(stream, FrameCodec.Json(MessageType.AuthProof, 2, new AuthProofPayload(proof, Nonce)));
        var accepted = await ReadAsync(stream);
        Assert.Equal(MessageType.AuthOk, accepted.Type);
        Assert.Equal(FrameCodec.ComputeServerAuthProof(secret, windows.DeviceId!, device, windows.ConnectionId!, connection,
            pairingId, challenge.Challenge, Nonce, proof), FrameCodec.ReadJson<AuthOkPayload>(accepted).Proof);
        Assert.Equal(MessageType.Status, (await ReadAsync(stream)).Type);
    }
    public static async Task<Frame> ReadAsync(Stream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        return await FrameCodec.ReadAsync(stream, timeout.Token);
    }
    public static async Task WriteAsync(Stream stream, Frame frame) => await stream.WriteAsync(FrameCodec.Encode(frame));
    public static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
