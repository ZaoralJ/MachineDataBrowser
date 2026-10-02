using Avalonia.Headless.XUnit;
using MachineDataBrowser.App.Services;
using MachineDataBrowser.App.ViewModels;
using MachineDataBrowser.Core.Tests;
using Xunit;

namespace MachineDataBrowser.App.Tests;

public sealed class CertificatePromptTests(OpcPlcFixture plc)
{
    [AvaloniaFact]
    public async Task Untrusted_certificate_asks_and_connects_only_when_trusted()
    {
        var dialogs = new TestDialogs { TrustAnswer = CertificateTrustChoice.Cancel };
        await using var vm = new MainWindowViewModel { EndpointUrl = plc.EndpointUrl, UseSecurity = true, AutoAcceptCertificates = false, Dialogs = dialogs };

        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.NotNull(dialogs.TrustAsked);
        Assert.False(vm.IsConnected);

        dialogs.TrustAnswer = CertificateTrustChoice.Once;
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.True(vm.IsConnected);
    }
}
