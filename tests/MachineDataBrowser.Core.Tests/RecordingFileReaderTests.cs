using Xunit;

namespace MachineDataBrowser.Core.Tests;

public sealed class RecordingFileReaderTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"rec-{Guid.NewGuid():N}.csv");

    public void Dispose() => File.Delete(_path);

    [Fact]
    public async Task Tails_growing_file_and_keeps_partial_lines_until_complete()
    {
        await File.WriteAllTextAsync(_path, "ReceivedAt,SourceTimestamp,ServerTimestamp,Name,NodeId,Value,Status\n" +
            "2026-09-27T10:00:00.0000000+00:00,,,\"StepUp\",\"nsu=x;s=a\",\"1\",Good\n", TestContext.Current.CancellationToken);
        var reader = new RecordingFileReader(_path);

        Assert.Equal("1", Assert.Single(await reader.ReadNewAsync(TestContext.Current.CancellationToken)).Value);
        Assert.Empty(await reader.ReadNewAsync(TestContext.Current.CancellationToken));

        await File.AppendAllTextAsync(_path, "2026-09-27T10:00:01.0000000+00:00,,,\"StepUp\",\"nsu=x;s=a\",\"a,\"\"q\"\"", TestContext.Current.CancellationToken);
        Assert.Empty(await reader.ReadNewAsync(TestContext.Current.CancellationToken));

        await File.AppendAllTextAsync(_path, "\",Good\n", TestContext.Current.CancellationToken);
        var row = Assert.Single(await reader.ReadNewAsync(TestContext.Current.CancellationToken));
        Assert.Equal("a,\"q\"", row.Value);
        Assert.Equal("StepUp", row.Name);
    }
}
