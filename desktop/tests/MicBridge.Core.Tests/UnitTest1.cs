using System.Text;
using MicBridge.Core.Protocol;

namespace MicBridge.Core.Tests;

public sealed class FrameCodecTests
{
    [Fact]
    public void EncodeDecode_PreservesFrameAndChecksum()
    {
        var original = FrameCodec.Json(
            MessageType.Hello,
            42,
            new HelloPayload("android", "Phone", FrameCodec.AudioRate, FrameCodec.AudioBits, FrameCodec.AudioChannels, ["tcp", "udp"]));

        var decoded = FrameCodec.Decode(FrameCodec.Encode(original));

        Assert.Equal(original.Type, decoded.Type);
        Assert.Equal(original.Sequence, decoded.Sequence);
        Assert.Equal(original.Data, decoded.Data);
        var hello = FrameCodec.ReadJson<HelloPayload>(decoded);
        Assert.Equal("android", hello.Peer);
        Assert.Equal(["tcp", "udp"], hello.AudioTransports);
    }

    [Fact]
    public void Decode_RejectsPayloadMutation()
    {
        var bytes = FrameCodec.Encode(new Frame(MessageType.Audio, 1, [1, 2, 3]));
        bytes[^1] ^= 0xFF;

        Assert.Throws<InvalidDataException>(() => FrameCodec.Decode(bytes));
    }

    [Fact]
    public void Encode_RejectsOversizedControlPayload()
    {
        var payload = Encoding.UTF8.GetBytes(new string('x', FrameCodec.MaxControl + 1));

        Assert.Throws<InvalidDataException>(() => FrameCodec.Encode(new Frame(MessageType.Status, 1, payload)));
    }

    [Fact]
    public void AuthProof_IsBoundToSecretAndConnectionIdentity()
    {
        var proof = FrameCodec.ComputeAuthProof("pairing", "challenge", "windows", "android", "win-1", "android-1");

        Assert.Equal(43, proof.Length);
        Assert.True(FrameCodec.FixedTimeEquals(proof, proof));
        Assert.False(FrameCodec.FixedTimeEquals(proof, FrameCodec.ComputeAuthProof("wrong", "challenge", "windows", "android", "win-1", "android-1")));
        Assert.False(FrameCodec.FixedTimeEquals(proof, FrameCodec.ComputeAuthProof("pairing", "challenge", "windows", "android", "win-1", "android-2")));
    }

    [Fact]
    public async Task ReadAsync_HandlesPartialReads()
    {
        var expected = new Frame(MessageType.Audio, 7, [0, 1, 2, 3]);
        await using var stream = new OneByteReadStream(FrameCodec.Encode(expected));

        var actual = await FrameCodec.ReadAsync(stream, CancellationToken.None);

        Assert.Equal(expected.Type, actual.Type);
        Assert.Equal(expected.Sequence, actual.Sequence);
        Assert.Equal(expected.Data, actual.Data);
    }

    private sealed class OneByteReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
        }
    }
}
