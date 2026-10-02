using Opc.Ua;
using MachineDataBrowser.Core.Mqtt;
using Xunit;

namespace MachineDataBrowser.Core.Tests;

/// <summary>MqttModel's Sparkplug B handling with hand-built payloads (no broker needed).</summary>
public sealed class SparkplugModelTests
{
    private const string Data = "spBv1.0/G/DDATA/E/D";
    private const string Birth = "spBv1.0/G/DBIRTH/E/D";

    [Fact]
    public void Placeholder_ids_follow_the_named_metric_after_the_birth()
    {
        var model = new MqttModel(100);

        // DATA before the BIRTH: only the alias is known.
        model.Apply(Data, Payload(Metric(null, 7, 10, Double(1.5))), false, 0, null, DateTime.UtcNow);
        Assert.Equal(1.5, model.Current("m:G|E|D|alias 7")?.Raw);

        // The birth names alias 7; the placeholder leaves the tree but its id keeps following "Temperature".
        var changed = model.Apply(Birth, Payload(Metric("Temperature", 7, 10, Double(2.5))), false, 0, null, DateTime.UtcNow);
        Assert.Contains("m:G|E|D|alias 7", changed);
        Assert.DoesNotContain(model.Browse("d:G|E|D"), i => i.DisplayName.StartsWith("alias", StringComparison.Ordinal));
        Assert.Equal(2.5, model.Current("m:G|E|D|alias 7")?.Raw);

        changed = model.Apply(Data, Payload(Metric(null, 7, 10, Double(3.5))), false, 0, null, DateTime.UtcNow);
        Assert.Contains("m:G|E|D|alias 7", changed);
        Assert.Equal(3.5, model.Current("m:G|E|D|alias 7")?.Raw);
        Assert.Equal(StatusCodes.Good, model.Current("m:G|E|D|alias 7")?.Status);
    }

    private static byte[] Payload(params byte[][] metrics) => [.. Field(1, Varint(1)), .. metrics.SelectMany(m => Bytes(2, m))];

    private static byte[] Metric(string? name, ulong alias, uint type, byte[] value) =>
        [.. name is null ? [] : Bytes(1, System.Text.Encoding.UTF8.GetBytes(name)), .. Field(2, Varint(alias)), .. Field(4, Varint(type)), .. value];

    private static byte[] Double(double value) => [(13 << 3) | 1, .. BitConverter.GetBytes(value)];

    private static byte[] Field(int number, byte[] varint) => [.. Varint((ulong)(number << 3)), .. varint];

    private static byte[] Bytes(int number, byte[] data) => [.. Varint((ulong)((number << 3) | 2)), .. Varint((ulong)data.Length), .. data];

    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            bytes.Add(value != 0 ? (byte)(b | 0x80) : b);
        }
        while (value != 0);
        return [.. bytes];
    }
}
