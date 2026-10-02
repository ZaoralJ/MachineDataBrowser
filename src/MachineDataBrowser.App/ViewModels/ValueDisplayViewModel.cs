using CommunityToolkit.Mvvm.ComponentModel;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.App.ViewModels;

/// <summary>The display format form: format, decimals, scaling and unit, previewed on one row's current value.</summary>
public sealed partial class ValueDisplayViewModel : ObservableObject
{
    private readonly WatchItemViewModel _sample;

    public ValueDisplayViewModel(ValueDisplay current, string target, WatchItemViewModel sample)
    {
        _sample = sample;
        Target = target;
        Format = current.Format;
        Decimals = current.Decimals;
        Gain = (decimal)current.Gain;
        Offset = (decimal)current.Offset;
        ShowUnit = current.Unit is not "";
        Unit = current.Unit ?? string.Empty;
    }

    public string Target { get; }

    public static IReadOnlyList<ValueFormat> Formats { get; } = Enum.GetValues<ValueFormat>();

    public string RawText => $"{_sample.DisplayName}: {_sample.Value} as sent";

    public string UnitPlaceholder => _sample.ServerUnit is { } unit ? $"server: {unit}" : "e.g. °C, bar, %";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview), nameof(HasDecimals))]
    public partial ValueFormat Format { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview))]
    public partial decimal? Decimals { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview))]
    public partial decimal? Gain { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview))]
    public partial decimal? Offset { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview))]
    public partial bool ShowUnit { get; set; }

    /// <summary>Empty = the server's engineering unit (when it has one).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Preview))]
    public partial string Unit { get; set; }

    public bool HasDecimals => Format == ValueFormat.Decimals;

    public string Preview => ToDisplay().Apply(_sample.RawValue, _sample.Value, _sample.ServerUnit);

    public ValueDisplay ToDisplay() => new()
    {
        Format = Format,
        Decimals = (int)(Decimals ?? 2),
        Gain = (double)(Gain ?? 1),
        Offset = (double)(Offset ?? 0),
        Unit = !ShowUnit ? string.Empty : string.IsNullOrWhiteSpace(Unit) ? null : Unit.Trim(),
    };
}
