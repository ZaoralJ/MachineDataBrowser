using CommunityToolkit.Mvvm.ComponentModel;
using OpcUaBrowser.App.Services;

namespace OpcUaBrowser.App.ViewModels;

public sealed partial class SettingsViewModel(AppSettings original) : ObservableObject
{
    public static IReadOnlyList<ThemePreference> Themes { get; } = Enum.GetValues<ThemePreference>();

    [ObservableProperty]
    public partial ThemePreference Theme { get; set; } = original.Theme;

    // Preview immediately; the caller restores the saved theme when the dialog is cancelled.
    partial void OnThemeChanged(ThemePreference value) => MainWindowViewModel.ApplyTheme(value);

    [ObservableProperty]
    public partial decimal? SamplingIntervalMs { get; set; } = original.SamplingIntervalMs;

    [ObservableProperty]
    public partial decimal? MaxRecursiveItems { get; set; } = original.MaxRecursiveItems;

    [ObservableProperty]
    public partial bool ReopenLastSession { get; set; } = original.ReopenLastSession;

    public AppSettings ToSettings() => original with
    {
        Theme = Theme,
        SamplingIntervalMs = (int)(SamplingIntervalMs ?? original.SamplingIntervalMs),
        MaxRecursiveItems = (int)(MaxRecursiveItems ?? original.MaxRecursiveItems),
        ReopenLastSession = ReopenLastSession,
    };
}
