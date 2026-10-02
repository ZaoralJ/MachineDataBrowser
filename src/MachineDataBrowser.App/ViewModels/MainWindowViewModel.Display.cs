using CommunityToolkit.Mvvm.Input;
using MachineDataBrowser.App.Services;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.App.ViewModels;

public sealed partial class MainWindowViewModel
{
    /// <summary>Format, scaling and unit for the selected Watch rows (display only).</summary>
    [RelayCommand(CanExecute = nameof(HasSelectedWatchItem))]
    private async Task EditDisplayAsync()
    {
        var items = WatchSelectionOrCurrent();
        if (items.Count == 0 || Dialogs is null)
        {
            return;
        }

        var label = items.Count == 1 ? items[0].DisplayName : $"{items.Count} items";
        if (await Dialogs.EditDisplayAsync(items[0].Display, label, items[0]) is not { } display)
        {
            return;
        }

        foreach (var item in items)
        {
            item.Display = display;
        }

        MarkDirty();
        StatusMessage = $"{label}: shown {display.Describe()}";
    }

    /// <summary>Engineering units of new watch items, in one round trip (best effort: values show without them).</summary>
    private async Task LoadUnitsAsync(IReadOnlyList<WatchItemViewModel> items)
    {
        if (_client is not IEngineeringUnitsSource source || items.Count == 0)
        {
            return;
        }

        try
        {
            var ids = items.Select(i => i.NodeId).ToList();
            var units = await Task.Run(() => source.ReadUnitsAsync(ids));
            for (var i = 0; i < items.Count && i < units.Count; i++)
            {
                items[i].ServerUnit = units[i];
            }
        }
        catch (Exception ex) when (AppErrors.IsRecoverable(ex))
        {
            AppErrors.Log(ex, "reading engineering units");
        }
    }
}
