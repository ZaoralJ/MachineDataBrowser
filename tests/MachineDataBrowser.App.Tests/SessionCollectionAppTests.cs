using Avalonia.Headless.XUnit;
using MachineDataBrowser.App.Services;
using MachineDataBrowser.App.ViewModels;
using MachineDataBrowser.App.Views;
using Xunit;

namespace MachineDataBrowser.App.Tests;

public sealed class SessionCollectionAppTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("mdb-session-collections-app").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [AvaloniaFact]
    public async Task Save_collection_preserves_tab_order_settings_and_relative_tag_maps()
    {
        var maps = Directory.CreateDirectory(Path.Combine(_directory, "maps"));
        var mapPath = Path.Combine(maps.FullName, "registers.csv");
        await File.WriteAllTextAsync(mapPath, "address,name\n0,Temperature", TestContext.Current.CancellationToken);
        var path = Path.Combine(_directory, "plant.mdbsessions");
        var first = new MainWindowViewModel
        {
            EndpointUrl = "modbus://first:502/1",
            UseSecurity = true,
            DefaultRefreshMs = 750,
            UserName = "operator",
            Password = "collection-secret",
            TagMapPath = mapPath,
        };
        var window = new MainWindow { DataContext = first, Width = 900, Height = 600 };
        window.Show();
        window.NewConnectionTab();
        var second = Assert.IsType<MainWindowViewModel>(window.DataContext);
        second.EndpointUrl = "opc.tcp://second:4840";
        second.IsReadOnly = true;

        try
        {
            await window.WriteSessionCollectionAsync(path);

            var collection = await SessionCollectionDocument.LoadAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(["modbus://first:502/1", "opc.tcp://second:4840"], collection.Sessions.Select(session => session.EndpointUrl));
            Assert.True(collection.Sessions[0].UseSecurity);
            Assert.Equal(750, collection.Sessions[0].DefaultRefreshMs);
            Assert.Equal("maps/registers.csv", collection.Sessions[0].TagMap);
            Assert.Equal(mapPath, MachineDataBrowser.Core.TagMapReference.Resolve(collection.Sessions[0].TagMap!, path));
            Assert.True(collection.Sessions[0].CredentialsRequired);
            Assert.True(collection.Sessions[1].ReadOnly);

            var json = await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken);
            Assert.DoesNotContain("collection-secret", json, StringComparison.Ordinal);
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Saving_collection_clears_dirty_state_and_save_updates_all_tabs()
    {
        var path = Path.Combine(_directory, "plant.mdbsessions");
        var first = new MainWindowViewModel { EndpointUrl = "opc.tcp://first:4840" };
        var window = new MainWindow { DataContext = first, Width = 900, Height = 600 };
        window.Show();
        window.NewConnectionTab();
        var second = Assert.IsType<MainWindowViewModel>(window.DataContext);
        second.EndpointUrl = "opc.tcp://second:4840";

        try
        {
            await window.WriteSessionCollectionAsync(path);
            Assert.False(window.IsSessionCollectionDirty);
            Assert.All(window.Tabs.Tabs, tab => Assert.False(tab.Connection.IsDirty));

            first.DefaultRefreshMs = 900;
            Assert.True(window.IsSessionCollectionDirty);
            Assert.All(window.Tabs.Tabs, tab => Assert.True(tab.Connection.IsDirty));

            await first.SaveSessionCommand.ExecuteAsync(null);

            Assert.False(window.IsSessionCollectionDirty);
            Assert.All(window.Tabs.Tabs, tab => Assert.False(tab.Connection.IsDirty));
            var collection = await SessionCollectionDocument.LoadAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(900, collection.Sessions[0].DefaultRefreshMs);
            Assert.Equal("opc.tcp://second:4840", collection.Sessions[1].EndpointUrl);

            var saveAsPath = Path.Combine(_directory, "plant-copy.mdbsessions");
            second.DefaultRefreshMs = 1200;
            window.DataContext = first;
            first.Dialogs = new TestDialogs { SessionCollectionSaveTarget = saveAsPath };
            await first.SaveSessionAsCommand.ExecuteAsync(null);
            var copied = await SessionCollectionDocument.LoadAsync(saveAsPath, TestContext.Current.CancellationToken);
            Assert.Equal(1200, copied.Sessions[1].DefaultRefreshMs);
            Assert.False(window.IsSessionCollectionDirty);
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Adding_a_tab_marks_the_current_collection_dirty()
    {
        var path = Path.Combine(_directory, "plant.mdbsessions");
        var first = new MainWindowViewModel { EndpointUrl = "opc.tcp://first:4840" };
        var window = new MainWindow { DataContext = first, Width = 900, Height = 600 };
        window.Show();

        try
        {
            await window.WriteSessionCollectionAsync(path);
            window.NewConnectionTab();

            Assert.True(window.IsSessionCollectionDirty);
            Assert.All(window.Tabs.Tabs, tab => Assert.True(tab.Connection.IsDirty));
        }
        finally
        {
            foreach (var tab in window.Tabs.Tabs)
            {
                await tab.Connection.DisposeAsync();
            }

            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Closing_a_dirty_collection_tab_can_save_the_whole_collection()
    {
        var path = Path.Combine(_directory, "plant.mdbsessions");
        var first = new MainWindowViewModel { EndpointUrl = "opc.tcp://first:4840" };
        var window = new MainWindow { DataContext = first, Width = 900, Height = 600 };
        window.Show();
        window.NewConnectionTab();
        var second = Assert.IsType<MainWindowViewModel>(window.DataContext);

        try
        {
            await window.WriteSessionCollectionAsync(path);
            first.DefaultRefreshMs = 900;
            window.DataContext = first;
            first.Dialogs = new TestDialogs { UnsavedChangesAnswer = MachineDataBrowser.App.Services.UnsavedChangesChoice.Save };

            await window.CloseConnectionTabAsync(first);

            var saved = await SessionCollectionDocument.LoadAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(900, saved.Sessions[0].DefaultRefreshMs);
            Assert.Equal(2, saved.Sessions.Count);
            Assert.True(window.IsSessionCollectionDirty); // removing the saved tab changes collection membership
            Assert.True(second.IsDirty);
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Opening_collection_replaces_tabs_in_order_and_associates_the_collection()
    {
        var path = Path.Combine(_directory, "replacement.mdbsessions");
        await new SessionCollectionDocument
        {
            Sessions =
            [
                new SessionDocument { EndpointUrl = "opc.tcp://first:4840" },
                new SessionDocument { EndpointUrl = "modbus://second:502/1" },
            ],
        }.SaveAsync(path, TestContext.Current.CancellationToken);
        var old = new MainWindowViewModel { EndpointUrl = "opc.tcp://old:4840" };
        var window = new MainWindow { DataContext = old, Width = 900, Height = 600 };
        window.Show();
        old.Dialogs = new TestDialogs { SessionOpenTarget = path };

        try
        {
            await old.OpenSessionCommand.ExecuteAsync(null);

            Assert.Equal(["opc.tcp://first:4840", "modbus://second:502/1"], window.Tabs.Tabs.Select(tab => tab.Connection.EndpointUrl));
            Assert.Same(window.Tabs.Tabs[0].Connection, window.DataContext);
            Assert.Same(window.Tabs.Tabs[0], window.Tabs.Active);
            Assert.False(window.IsSessionCollectionDirty);
            Assert.All(window.Tabs.Tabs, tab => Assert.False(tab.Connection.IsDirty));
            Assert.All(window.Tabs.Tabs, tab => Assert.Equal("replacement", tab.Connection.DocumentName));
        }
        finally
        {
            foreach (var tab in window.Tabs.Tabs)
            {
                await tab.Connection.DisposeAsync();
            }

            await old.DisposeAsync();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Cancelling_unsaved_confirmation_keeps_existing_tabs()
    {
        var path = Path.Combine(_directory, "replacement.mdbsessions");
        await new SessionCollectionDocument
        {
            Sessions = [new SessionDocument { EndpointUrl = "opc.tcp://new:4840" }],
        }.SaveAsync(path, TestContext.Current.CancellationToken);
        var existing = new MainWindowViewModel { EndpointUrl = "opc.tcp://existing:4840" };
        var window = new MainWindow { DataContext = existing, Width = 900, Height = 600 };
        window.Show();
        existing.Dialogs = new TestDialogs { UnsavedChangesAnswer = MachineDataBrowser.App.Services.UnsavedChangesChoice.Cancel };

        try
        {
            await window.OpenSessionCollectionAsync(path);

            Assert.Single(window.Tabs.Tabs);
            Assert.Same(existing, window.Tabs.Tabs[0].Connection);
            Assert.Equal("opc.tcp://existing:4840", existing.EndpointUrl);
        }
        finally
        {
            await existing.DisposeAsync();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Malformed_collection_does_not_replace_existing_tabs()
    {
        var path = Path.Combine(_directory, "broken.mdbsessions");
        await File.WriteAllTextAsync(path, "{", TestContext.Current.CancellationToken);
        var existing = new MainWindowViewModel { EndpointUrl = "opc.tcp://existing:4840" };
        var window = new MainWindow { DataContext = existing, Width = 900, Height = 600 };
        window.Show();

        try
        {
            await window.OpenSessionCollectionAsync(path);

            Assert.Single(window.Tabs.Tabs);
            Assert.Same(existing, window.Tabs.Tabs[0].Connection);
            Assert.Equal("opc.tcp://existing:4840", existing.EndpointUrl);
        }
        finally
        {
            await existing.DisposeAsync();
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Collection_entry_uses_existing_credential_prompt()
    {
        var path = Path.Combine(_directory, "authenticated.mdbsessions");
        await new SessionCollectionDocument
        {
            Sessions =
            [
                new SessionDocument
                {
                    EndpointUrl = "opc.tcp://127.0.0.1:1",
                    UserName = "pack",
                    CredentialsRequired = true,
                    Watch = [new WatchEntry("i=2258", "CurrentTime")],
                },
                new SessionDocument { EndpointUrl = "modbus://second:502/1" },
            ],
        }.SaveAsync(path, TestContext.Current.CancellationToken);
        var existing = new MainWindowViewModel { EndpointUrl = "opc.tcp://existing:4840" };
        var window = new MainWindow { DataContext = existing, Width = 900, Height = 600 };
        window.Show();
        var dialogs = new TestDialogs { SessionPasswordAnswer = new MachineDataBrowser.App.Services.CredentialPromptResult("supplied-secret", Remember: false) };
        existing.Dialogs = dialogs;

        try
        {
            await window.OpenSessionCollectionAsync(path);

            Assert.Equal(("pack", "opc.tcp://127.0.0.1:1"), dialogs.SessionPasswordAsked);
            Assert.Equal(2, window.Tabs.Tabs.Count);
            Assert.Equal("supplied-secret", window.Tabs.Tabs[0].Connection.Password);
            Assert.Equal("modbus://second:502/1", window.Tabs.Tabs[1].Connection.EndpointUrl);
            Assert.DoesNotContain("supplied-secret", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken), StringComparison.Ordinal);
        }
        finally
        {
            foreach (var tab in window.Tabs.Tabs)
            {
                await tab.Connection.DisposeAsync();
            }

            await existing.DisposeAsync();
            window.Close();
        }
    }
}
