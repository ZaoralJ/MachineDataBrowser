using System.Buffers.Binary;
using System.Text;
using OpcUaBrowser.Core.Cip;
using Opc.Ua;
using Xunit;
using OpcUaBrowser.Core.Ua;

namespace OpcUaBrowser.Core.Tests;

public sealed class LogixCodecTests
{
    [Fact]
    public void Parses_tag_list_entries()
    {
        var buffer = Concat(
            TagEntry(1, 0x00C4, 4, [], "Counter"),
            TagEntry(2, 0x20CA, 4, [10], "Temps"),
            TagEntry(3, 0x8000 | 0x0123, 88, [], "Recipe"),
            TagEntry(4, 0x1068, 0, [], "Program:MainProgram"));

        var tags = LogixCodec.ParseTagList(buffer);

        Assert.Equal(["Counter", "Temps", "Recipe", "Program:MainProgram"], tags.Select(t => t.Name));
        Assert.Equal(LogixAtomic.Dint, tags[0].Type.Atomic);
        Assert.Empty(tags[0].Dimensions);
        Assert.Equal([10], tags[1].Dimensions);
        Assert.Equal(10, tags[1].ElementCount);
        Assert.True(tags[2].Type.IsStruct);
        Assert.Equal(0x123, tags[2].Type.TemplateId);
        Assert.True(tags[3].Type.IsSystem);
    }

    [Fact]
    public void Parses_udt_template_and_detects_strings()
    {
        var template = LogixCodec.ParseTemplate(Template(0x0FCE, 88, "STRING", ("LEN", 0x00C4, 0, 0), ("DATA", 0x20C2, 82, 4)));

        Assert.Equal("STRING", template.Name);
        Assert.Equal(88, template.InstanceSize);
        Assert.True(template.IsString);
        Assert.True(template.Members[1].IsArray);
        Assert.Equal(82, template.Members[1].ElementCount);

        var data = new byte[88];
        BinaryPrimitives.WriteInt32LittleEndian(data, 5);
        Encoding.ASCII.GetBytes("Hello world").CopyTo(data, 4);
        Assert.Equal("Hello", LogixCodec.DecodeString(template, data));
    }

    [Fact]
    public void Udt_hides_bool_host_members()
    {
        var template = LogixCodec.ParseTemplate(Template(0x0123, 8, "MOTOR",
            ("ZZZZZZZZZZMOTOR0", 0x00C2, 0, 0), ("Running", 0x00C1, 0, 0), ("Faulted", 0x00C1, 1, 0), ("Speed", 0x00CA, 0, 4)));

        Assert.Equal(["Running", "Faulted", "Speed"], template.Members.Where(m => !m.IsHidden).Select(m => m.Name));
        Assert.False(template.IsString);
        Assert.False(template.Members[2].IsArray); // Info is the bit number for BOOLs
    }

    [Fact]
    public void Decodes_atomics_and_arrays()
    {
        Assert.Equal(-2, LogixCodec.DecodeAtomic(LogixAtomic.Dint, BitConverter.GetBytes(-2)));
        Assert.Equal(true, LogixCodec.DecodeAtomic(LogixAtomic.Bool, [0xFF]));
        Assert.Equal(1.5f, LogixCodec.DecodeAtomic(LogixAtomic.Real, BitConverter.GetBytes(1.5f)));
        Assert.Equal(new short[] { 1, -1 }, LogixCodec.DecodeAtomic(LogixAtomic.Int, [1, 0, 0xFF, 0xFF], 2));
        Assert.Equal(new int[] { 7 }, LogixCodec.DecodeAtomic(LogixAtomic.Dint, BitConverter.GetBytes(7), 1, forceArray: true));
    }

    [Theory]
    [InlineData("Counter", null, new[] { "Counter" })]
    [InlineData("Program:Main.Motor[2].Speed", "Program:Main", new[] { "Motor", "[2]", ".Speed" })]
    [InlineData("Grid[1, 2].X", null, new[] { "Grid", "[1,2]", ".X" })]
    public void Parses_tag_paths(string text, string? program, string[] segments)
    {
        Assert.True(TagPath.TryParse(text, out var path));
        Assert.Equal(program, path.Program);
        Assert.Equal(segments, path.Segments);
    }

    [Fact]
    public void Path_prefixes_match_browse_ids()
    {
        Assert.True(TagPath.TryParse("Program:Main.Motor[2].Speed", out var path));
        Assert.Equal(["Program:Main.Motor", "Program:Main.Motor[2]", "Program:Main.Motor[2].Speed"], path.Prefixes());
    }

    [Fact]
    public async Task Cip_client_paths_and_portable_ids()
    {
        await using var client = new CipClient();
        var tag = client.ParsePortableId("Program:Main.Motor.Speed");
        Assert.Equal("Program:Main.Motor.Speed", client.ToPortableId(tag));
        Assert.Equal(new NodeId("Programs", 2), client.ParsePortableId("@Programs"));

        var path = await client.GetPathFromRootAsync(tag, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["@Root", "@Programs", "@Program:Main", "Program:Main.Motor", "Program:Main.Motor.Speed"], path.Select(client.ToPortableId));
        Assert.True(DeviceClient.IsEip("eip://10.0.0.5/1,0"));
        Assert.IsType<CipClient>(DeviceClient.Create("eip://10.0.0.5"));
        Assert.IsType<OpcUaClient>(DeviceClient.Create("opc.tcp://localhost:4840"));
    }

    [Fact]
    public async Task Generated_code_comments_use_the_client_display_id()
    {
        var lamp = new NodeTree(new NodeId("EasyView.AlarmLamp", 1), "AlarmLamp", NodeClass.Object);
        lamp.Children.Add(new NodeTree(new NodeId("EasyView.AlarmLamp.On", 1), "On", NodeClass.Variable) { Value = 1 });

        var ua = NodeExport.ToCSharp(lamp);
        Assert.Contains("/// <summary>ns=1;s=EasyView.AlarmLamp</summary>", ua, StringComparison.Ordinal);
        Assert.Contains("/// <summary>ns=1;s=EasyView.AlarmLamp.On</summary>", ua, StringComparison.Ordinal);

        await using var client = new CipClient();
        var motor = new NodeTree(client.ParsePortableId("Program:Main.Motor"), "Motor", NodeClass.Object);
        motor.Children.Add(new NodeTree(client.ParsePortableId("Program:Main.Motor.Speed"), "Speed", NodeClass.Variable) { Value = 1.5f });

        var cip = NodeExport.ToCSharp(motor, asRecord: true, formatId: client.ToDisplayId);
        Assert.Contains("/// <summary>Program:Main.Motor</summary>", cip, StringComparison.Ordinal);
        Assert.Contains("/// <summary>Program:Main.Motor.Speed</summary>", cip, StringComparison.Ordinal);
        Assert.DoesNotContain("ns=1", cip, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Connect_rejects_bad_url()
    {
        await using var client = new CipClient();
        var ex = await Assert.ThrowsAsync<ServiceResultException>(() => client.ConnectAsync(new ConnectOptions { EndpointUrl = "eip://" }, TestContext.Current.CancellationToken));
        Assert.Equal(StatusCodes.BadTcpEndpointUrlInvalid, ex.StatusCode);
        Assert.Equal(ConnectionState.Disconnected, client.State);
    }

    private static byte[] TagEntry(uint id, ushort type, ushort size, int[] dims, string name)
    {
        var bytes = new byte[22 + name.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, id);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), type);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), size);
        for (var i = 0; i < dims.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8 + (i * 4)), (uint)dims[i]);
        }

        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(20), (ushort)name.Length);
        Encoding.ASCII.GetBytes(name).CopyTo(bytes, 22);
        return bytes;
    }

    private static byte[] Template(ushort id, int size, string name, params (string Name, ushort Type, ushort Info, int Offset)[] members)
    {
        var header = new byte[14 + (members.Length * 8)];
        BinaryPrimitives.WriteUInt16LittleEndian(header, id);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(6), (uint)size);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(10), (ushort)members.Length);
        for (var i = 0; i < members.Length; i++)
        {
            var o = 14 + (i * 8);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(o), members[i].Info);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(o + 2), members[i].Type);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(o + 4), (uint)members[i].Offset);
        }

        var names = Encoding.ASCII.GetBytes($"{name};n\0" + string.Concat(members.Select(m => m.Name + "\0")));
        return Concat(header, names);
    }

    private static byte[] Concat(params byte[][] parts) => [.. parts.SelectMany(p => p)];
}
