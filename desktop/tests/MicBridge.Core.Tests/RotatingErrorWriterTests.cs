using MicBridge.App;

namespace MicBridge.Core.Tests;

public sealed class RotatingErrorWriterTests
{
    [Fact]
    public void StderrRotationKeepsOnlyOneBoundedBackup()
    {
        var directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(directory, "core.log");
            using var stderr = new StringWriter();
            using var writer = new RotatingErrorWriter(path, stderr, maxBytes: 256);
            for (var i = 0; i < 30; i++) writer.WriteLine($"event-{i:D2}");
            Assert.Contains("event-29", stderr.ToString());
            Assert.Contains("event-29", File.ReadAllText(path));
            Assert.Equal(2, Directory.GetFiles(directory).Length);
            Assert.InRange(new FileInfo(path).Length, 1L, 256L);
            Assert.InRange(new FileInfo(path + ".1").Length, 1L, 256L);
            writer.Write(new string('x', 4096));
            Assert.InRange(new FileInfo(path).Length, 1L, 256L);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void UnavailableLogDirectoryDoesNotInterruptStderr()
    {
        var file = Path.GetTempFileName();
        try
        {
            using var stderr = new StringWriter();
            using var writer = new RotatingErrorWriter(Path.Combine(file, "core.log"), stderr);
            writer.WriteLine("capture remains available");
            Assert.Contains("capture remains available", stderr.ToString());
        }
        finally { File.Delete(file); }
    }
}
