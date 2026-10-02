using Opc.Ua;
using MachineDataBrowser.Core.Cip;
using Xunit;

namespace MachineDataBrowser.Core.Tests;

/// <summary>Writes through CipClient to the Logix simulator: static tags accept writes, animated values are read-only.</summary>
public sealed class CipWriteTests(LogixSimulatorFixture plc) : IAsyncLifetime
{
    private readonly CipClient _client = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NodeId Tag(string path) => new(path, CipClient.TagNamespace);

    public async ValueTask InitializeAsync() =>
        await _client.ConnectAsync(new ConnectOptions { EndpointUrl = plc.EndpointUrl }, Ct);

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    private async Task<object?> WriteAndReadAsync(string path, string text)
    {
        await _client.WriteValueAsync(Tag(path), text, Ct);
        return (await _client.ReadValuesAsync([Tag(path)], Ct))[0];
    }

    [Fact]
    public async Task Writes_atomic_scalars() =>
        Assert.Equal(1234, await WriteAndReadAsync("Recipe_Active", "1234"));

    [Fact]
    public async Task Writes_udt_members() =>
        Assert.Equal(987.5f, await WriteAndReadAsync("Motor2.Speed", "987.5"));

    [Fact]
    public async Task Writes_program_tags() =>
        Assert.Equal((short)5, await WriteAndReadAsync("Program:Packaging.Mode", "5"));

    [Fact]
    public async Task Writes_strings() =>
        Assert.Equal("written by test", await WriteAndReadAsync("Stations[1].Name", "written by test"));

    [Fact]
    public async Task Writes_whole_atomic_arrays() =>
        Assert.Equal(Enumerable.Range(0, 16).Select(i => (short)(i * 3)).ToArray(),
            await WriteAndReadAsync("Arrays.Ints", "[" + string.Join(", ", Enumerable.Range(0, 16).Select(i => i * 3)) + "]"));

    [Fact]
    public async Task Rejects_writes_to_animated_values()
    {
        var ex = await Assert.ThrowsAsync<ServiceResultException>(() => _client.WriteValueAsync(Tag("Fast.Counter"), "1", Ct));
        Assert.Equal(StatusCodes.BadNotWritable, ex.StatusCode);
        await Assert.ThrowsAsync<ServiceResultException>(() => _client.WriteValueAsync(Tag("Stations[1].Robot.Speed"), "1", Ct));
    }

    [Fact]
    public async Task Rejects_wrong_array_length_and_too_long_strings()
    {
        await Assert.ThrowsAsync<FormatException>(() => _client.WriteValueAsync(Tag("Arrays.Ints"), "1, 2", Ct));
        await Assert.ThrowsAsync<FormatException>(() => _client.WriteValueAsync(Tag("Types.Str20"), new string('x', 21), Ct));
    }
}
