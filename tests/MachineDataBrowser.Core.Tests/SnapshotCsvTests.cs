using Xunit;

namespace MachineDataBrowser.Core.Tests;

public sealed class SnapshotCsvTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("snapshot-csv").FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    private static Snapshot Sample() => new()
    {
        Name = "Line 1",
        TakenAt = DateTimeOffset.Now,
        Items =
        [
            SnapshotItem.Of("Settings/Speed", "nsu=urn:test;s=Speed", "1500", "Good") with { Path = "/Objects/Line1/Settings/Speed" },
            SnapshotItem.Of("Settings/Recipe", "nsu=urn:test;s=Recipe", "Product A, \"special\"\nline 2", "Good"),
            SnapshotItem.Of("Settings/Broken", "nsu=urn:test;s=Broken", string.Empty, "Bad (read failed)"),
        ],
    };

    [Fact]
    public void Csv_round_trips_values_with_separators_quotes_and_line_breaks()
    {
        using var writer = new StringWriter();
        SnapshotCsv.Write(Sample(), writer);
        var read = SnapshotCsv.Read(new StringReader(writer.ToString()), "line1");

        Assert.Equal("line1", read.Name);
        Assert.True(read.Partial);
        Assert.Equal(Sample().Items, read.Items);
        Assert.Equal(1500, read.Items[0].Numeric);
    }

    [Fact]
    public void Csv_from_a_spreadsheet_may_use_semicolons_reorder_columns_and_leave_out_name_and_status()
    {
        const string csv = "Value;NodeId\r\n42;\"nsu=urn:test;s=Speed\"\r\n\r\n\"1,5\";\"nsu=urn:test;s=Ratio\"\r\n";
        var read = SnapshotCsv.Read(new StringReader(csv), "edited");

        Assert.Collection(read.Items,
            i => Assert.Equal(("nsu=urn:test;s=Speed", "nsu=urn:test;s=Speed", "42", "Good"), (i.Name, i.NodeId, i.Value, i.Status)),
            i => Assert.Equal(("nsu=urn:test;s=Ratio", "1,5"), (i.NodeId, i.Value)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Name,Value\nSpeed,1")]
    [InlineData("NodeId,Value\n,1")]
    public void Csv_without_node_ids_is_refused(string csv) =>
        Assert.Throws<InvalidDataException>(() => SnapshotCsv.Read(new StringReader(csv), "x"));

    [Fact]
    public void The_store_exports_and_imports_csv_by_file_extension()
    {
        var store = new SnapshotStore(Path.Combine(_dir, "snapshots"));
        var path = Path.Combine(_dir, "Line 1.csv");
        SnapshotStore.Export(Sample(), path);
        Assert.StartsWith("Name,NodeId,Value,Status", File.ReadAllText(path), StringComparison.Ordinal);

        var imported = store.Import(path);
        Assert.Equal("Line 1", imported.Name);
        Assert.Equal(Sample().Items, imported.Items);
        Assert.Single(store.List());

        File.WriteAllText(path, "Name\nSpeed\n");
        var error = Assert.Throws<InvalidDataException>(() => store.Import(path));
        Assert.StartsWith("Line 1.csv:", error.Message, StringComparison.Ordinal);
    }
}
