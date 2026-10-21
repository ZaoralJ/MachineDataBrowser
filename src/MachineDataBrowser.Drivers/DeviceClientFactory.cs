using MachineDataBrowser.Core;
using MachineDataBrowser.Driver.Cip;
using MachineDataBrowser.Driver.Modbus;
using MachineDataBrowser.Driver.Mqtt;
using MachineDataBrowser.Driver.OpcUa;
using MachineDataBrowser.Driver.Pccc;

namespace MachineDataBrowser.Drivers;

public sealed class DeviceClientFactory : IDeviceClientFactory
{
    public IDeviceClient Create(string endpointUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointUrl);
        return DeviceClient.IsEip(endpointUrl) ? new CipClient()
            : DeviceClient.IsPccc(endpointUrl) ? new PcccClient()
            : DeviceClient.IsModbus(endpointUrl) ? new ModbusClient()
            : DeviceClient.IsMqtt(endpointUrl) ? new MqttDeviceClient()
            : new OpcUaClient();
    }

    public bool Supports(IDeviceClient client, string endpointUrl) => client switch
    {
        CipClient => DeviceClient.IsEip(endpointUrl),
        PcccClient => DeviceClient.IsPccc(endpointUrl),
        ModbusClient => DeviceClient.IsModbus(endpointUrl),
        MqttDeviceClient => DeviceClient.IsMqtt(endpointUrl),
        _ => !DeviceClient.IsAllenBradley(endpointUrl) && !DeviceClient.IsModbus(endpointUrl) && !DeviceClient.IsMqtt(endpointUrl),
    };
}
