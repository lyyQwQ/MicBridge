using NAudio.Wave;

namespace MicBridge.Core.Audio;

public sealed class NAudioSink : MicBridge.Core.Control.IAudioSink
{
    private readonly object gate = new();
    private WaveOutEvent? output;
    private CatchUpWaveProvider? buffer;
    private string name = "VB-Cable";

    public string Name
    {
        get { lock (gate) return name; }
    }

    public bool IsOpen
    {
        get { lock (gate) return output is not null; }
    }

    public void Open()
    {
        lock (gate)
        {
            if (output is not null) return;
            var device = FindDevice();
            var waveFormat = new WaveFormat(48_000, 16, 1);
            var nextBuffer = new CatchUpWaveProvider(waveFormat, message => Console.Error.WriteLine(message));
            var nextOutput = new WaveOutEvent
            {
                DeviceNumber = device.Number,
                DesiredLatency = 80,
                // At 80 ms total, two ~40 ms blocks balance refill overhead and underrun tolerance.
                NumberOfBuffers = 2,
            };
            try
            {
                nextOutput.Init(nextBuffer);
                nextOutput.Play();
            }
            catch
            {
                nextOutput.Dispose();
                throw;
            }

            name = device.Name;
            buffer = nextBuffer;
            output = nextOutput;
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (gate)
        {
            if (buffer is null) throw new InvalidOperationException("VB-Cable sink is not open");
            if (data.Length == 0) return;
            buffer.AddSamples(data);
        }
    }

    public void Close()
    {
        lock (gate)
        {
            output?.Stop();
            output?.Dispose();
            output = null;
            buffer = null;
        }
    }

    public void Dispose() => Close();

    private static (int Number, string Name) FindDevice()
    {
        for (var index = 0; index < WaveOut.DeviceCount; index++)
        {
            var capabilities = WaveOut.GetCapabilities(index);
            var deviceName = capabilities.ProductName;
            if (deviceName.Contains("cable input", StringComparison.OrdinalIgnoreCase) ||
                deviceName.Contains("vb-audio", StringComparison.OrdinalIgnoreCase))
            {
                return (index, deviceName);
            }
        }

        throw new InvalidOperationException("VB-Cable output device not found; install VB-Cable and select its CABLE Input endpoint");
    }
}

/// <summary>Retains recent PCM audio when a stalled consumer accumulates a backlog.</summary>
public sealed class CatchUpWaveProvider : IWaveProvider
{
    private readonly object gate = new();
    private readonly BufferedWaveProvider buffer;
    private readonly byte[] discardedSamples;
    private readonly int thresholdBytes;
    private readonly int targetBytes;
    private readonly Action<string>? log;
    private bool catchUpLogged;

    public CatchUpWaveProvider(WaveFormat waveFormat, Action<string>? log = null)
    {
        buffer = new BufferedWaveProvider(waveFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(500),
        };
        thresholdBytes = waveFormat.ConvertLatencyToByteSize(200);
        targetBytes = waveFormat.ConvertLatencyToByteSize(80);
        discardedSamples = new byte[buffer.BufferLength];
        this.log = log;
    }

    public WaveFormat WaveFormat => buffer.WaveFormat;

    public void AddSamples(ReadOnlySpan<byte> data)
    {
        if (data.Length % WaveFormat.BlockAlign != 0)
            throw new ArgumentException("PCM data must contain complete sample frames", nameof(data));
        if (data.IsEmpty) return;

        var reportCatchUp = false;
        lock (gate)
        {
            var queuedBytes = buffer.BufferedBytes;
            if ((long)queuedBytes + data.Length > thresholdBytes)
            {
                // Trim before adding, including oversized input, so the newest audio always wins.
                var discardBytes = (long)queuedBytes + data.Length - targetBytes;
                var discardQueuedBytes = (int)Math.Min(queuedBytes, discardBytes);
                buffer.Read(discardedSamples, 0, discardQueuedBytes);
                data = data[(int)(discardBytes - discardQueuedBytes)..];
                reportCatchUp = !catchUpLogged;
                catchUpLogged = true;
            }

            buffer.AddSamples(data.ToArray(), 0, data.Length);
        }

        // At most once per provider (capture session), even during repeated stalls.
        if (reportCatchUp)
            log?.Invoke("Audio sink backlog exceeded 200 ms; discarded oldest PCM to retain the latest 80 ms");
    }

    public int Read(byte[] destination, int offset, int count)
    {
        // The device reader shares this lock with trim/write, making eviction atomic.
        lock (gate) return buffer.Read(destination, offset, count);
    }
}
