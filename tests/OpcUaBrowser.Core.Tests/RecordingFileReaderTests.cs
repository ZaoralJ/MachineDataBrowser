using Xunit;

namespace OpcUaBrowser.Core.Tests;

public sealed class RecordingFileReaderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rec-{Guid.NewGuid():N}.csv");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public void Tails_growing_file_and_keeps_partial_lines_until_complete()
    {
        File.WriteAllText(_path, "ReceivedAt,SourceTimestamp,ServerTimestamp,Name,NodeId,Value,Status\n" +
            "2026-09-27T10:00:00.0000000+00:00,,,\"StepUp\",\"nsu=x;s=a\",\"1\",Good\n");
        var reader = new RecordingFileReader(_path);

        Assert.Equal("1", Assert.Single(reader.ReadNew()).Value);
        Assert.Empty(reader.ReadNew());

        File.AppendAllText(_path, "2026-09-27T10:00:01.0000000+00:00,,,\"StepUp\",\"nsu=x;s=a\",\"a,\"\"q\"\"");
        Assert.Empty(reader.ReadNew());

        File.AppendAllText(_path, "\",Good\n");
        var row = Assert.Single(reader.ReadNew());
        Assert.Equal("a,\"q\"", row.Value);
        Assert.Equal("StepUp", row.Name);
    }
}
