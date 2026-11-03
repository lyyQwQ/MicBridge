using NAudio.Wave;
using MicBridge.Core.Audio;

namespace MicBridge.Core.Tests;

public sealed class AudioBacklogPolicyTests
{
    private static readonly WaveFormat Format = new(48_000, 16, 1);

    [Fact]
    public void AtThresholdPreservesAllSamplesInOrder()
    {
        var logs = new List<string>();
        var provider = new CatchUpWaveProvider(Format, logs.Add);
        var first = Samples(100, 0x11);
        var second = Samples(100, 0x22);

        provider.AddSamples(first);
        provider.AddSamples(second);

        Assert.Equal(first.Concat(second).ToArray(), Read(provider, 200));
        Assert.Empty(logs);
    }

    [Fact]
    public void CrossingThresholdDropsOldestAudioAndRetainsNewestTail()
    {
        var provider = new CatchUpWaveProvider(Format);
        provider.AddSamples(Samples(140, 0x11));
        provider.AddSamples(Samples(60, 0x22));

        provider.AddSamples(Samples(20, 0x33));

        var expected = Samples(60, 0x22).Concat(Samples(20, 0x33)).ToArray();
        Assert.Equal(expected, Read(provider, 80));
        Assert.Equal(new byte[Bytes(20)], Read(provider, 20));
    }

    [Fact]
    public void OversizedInputKeepsItsNewestSamplesInsteadOfOverflowing()
    {
        var provider = new CatchUpWaveProvider(Format);
        provider.AddSamples(Samples(100, 0x11));
        var incoming = Samples(520, 0x22).Concat(Samples(80, 0x33)).ToArray();

        provider.AddSamples(incoming);

        Assert.Equal(Samples(80, 0x33), Read(provider, 80));
        Assert.Equal(new byte[Bytes(20)], Read(provider, 20));
    }

    [Fact]
    public void ConsumedAudioDoesNotCountTowardsBacklog()
    {
        var logs = new List<string>();
        var provider = new CatchUpWaveProvider(Format, logs.Add);
        provider.AddSamples(Samples(180, 0x11));
        Assert.Equal(Samples(160, 0x11), Read(provider, 160));

        provider.AddSamples(Samples(100, 0x22));

        Assert.Equal(Samples(20, 0x11).Concat(Samples(100, 0x22)).ToArray(), Read(provider, 120));
        Assert.Empty(logs);
    }

    [Fact]
    public void RepeatedCatchUpKeepsRecentAudioButLogsOnlyOnce()
    {
        var logs = new List<string>();
        var provider = new CatchUpWaveProvider(Format, logs.Add);
        for (byte marker = 1; marker <= 3; marker++)
        {
            provider.AddSamples(Samples(220, marker));
            Assert.Equal(Samples(80, marker), Read(provider, 80));
        }

        Assert.Single(logs);
    }

    [Fact]
    public void OneSampleOverThresholdRetainsCompleteNewestSamples()
    {
        var provider = new CatchUpWaveProvider(Format);
        var queued = Samples(200, 0x11);
        byte[] newestSample = [0x34, 0x12];
        provider.AddSamples(queued);

        provider.AddSamples(newestSample);

        var expected = queued.AsSpan(queued.Length - Bytes(80) + Format.BlockAlign).ToArray()
            .Concat(newestSample).ToArray();
        Assert.Equal(expected, Read(provider, 80));
    }

    private static int Bytes(int milliseconds) => Format.ConvertLatencyToByteSize(milliseconds);

    private static byte[] Samples(int milliseconds, byte marker) =>
        Enumerable.Repeat(marker, Bytes(milliseconds)).ToArray();

    private static byte[] Read(CatchUpWaveProvider provider, int milliseconds)
    {
        var result = new byte[Bytes(milliseconds)];
        Assert.Equal(result.Length, provider.Read(result, 0, result.Length));
        return result;
    }
}
