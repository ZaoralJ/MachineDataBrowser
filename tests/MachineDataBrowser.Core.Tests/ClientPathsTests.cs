using Xunit;

namespace MachineDataBrowser.Core.Tests;

public sealed class ClientPathsTests : IDisposable
{
    private readonly string _parent = Directory.CreateTempSubdirectory("datafolders").FullName;

    public void Dispose() => Directory.Delete(_parent, true);

    [Fact]
    public void The_old_data_folder_is_copied_once_and_left_in_place()
    {
        var legacy = Path.Combine(_parent, ClientPaths.LegacyFolderName);
        var root = Path.Combine(_parent, "MachineDataBrowser");
        Directory.CreateDirectory(Path.Combine(legacy, "pki", "trusted", "certs"));
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{\"theme\":\"Dark\"}");
        File.WriteAllText(Path.Combine(legacy, "pki", "trusted", "certs", "server.der"), "cert");

        Assert.True(ClientPaths.MigrateLegacyFolder(legacy, root));
        Assert.Equal("{\"theme\":\"Dark\"}", File.ReadAllText(Path.Combine(root, "settings.json")));
        Assert.True(File.Exists(Path.Combine(root, "pki", "trusted", "certs", "server.der")));
        Assert.True(Directory.Exists(legacy));                       // older versions still find their data
        Assert.False(Directory.Exists(root + ".migrating"));

        // Only once: the new folder wins from then on.
        File.WriteAllText(Path.Combine(legacy, "settings.json"), "{\"theme\":\"Light\"}");
        Assert.False(ClientPaths.MigrateLegacyFolder(legacy, root));
        Assert.Equal("{\"theme\":\"Dark\"}", File.ReadAllText(Path.Combine(root, "settings.json")));
    }

    [Fact]
    public void Nothing_to_migrate_without_an_old_folder() =>
        Assert.False(ClientPaths.MigrateLegacyFolder(Path.Combine(_parent, "missing"), Path.Combine(_parent, "new")));
}
