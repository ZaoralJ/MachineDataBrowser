using MachineDataBrowser.App.Services;
using Xunit;

namespace MachineDataBrowser.App.Tests;

public sealed class MultiInstanceTests
{
    /// <summary>Several app instances share settings.json; simultaneous saves must neither fail nor corrupt it.</summary>
    [Fact]
    public async Task Concurrent_settings_saves_from_several_instances_stay_valid()
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        var stores = Enumerable.Range(0, 8).Select(_ => new SettingsStore(path)).ToList();
        await Task.WhenAll(stores.Select((store, i) => Task.Run(() =>
        {
            for (var n = 0; n < 50; n++)
            {
                store.Save(new AppSettings { SamplingIntervalMs = (i * 1000) + n });
            }
        })));

        Assert.InRange(new SettingsStore(path).Load().SamplingIntervalMs, 0, 8000);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp"));
        File.Delete(path);
    }
}
