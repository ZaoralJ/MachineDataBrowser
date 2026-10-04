using MachineDataBrowser.Cli.Tui;
using Xunit;

namespace MachineDataBrowser.Cli.Tests;

public sealed class PathCompletionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mdbrowser-paths").FullName;

    public PathCompletionTests()
    {
        File.WriteAllText(Path.Combine(_dir, "line1.mdbsession"), "{}");
        File.WriteAllText(Path.Combine(_dir, "line2.mdbsession"), "{}");
        File.WriteAllText(Path.Combine(_dir, "night.db"), string.Empty);
        Directory.CreateDirectory(Path.Combine(_dir, "recordings"));
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void One_match_completes_and_folders_get_a_slash()
    {
        Assert.Equal(Path.Combine(_dir, "night.db"), PathCompletion.Complete(Path.Combine(_dir, "ni")).Text);
        Assert.Equal(Path.Combine(_dir, "recordings") + "/", PathCompletion.Complete(Path.Combine(_dir, "rec")).Text);
    }

    [Fact]
    public void Several_matches_complete_their_common_start_and_are_listed()
    {
        var (text, matches) = PathCompletion.Complete(Path.Combine(_dir, "l"));
        Assert.Equal(Path.Combine(_dir, "line"), text);
        Assert.Equal(["line1.mdbsession", "line2.mdbsession"], matches);
    }

    [Fact]
    public void No_match_keeps_the_text_and_home_is_expanded()
    {
        Assert.Equal((Path.Combine(_dir, "zz"), 0), (PathCompletion.Complete(Path.Combine(_dir, "zz")).Text, PathCompletion.Complete(Path.Combine(_dir, "zz")).Matches.Count));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + "/x.db", PathCompletion.Expand("~/x.db"));
        Assert.Equal("x.db", PathCompletion.Expand("x.db"));
    }
}
