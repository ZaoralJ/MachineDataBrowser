using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using MachineDataBrowser.App.ViewModels;
using MachineDataBrowser.App.Views;
using MachineDataBrowser.Core.Tests;
using Xunit;

namespace MachineDataBrowser.App.Tests;

public sealed class MethodCallAppTests(CustomTypesServerFixture server)
{
    [AvaloniaFact]
    public async Task Method_form_lists_arguments_calls_and_shows_outputs_or_errors()
    {
        var dialogs = new TestDialogs();
        await using var vm = new MainWindowViewModel { EndpointUrl = server.EndpointUrl, Dialogs = dialogs };
        await vm.ConnectCommand.ExecuteAsync(null);

        var node = vm.RootNodes[0];
        foreach (var name in new[] { "Objects", "Custom", "Methods", "Stats" })
        {
            await node.EnsureChildrenLoadedAsync();
            node = node.Children.Single(c => c.DisplayName == name);
        }

        Assert.True(vm.CallMethodCommand.CanExecute(node));
        await vm.CallMethodCommand.ExecuteAsync(node);
        var call = Assert.IsType<MethodCallViewModel>(dialogs.MethodCall);
        Assert.Equal("Methods", call.ObjectName);
        var input = Assert.Single(call.Inputs);
        Assert.Equal("Values (Double[])", input.Label);

        input.Text = "[3, 8, 1]";
        await call.CallCommand.ExecuteAsync(null);
        Assert.False(call.IsError, call.StatusText);
        Assert.Equal(["Min (Double)", "Max (Double)", "Mean (Double)"], call.Outputs.Select(o => o.Label));
        Assert.Equal(["1", "8", "4"], call.Outputs.Select(o => o.Value));

        var window = new MethodCallWindow { DataContext = call };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Dispose();
        Dispatcher.UIThread.RunJobs();
        using (var frame = window.CaptureRenderedFrame())
        {
            Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "screenshots"));
            frame!.Save(Path.Combine(AppContext.BaseDirectory, "screenshots", "method-call.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }

        input.Text = "[]";
        await call.CallCommand.ExecuteAsync(null);
        Assert.True(call.IsError);
        Assert.Contains("BadInvalidArgument", call.StatusText, StringComparison.Ordinal);
        window.Close();
    }
}
