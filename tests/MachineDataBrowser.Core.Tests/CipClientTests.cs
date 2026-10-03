using Opc.Ua;
using MachineDataBrowser.Core.Cip;
using Xunit;

namespace MachineDataBrowser.Core.Tests;

/// <summary>CipClient against the Logix simulator in <c>simulators/cip</c>: tag listing, UDTs, arrays, polling.</summary>
public sealed class CipClientTests(LogixSimulatorFixture plc) : IAsyncLifetime
{
    private readonly CipClient _client = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NodeId Tag(string path) => new(path, CipClient.TagNamespace);

    private static NodeId Folder(string name) => new(name, CipClient.FolderNamespace);

    public async ValueTask InitializeAsync() =>
        await _client.ConnectAsync(new ConnectOptions { EndpointUrl = plc.EndpointUrl }, Ct);

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private async Task<object?> ReadAsync(string path) => (await _client.ReadValuesAsync([Tag(path)], Ct))[0];

    [Fact]
    public void Connect_sets_state_connected() =>
        Assert.Equal(ConnectionState.Connected, _client.State);

    [Fact]
    public async Task Controller_scope_lists_paged_user_tags_and_hides_system_module_and_hidden_tags()
    {
        var tags = (await _client.BrowseAsync(Folder("Controller"), Ct)).Select(t => t.DisplayName).ToList();

        Assert.Contains("Stations", tags);
        Assert.Contains("Bulk_0001", tags);
        Assert.Contains("Bulk_3000", tags); // beyond the first @tags page
        Assert.DoesNotContain("Local:1:I", tags);
        Assert.DoesNotContain("__HiddenCounter", tags);
        Assert.DoesNotContain("SystemClock", tags);
        Assert.DoesNotContain(tags, t => t.StartsWith("Program:", StringComparison.Ordinal) || t.StartsWith("Task:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Programs_folder_lists_program_scopes_without_routines()
    {
        var programs = (await _client.BrowseAsync(Folder("Programs"), Ct)).Select(p => p.DisplayName).ToList();
        Assert.Contains("MainProgram", programs);
        Assert.Contains("Line20", programs);
        Assert.Contains("Empty", programs);

        var main = (await _client.BrowseAsync(Folder("Program:MainProgram"), Ct)).Select(t => t.DisplayName).ToList();
        Assert.Equal(["Counter", "Station", "Status", "Steps"], main);
        Assert.Empty(await _client.BrowseAsync(Folder("Program:Empty"), Ct));
    }

    [Fact]
    public async Task Array_of_udts_browses_elements_and_members()
    {
        var stations = await _client.BrowseAsync(Tag("Stations"), Ct);
        Assert.Equal(6, stations.Count);
        Assert.Equal("[2]", stations[2].DisplayName);

        var members = (await _client.BrowseAsync(Tag("Stations[2]"), Ct)).Select(m => m.DisplayName).ToList();
        Assert.Equal(["Id", "Name", "Robot", "Conveyor", "Motors", "Alarms", "Status", "Temps", "Enabled"], members);

        Assert.Equal("PRG_02", await ReadAsync("Stations[2].Robot.Program"));
        Assert.Equal("S2M3", await ReadAsync("Stations[1].Motors[2].Name"));
        Assert.Equal((byte)3, await ReadAsync("Stations[2].Id"));
        Assert.Equal(false, await ReadAsync("Stations[4].Enabled"));
    }

    [Fact]
    public async Task Program_scoped_udt_arrays_resolve()
    {
        var servos = await _client.BrowseAsync(Tag("Program:Motion.Servos"), Ct);
        Assert.Equal(4, servos.Count);
        Assert.IsType<double>(await ReadAsync("Program:Motion.Servos[1].Position"));
        Assert.True(Assert.IsType<int>(await ReadAsync("Program:DATALOG.OUTPUT.TOTAL")) >= 1000);
    }

    [Fact]
    public async Task Multi_dimensional_arrays_read_whole_and_by_index()
    {
        // Atomic arrays are single variables; UDT arrays enumerate every index.
        Assert.Equal(20, Assert.IsType<int[]>(await ReadAsync("Matrix")).Length);
        Assert.Equal(23, await ReadAsync("Matrix[2,3]"));
        Assert.Equal(24, Assert.IsType<float[]>(await ReadAsync("Cube")).Length);
        Assert.IsType<float>(await ReadAsync("Cube[1,2,3]"));

        var grid = await _client.BrowseAsync(Tag("Grid"), Ct);
        Assert.Equal(["[0,0]", "[0,1]", "[0,2]", "[1,0]", "[1,1]", "[1,2]", "[2,0]", "[2,1]", "[2,2]"], grid.Select(g => g.DisplayName));
        Assert.Equal(5, await ReadAsync("Grid[1,2].Value"));
    }

    [Fact]
    public async Task Unsigned_and_bit_string_types_decode()
    {
        Assert.Equal(18000000000000000000UL, await ReadAsync("TestUlint"));
        Assert.Equal(4000000000U, await ReadAsync("TestUdint"));
        Assert.Equal((ushort)60000, await ReadAsync("TestUint"));
        Assert.Equal((byte)200, await ReadAsync("TestUsint"));
        Assert.Equal(0x0123456789ABCDEFUL, await ReadAsync("TestLword"));
        Assert.Equal(0xDEADBEEFU, await ReadAsync("TestDword"));
        Assert.Equal(uint.MaxValue, await ReadAsync("Types.UD"));
        Assert.Equal(new ushort[] { 65535, 65534, 65533, 65532, 65531, 65530, 65529, 65528 }, await ReadAsync("Arrays.Uints"));
    }

    [Fact]
    public async Task Custom_string_types_and_string_arrays_decode()
    {
        Assert.Equal("twenty chars max", await ReadAsync("TestString20"));
        Assert.Equal("a forty character capacity string type", await ReadAsync("TestString40"));
        Assert.Equal("gamma", await ReadAsync("TestStringArray[2]"));
        Assert.Equal("short", await ReadAsync("Types.Str20"));

        var strings = await _client.BrowseAsync(Tag("TestStringArray"), Ct);
        Assert.Equal(5, strings.Count);
        Assert.All(strings, s => Assert.Equal(NodeClass.Variable, s.NodeClass));
    }

    [Fact]
    public async Task Packed_bool_members_decode_per_bit()
    {
        Assert.Equal(true, await ReadAsync("Flags.B3"));
        Assert.Equal(false, await ReadAsync("Flags.B2"));
        Assert.Equal(true, await ReadAsync("Flags.B11"));
        Assert.Equal(true, await ReadAsync("BoolsOnly.B"));
    }

    [Fact]
    public async Task Large_arrays_read_whole_and_browse_capped()
    {
        var big = Assert.IsType<int[]>(await ReadAsync("BigDintArray"));
        Assert.Equal(5000, big.Length);
        Assert.Equal(4998, big[4998]);

        Assert.Equal(1000, (await _client.BrowseAsync(Tag("BigStructArray"), Ct)).Count);
        Assert.Equal(1199, await ReadAsync("BigStructArray[1199].Value"));
    }

    [Fact]
    public async Task Deeply_nested_udt_exports_to_csharp()
    {
        var tree = await _client.ReadTreeAsync(Tag("Plant"), "Plant", NodeClass.Object, 8, 2000, Ct);
        var code = NodeExport.ToCSharp(tree);

        Assert.Contains("class Plant", code, StringComparison.Ordinal);
        Assert.Contains("Heater", code, StringComparison.Ordinal);
        Assert.Contains("Setpoint", code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unknown_tag_reads_as_null()
    {
        Assert.Null(await ReadAsync("NoSuchTag"));
        Assert.Null(await ReadAsync("Stations[99].Id"));
    }

    [Fact]
    public async Task Monitoring_reports_values_changing_every_10_ms()
    {
        var values = new System.Collections.Concurrent.ConcurrentBag<string>();
        await using var handle = await _client.MonitorAsync(Tag("HighSpeed.Cycle.Counter"), u => values.Add(u.Value), 50, Ct);

        await Task.Delay(TimeSpan.FromSeconds(2), Ct);

        Assert.True(values.Distinct().Count() >= 5, $"only {values.Distinct().Count()} distinct values");
    }

    [Fact]
    public async Task Diagnostics_show_the_link_and_each_poll_group()
    {
        await using var handle = await _client.MonitorAsync(Tag("HighSpeed.Cycle.Counter"), _ => { }, 100, Ct);
        await Task.Delay(TimeSpan.FromSeconds(1), Ct);

        var rows = (await _client.GetDiagnosticsAsync(Ct)).Session.ToDictionary(r => r.Name, r => r.Value);

        Assert.Equal("1,0 (backplane, slot)", rows["Path"]);
        Assert.Equal("0", rows["Link lost"]);
        Assert.Matches(@"^\d+ controller · \d+ programs", rows["Tags"]);
        Assert.Contains("0 failed", rows["Reads"], StringComparison.Ordinal);
        Assert.StartsWith("1 tags · last ", rows["Poll 100 ms"], StringComparison.Ordinal);
    }
}
