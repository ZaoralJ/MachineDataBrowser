using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Opc.Ua;
using OpcUaBrowser.App.Services;
using OpcUaBrowser.Core;

namespace OpcUaBrowser.App.ViewModels;

/// <summary>One input box of the method call form.</summary>
public sealed partial class MethodInputViewModel(MethodArgument argument) : ObservableObject
{
    public MethodArgument Argument { get; } = argument;

    public string Label => $"{Argument.Name} ({Argument.DataType})";

    public string Hint => string.IsNullOrEmpty(Argument.Description)
        ? (Argument.IsArray ? "Comma-separated, e.g. [1, 2, 3]" : string.Empty)
        : Argument.Description + (Argument.IsArray ? " · comma-separated, e.g. [1, 2, 3]" : string.Empty);

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;
}

/// <summary>One output of the last call.</summary>
public sealed record MethodOutput(string Label, string Value);

/// <summary>Calls one method: argument form, Call, and the outputs of the last call.</summary>
public sealed partial class MethodCallViewModel : ObservableObject
{
    private readonly IMethodCaller _caller;
    private readonly NodeId _objectId;
    private readonly NodeId _methodId;
    private IReadOnlyList<MethodArgument> _outputs = [];

    public MethodCallViewModel(IMethodCaller caller, NodeId objectId, NodeId methodId, string methodName, string objectName)
    {
        _caller = caller;
        _objectId = objectId;
        _methodId = methodId;
        MethodName = methodName;
        ObjectName = objectName;
    }

    public string MethodName { get; }

    public string ObjectName { get; }

    public string Title => $"Call {MethodName}";

    public ObservableCollection<MethodInputViewModel> Inputs { get; } = [];

    public ObservableCollection<MethodOutput> Outputs { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CallCommand))]
    public partial bool IsLoaded { get; private set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CallCommand))]
    public partial bool IsCalling { get; private set; }

    [ObservableProperty]
    public partial string StatusText { get; private set; } = "Reading arguments…";

    [ObservableProperty]
    public partial bool IsError { get; private set; }

    public bool HasInputs => Inputs.Count > 0;

    public async Task LoadAsync()
    {
        try
        {
            var caller = _caller;
            var methodId = _methodId;
            var signature = await Task.Run(() => caller.GetMethodSignatureAsync(methodId));
            foreach (var input in signature.Inputs)
            {
                Inputs.Add(new MethodInputViewModel(input));
            }

            _outputs = signature.Outputs;
            OnPropertyChanged(nameof(HasInputs));
            IsLoaded = true;
            StatusText = $"On {ObjectName} · {Describe(signature.Inputs.Count, "input")}, {Describe(signature.Outputs.Count, "output")}";
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            IsError = true;
            StatusText = $"Could not read the arguments: {AppErrors.Describe(ex)}";
        }
    }

    private bool CanCall() => IsLoaded && !IsCalling;

    [RelayCommand(CanExecute = nameof(CanCall))]
    private async Task CallAsync()
    {
        IsCalling = true;
        IsError = false;
        StatusText = "Calling…";
        try
        {
            var caller = _caller;
            var (objectId, methodId) = (_objectId, _methodId);
            var inputs = Inputs.Select(i => i.Text).ToList();
            var results = await Task.Run(() => caller.CallMethodAsync(objectId, methodId, inputs));
            Outputs.Clear();
            for (var i = 0; i < results.Count; i++)
            {
                var label = i < _outputs.Count ? $"{_outputs[i].Name} ({_outputs[i].DataType})" : $"Output {i + 1}";
                Outputs.Add(new MethodOutput(label, results[i]));
            }

            StatusText = $"Called at {Timestamps.Format(DateTimeOffset.Now)} · Good"
                + (results.Count == 0 ? " · no outputs" : string.Empty);
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            IsError = true;
            StatusText = AppErrors.Describe(ex);
        }
        finally
        {
            IsCalling = false;
        }
    }

    private static string Describe(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
