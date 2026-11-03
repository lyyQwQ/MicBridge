using System.Text.Json;
using System.Text.Json.Serialization;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace MicBridge.Core.Protocol;

public enum MessageType : byte
{
    Hello = 1,
    Start = 2,
    Stop = 3,
    Status = 4,
    Ping = 5,
    Response = 6,
    Audio = 7,
    Error = 8,
    AuthChallenge = 9,
    AuthProof = 10,
    AuthOk = 11,
    PairGrant = 12,
    PairAck = 13,
}

public readonly record struct Frame(MessageType Type, uint Sequence, byte[] Data);

public static class FrameCodec
{
    public const int HeaderSize = 20;
    public const int MaxPayload = 1 << 20;
    public const int MaxControl = 64 << 10;
    public const int MaxUdpPacket = 1200;
    public const int AudioRate = 48_000;
    public const int AudioBits = 16;
    public const int AudioChannels = 1;
    public const string AudioTransportTcp = "tcp";
    public const string AudioTransportUdp = "udp";
    public const int AuthChallengeLifetimeMilliseconds = 10_000;

    private static readonly byte[] Magic = "MCBR"u8.ToArray();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string AuthContext = "MICBRIDGE-AUTH-V1";

    public static AuthChallengePayload CreateAuthChallenge(string deviceId, string connectionId, long nowUnixMilliseconds)
    {
        var challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal)
            .TrimEnd('=');
        return new AuthChallengePayload(
            challenge,
            deviceId,
            connectionId,
            nowUnixMilliseconds + AuthChallengeLifetimeMilliseconds);
    }

    public static string ComputeAuthProof(
        string secret,
        string challenge,
        string localDeviceId,
        string remoteDeviceId,
        string localConnectionId,
        string remoteConnectionId)
    {
        var message = AuthMessage(challenge, localDeviceId, remoteDeviceId, localConnectionId, remoteConnectionId);
        var digest = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(message));
        return Convert.ToBase64String(digest)
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal)
            .TrimEnd('=');
    }

    public static string ComputeServerAuthProof(string secret, string windowsDeviceId, string androidDeviceId,
        string windowsConnectionId, string androidConnectionId, string pairingId, string challenge, string nonce, string phoneProof)
    {
        var message = $"MICBRIDGE-SERVER-AUTH-V1|1|{windowsDeviceId}|{androidDeviceId}|{windowsConnectionId}|{androidConnectionId}|{pairingId}|{challenge}|{nonce}|{phoneProof}";
        return Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(message)))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    public static bool FixedTimeEquals(string expected, string actual)
    {
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        var actualBytes = Encoding.ASCII.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length &&
            CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static string AuthMessage(
        string challenge,
        string localDeviceId,
        string remoteDeviceId,
        string localConnectionId,
        string remoteConnectionId)
    {
        var devices = new[] { localDeviceId, remoteDeviceId };
        Array.Sort(devices, StringComparer.Ordinal);
        var connections = new[] { localConnectionId, remoteConnectionId };
        Array.Sort(connections, StringComparer.Ordinal);
        return $"{AuthContext}|1|{devices[0]}|{devices[1]}|{connections[0]}|{connections[1]}|{challenge}";
    }

    public static byte[] Encode(Frame frame)
    {
        ValidatePayload(frame.Type, frame.Data.Length);
        var bytes = new byte[HeaderSize + frame.Data.Length];
        Magic.CopyTo(bytes, 0);
        bytes[4] = 1;
        bytes[5] = (byte)frame.Type;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8, 4), frame.Sequence);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12, 4), (uint)frame.Data.Length);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), Crc32(frame.Data));
        frame.Data.CopyTo(bytes, HeaderSize);
        return bytes;
    }

    public static Frame Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize)
        {
            throw new InvalidDataException("protocol frame header is incomplete");
        }

        if (!bytes[..4].SequenceEqual(Magic))
        {
            throw new InvalidDataException("protocol frame magic is invalid");
        }

        if (bytes[4] != 1)
        {
            throw new InvalidDataException($"protocol version {bytes[4]} is unsupported");
        }

        var type = (MessageType)bytes[5];
        if (!Enum.IsDefined(type))
        {
            throw new InvalidDataException($"protocol message type {bytes[5]} is unsupported");
        }

        var length = ReadPayloadLength(bytes[12..16], type);
        if (bytes.Length != HeaderSize + length)
        {
            throw new InvalidDataException("protocol frame length does not match payload");
        }

        var payload = bytes[HeaderSize..].ToArray();
        var checksum = BinaryPrimitives.ReadUInt32BigEndian(bytes[16..20]);
        if (Crc32(payload) != checksum)
        {
            throw new InvalidDataException("protocol payload checksum mismatch");
        }

        return new Frame(type, BinaryPrimitives.ReadUInt32BigEndian(bytes[8..12]), payload);
    }

    public static async ValueTask<Frame> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[HeaderSize];
        await ReadExactlyAsync(stream, header, cancellationToken);
        var length = ReadPayloadLength(header.AsSpan(12, 4), (MessageType)header[5]);
        var payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken);
        var bytes = new byte[HeaderSize + length];
        header.CopyTo(bytes, 0);
        payload.CopyTo(bytes, HeaderSize);
        return Decode(bytes);
    }

    private static int ReadPayloadLength(ReadOnlySpan<byte> bytes, MessageType type)
    {
        var length = BinaryPrimitives.ReadUInt32BigEndian(bytes);
        if (length > MaxPayload) throw new InvalidDataException("protocol payload exceeds maximum length");
        ValidatePayload(type, (int)length);
        return (int)length;
    }

    public static Frame Json<T>(MessageType type, uint sequence, T value)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        ValidatePayload(type, data.Length);
        return new Frame(type, sequence, data);
    }

    public static T ReadJson<T>(Frame frame)
    {
        if (frame.Type == MessageType.Audio)
        {
            throw new InvalidDataException("audio frame is not JSON");
        }

        ValidatePayload(frame.Type, frame.Data.Length);
        return JsonSerializer.Deserialize<T>(frame.Data, JsonOptions)
            ?? throw new InvalidDataException("JSON payload is null");
    }

    private static async ValueTask ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer[offset..], cancellationToken);
            if (count == 0)
            {
                throw new EndOfStreamException("protocol stream ended before a complete frame was read");
            }
            offset += count;
        }
    }

    private static void ValidatePayload(MessageType type, int length)
    {
        if (length < 0 || length > MaxPayload)
        {
            throw new InvalidDataException($"protocol payload length {length} exceeds the limit");
        }
        if (type != MessageType.Audio && length > MaxControl)
        {
            throw new InvalidDataException("protocol control payload exceeds 64 KiB");
        }
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFF_FFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) == 0 ? crc >> 1 : (crc >> 1) ^ 0xEDB8_8320u;
            }
        }
        return ~crc;
    }
}

public sealed record HelloPayload(
    [property: JsonPropertyName("peer")] string Peer,
    [property: JsonPropertyName("device_name")] string? DeviceName,
    [property: JsonPropertyName("audio_rate")] int AudioRate,
    [property: JsonPropertyName("audio_bits")] int AudioBits,
    [property: JsonPropertyName("audio_channels")] int AudioChannels,
    [property: JsonPropertyName("audio_transports")] IReadOnlyList<string>? AudioTransports,
    [property: JsonPropertyName("device_id")] string? DeviceId = null,
    [property: JsonPropertyName("connection_id")] string? ConnectionId = null,
    [property: JsonPropertyName("sources")] IReadOnlyList<string>? Sources = null,
    [property: JsonPropertyName("sinks")] IReadOnlyList<string>? Sinks = null,
    [property: JsonPropertyName("auth_mode")] string? AuthMode = null,
    [property: JsonPropertyName("pairing_requested")] bool PairingRequested = false,
    [property: JsonPropertyName("pairing_id")] string? PairingId = null);

public sealed record CommandPayload([property: JsonPropertyName("command")] string Command);

public sealed record StatusPayload(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("device")] string? Device = null,
    [property: JsonPropertyName("audio_device")] string? AudioDevice = null,
    [property: JsonPropertyName("detail")] string? Detail = null,
    [property: JsonPropertyName("error_code")] string? ErrorCode = null,
    [property: JsonPropertyName("dropped_audio_frames")] ulong DroppedAudioFrames = 0,
    [property: JsonPropertyName("audio_transport")] string? AudioTransport = null,
    [property: JsonPropertyName("audio_port")] int? AudioPort = null,
    [property: JsonPropertyName("listenerActive")] bool ListenerActive = false);

public sealed record ResponsePayload(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("message")] string? Message = null,
    [property: JsonPropertyName("status")] StatusPayload? Status = null);

public sealed record ErrorPayload(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message);

public sealed record AuthChallengePayload(
    [property: JsonPropertyName("challenge")] string Challenge,
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("connection_id")] string ConnectionId,
    [property: JsonPropertyName("expires_unix_ms")] long ExpiresUnixMilliseconds);

public sealed record AuthProofPayload(
    [property: JsonPropertyName("proof")] string Proof,
    [property: JsonPropertyName("nonce")] string? Nonce = null);

public sealed record AuthOkPayload([property: JsonPropertyName("proof")] string Proof);
public sealed record PairGrantPayload(
    [property: JsonPropertyName("pairing_id")] string PairingId,
    [property: JsonPropertyName("secret")] string Secret);
public sealed record PairAckPayload([property: JsonPropertyName("pairing_id")] string PairingId);
