using MachineDataBrowser.App.Services;
using Xunit;

namespace MachineDataBrowser.App.Tests;

public sealed class SessionCollectionDocumentTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mdb-session-collections").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public async Task Save_and_load_preserves_order_and_session_settings()
    {
        var path = Path.Combine(_directory, "plant.mdbsessions");
        var document = new SessionCollectionDocument
        {
            Sessions =
            [
                new SessionDocument
                {
                    EndpointUrl = "opc.tcp://first:4840",
                    UserName = "operator",
                    CredentialsRequired = true,
                    Watch = [new WatchEntry("nsu=urn:test;s=Temperature", "Temperature")],
                },
                new SessionDocument { EndpointUrl = "modbus://second:502/1", ReadOnly = true },
            ],
        };

        await document.SaveAsync(path, TestContext.Current.CancellationToken);

        var loaded = await SessionCollectionDocument.LoadAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal(SessionCollectionDocument.CurrentVersion, loaded.Version);
        Assert.Equal(["opc.tcp://first:4840", "modbus://second:502/1"], loaded.Sessions.Select(session => session.EndpointUrl));
        Assert.Equal("operator", loaded.Sessions[0].UserName);
        Assert.True(loaded.Sessions[0].CredentialsRequired);
        Assert.Equal("nsu=urn:test;s=Temperature", Assert.Single(loaded.Sessions[0].Watch).NodeId);
        Assert.True(loaded.Sessions[1].ReadOnly);

        var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Load_rejects_an_unsupported_version()
    {
        var path = Path.Combine(_directory, "future.mdbsessions");
        await File.WriteAllTextAsync(path, """
            {"version":2,"sessions":[{"endpointUrl":"opc.tcp://first:4840"}]}
            """, TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => SessionCollectionDocument.LoadAsync(path, TestContext.Current.CancellationToken));

        Assert.Contains("unsupported", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Load_rejects_an_empty_collection()
    {
        var path = Path.Combine(_directory, "empty.mdbsessions");
        await File.WriteAllTextAsync(path, "{\"sessions\":[]}", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidDataException>(() => SessionCollectionDocument.LoadAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Load_rejects_malformed_json()
    {
        var path = Path.Combine(_directory, "malformed.mdbsessions");
        await File.WriteAllTextAsync(path, "{", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<System.Text.Json.JsonException>(() => SessionCollectionDocument.LoadAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Collection_extension_is_distinct_from_individual_session_extensions()
    {
        Assert.True(SessionCollectionDocument.IsSessionCollectionFile("plant.mdbsessions"));
        Assert.False(SessionDocument.IsSessionFile("plant.mdbsessions"));
        Assert.True(SessionDocument.IsSessionFile("device.mdbsession"));
        Assert.True(SessionDocument.IsSessionFile("legacy.opcsession"));
    }
}
