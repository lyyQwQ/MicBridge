namespace MicBridge.Core.Control;

public enum CoreState
{
    Disconnected,
    ConnectedIdle,
    Starting,
    Streaming,
    Stopping,
    Error,
}

public sealed record CoreSnapshot(
    CoreState State,
    bool AndroidConnected,
    string AndroidPeer,
    string AudioDevice,
    string Detail,
    ulong DroppedAudioFrames,
    string AudioTransport,
    int AudioPort,
    bool PeerConnected = false,
    string PeerName = "",
    [property: System.Text.Json.Serialization.JsonPropertyName("listenerActive")] bool ListenerActive = false)
{
    public static CoreSnapshot Initial => new(CoreState.Disconnected, false, "", "", "", 0, "tcp", 0);
}

public interface IAudioSink : IDisposable
{
    string Name { get; }
    bool IsOpen { get; }
    void Open();
    void Write(ReadOnlySpan<byte> data);
    void Close();
}
