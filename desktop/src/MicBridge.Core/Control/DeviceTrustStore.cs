using System.Security.Cryptography;
using System.Text.Json;

namespace MicBridge.Core.Control;

public sealed record TrustedDevice(string DeviceId, string DeviceName, string PairingId);
public sealed record DeviceTrustRecord(string DeviceId, string DeviceName, string PairingId, string Secret)
{
    public TrustedDevice PublicDevice => new(DeviceId, DeviceName, PairingId);
}
public sealed record DeviceTrustState(string InstallationId, DeviceTrustRecord? Device, int SchemaVersion = 1);
public sealed record PendingPairing(string RequestId, string DeviceId, string DeviceName, DateTimeOffset ExpiresAt);

/// <summary>Save must atomically replace the state or throw; never expose an uncommitted device.</summary>
public interface IDeviceTrustStore
{
    DeviceTrustState Load();
    void Save(DeviceTrustState state);
}

public sealed class DpapiDeviceTrustStore : IDeviceTrustStore
{
    private readonly string path;
    public DpapiDeviceTrustStore(string? path = null)
    {
        this.path = Path.GetFullPath(path ?? Environment.GetEnvironmentVariable("MICBRIDGE_TRUST_PATH")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MicBridge", "device-trust.dat"));
    }

    public DeviceTrustState Load()
    {
        if (!File.Exists(path))
        {
            var initial = new DeviceTrustState(Guid.NewGuid().ToString("N"), null);
            Save(initial);
            return initial;
        }
        var plaintext = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        try
        {
            var state = JsonSerializer.Deserialize<DeviceTrustState>(plaintext)
                ?? throw new InvalidDataException("Device trust data is empty");
            Validate(state);
            return state;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public void Save(DeviceTrustState state)
    {
        Validate(state);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(state);
        byte[] ciphertext;
        try { ciphertext = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                file.Write(ciphertext);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static bool IsIdentity(string? value) => value is { Length: 32 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static bool IsSecret(string? value)
    {
        if (value is not { Length: 43 } || !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return false;
        try
        {
            var bytes = Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + "=");
            return bytes.Length == 32 && Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=') == value;
        }
        catch (FormatException) { return false; }
    }
    private static void Validate(DeviceTrustState state)
    {
        if (state.SchemaVersion != 1 || !IsIdentity(state.InstallationId)) throw new InvalidDataException("Invalid trust schema or installation identity");
        if (state.Device is { } device && (!IsIdentity(device.DeviceId) || !IsIdentity(device.PairingId) || !IsSecret(device.Secret)))
            throw new InvalidDataException("Invalid stored device trust");
    }
}
