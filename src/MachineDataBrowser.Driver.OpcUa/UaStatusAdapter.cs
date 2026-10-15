using Opc.Ua;
using MachineDataBrowser.Core;

namespace MachineDataBrowser.Driver.OpcUa;

internal static class UaStatusAdapter
{
    public static DeviceStatusCode ToDevice(StatusCode status) =>
        new(status.SymbolicId ?? status.ToString(),
            StatusCode.IsGood(status) ? DeviceStatusSeverity.Good
            : StatusCode.IsUncertain(status) ? DeviceStatusSeverity.Uncertain
            : DeviceStatusSeverity.Bad,
            status.Code);

    public static DeviceError ToDevice(ServiceResult result) =>
        new(ToDevice(result.StatusCode), result.LocalizedText.Text ?? result.StatusCode.ToString());

    public static async Task<T> TranslateAsync<T>(Func<Task<T>> operation)
    {
        try
        {
            return await operation().ConfigureAwait(false);
        }
        catch (ServiceResultException ex)
        {
            throw new DeviceClientException(ToDevice(ex.StatusCode), ex.Message, ex);
        }
    }

    public static async Task TranslateAsync(Func<Task> operation)
    {
        try
        {
            await operation().ConfigureAwait(false);
        }
        catch (ServiceResultException ex)
        {
            throw new DeviceClientException(ToDevice(ex.StatusCode), ex.Message, ex);
        }
    }

    public static T Translate<T>(Func<T> operation)
    {
        try
        {
            return operation();
        }
        catch (ServiceResultException ex)
        {
            throw new DeviceClientException(ToDevice(ex.StatusCode), ex.Message, ex);
        }
    }
}
