using System.Text;

namespace MicBridge.App;

/// <summary>The windowless core host is the sole owner of stderr persistence.</summary>
internal sealed class RotatingErrorWriter(string path, TextWriter original, long maxBytes = 1024 * 1024) : TextWriter
{
    private readonly object gate = new();
    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value) => Write(value.ToString());
    public override void Write(char[] buffer, int index, int count) => Write(new string(buffer, index, count));
    public override void WriteLine(string? value) => Write($"{DateTimeOffset.Now:O} {value}{NewLine}");

    public override void Write(string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        lock (gate)
        {
            try { original.Write(value); }
            catch (IOException) { }
            try
            {
                // Bound even an unexpectedly large diagnostic; audio/protocol payloads are never logged.
                var maxChars = (int)Math.Min(maxBytes / 4, 16 * 1024);
                var text = value.Length > maxChars ? value[..maxChars] : value;
                var bytes = Encoding.UTF8.GetBytes(text);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (File.Exists(path) && new FileInfo(path).Length + bytes.Length > maxBytes)
                    File.Move(path, path + ".1", overwrite: true);
                using var file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
                file.Write(bytes);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A full disk or unavailable log directory must not stop capture or the control server.
            }
        }
    }
}
