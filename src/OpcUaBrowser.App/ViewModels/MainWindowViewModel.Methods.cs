using CommunityToolkit.Mvvm.Input;
using Opc.Ua;
using OpcUaBrowser.Core;

namespace OpcUaBrowser.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly List<MethodCallViewModel> _methodCalls = [];

    /// <summary>The connection can call methods (OPC UA).</summary>
    public bool SupportsMethods => _client is IMethodCaller;

    private bool CanCallMethod(NodeViewModel? node) =>
        IsConnected && SupportsMethods && (node ?? SelectedNode) is { NodeClass: NodeClass.Method, Parent: not null };

    /// <summary>Opens the call form of a method node; it is called on the object it was browsed from.</summary>
    [RelayCommand(CanExecute = nameof(CanCallMethod))]
    private async Task CallMethodAsync(NodeViewModel? node)
    {
        if ((node ?? SelectedNode) is not { NodeClass: NodeClass.Method, Parent: { } owner } method
            || _client is not IMethodCaller caller || Dialogs is null)
        {
            return;
        }

        var call = new MethodCallViewModel(caller, owner.NodeId, method.NodeId, method.DisplayName, owner.DisplayName) { IsReadOnly = IsReadOnly };
        _methodCalls.Add(call);
        Dialogs.ShowMethodCall(call);
        await call.LoadAsync();
    }
}
