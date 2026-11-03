using System.Security.Cryptography;
using System.Text.Json;
using MicBridge.Core.Control;
using static MicBridge.Core.Tests.TrustTestSupport;

namespace MicBridge.Core.Tests;

[CollectionDefinition("Trust environment", DisableParallelization = true)]
public sealed class TrustEnvironmentCollection { }

[Collection("Trust environment")]
public sealed class DeviceTrustStoreTests
{
    [Fact]
    public void EnvironmentOverrideIsolatesDefaultStoreAndPreservesInstallationIdentity()
    {
        var previous = Environment.GetEnvironmentVariable("MICBRIDGE_TRUST_PATH");
        var folder = Path.Combine(Path.GetTempPath(), "micbridge-env-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "isolated.dat");
        try
        {
            Environment.SetEnvironmentVariable("MICBRIDGE_TRUST_PATH", path);
            var first = new DpapiDeviceTrustStore().Load();
            Assert.True(File.Exists(path));
            Assert.Matches("^[0-9a-f]{32}$", first.InstallationId);
            Assert.Equal(first, new DpapiDeviceTrustStore().Load());
            Assert.Null(first.Device);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MICBRIDGE_TRUST_PATH", previous);
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData(2, Secret)]
    [InlineData(1, "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh9")]
    public async Task UnknownSchemaAndNonCanonicalSecretFailClosed(int version, string secret)
    {
        var folder = Path.Combine(Path.GetTempPath(), "micbridge-schema-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(folder, "trust.dat");
        Directory.CreateDirectory(folder);
        try
        {
            var state = new DeviceTrustState(WindowsId, new DeviceTrustRecord(AndroidId, "phone", PairingId, secret), version);
            var plaintext = JsonSerializer.SerializeToUtf8Bytes(state);
            File.WriteAllBytes(path, ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser));
            Assert.Throws<InvalidDataException>(() => new DpapiDeviceTrustStore(path).Load());
            var ciphertext = File.ReadAllBytes(path);
            await using var core = new CoreController(new CoreOptions(), new Sink(),
                sessionMonitor: new ManualSessionMonitor(), trustStore: new DpapiDeviceTrustStore(path));
            Assert.Equal("repair_required", core.TrustState);
            Assert.Null(core.TrustedDevice);
            Assert.Equal(ciphertext, File.ReadAllBytes(path));
            await core.ForgetDeviceAsync();
            Assert.Equal("unpaired", core.TrustState);
            Assert.Null(new DpapiDeviceTrustStore(path).Load().Device);
        }
        finally { Directory.Delete(folder, true); }
    }

    private sealed class Sink : IAudioSink
    {
        public string Name => "test";
        public bool IsOpen => false;
        public void Open() { }
        public void Write(ReadOnlySpan<byte> data) { }
        public void Close() { }
        public void Dispose() { }
    }
}
