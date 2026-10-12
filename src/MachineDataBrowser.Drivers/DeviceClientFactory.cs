using MachineDataBrowser.Core;
using MachineDataBrowser.Core.Cip;
using MachineDataBrowser.Core.Mqtt;
using MachineDataBrowser.Core.Ua;

namespace MachineDataBrowser.Drivers;

public sealed class DeviceClientFactory : IDeviceClientFactory
{
    public IDeviceClient Create(string endpointUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointUrl);
        return DeviceClient.IsEip(endpointUrl)
            ? new CipClient()
            : DeviceClient.IsMqtt(endpointUrl)
                ? new MqttDeviceClient()
                : new OpcUaClient();
    }

    public bool Supports(IDeviceClient client, string endpointUrl) => client switch
    {
        CipClient => DeviceClient.IsEip(endpointUrl),
        MqttDeviceClient => DeviceClient.IsMqtt(endpointUrl),
        _ => !DeviceClient.IsEip(endpointUrl) && !DeviceClient.IsMqtt(endpointUrl),
    };
}
