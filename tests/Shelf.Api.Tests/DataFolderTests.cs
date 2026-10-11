using Shelf.Api.Data;

namespace Shelf.Api.Tests;

public sealed class DataFolderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"shelf-data-{Guid.NewGuid():N}");

    public DataFolderTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(_folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        Directory.Delete(_folder, recursive: true);
    }

    [Fact]
    public void A_fresh_folder_or_one_with_a_shelf_in_it_is_fine()
    {
        var database = Path.Combine(_folder, "shelf.db");
        Assert.Null(DataFolder.Problem(database));
        File.WriteAllText(database, "");
        Assert.Null(DataFolder.Problem(database));
        Assert.Single(Directory.GetFiles(_folder));
    }

    [Fact]
    public void A_docker_volume_copied_whole_is_found_one_folder_down()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "_data"));
        File.WriteAllText(Path.Combine(_folder, "_data", "shelf.db"), "");

        Assert.Contains("_data", DataFolder.Problem(Path.Combine(_folder, "shelf.db")));
    }

    [Fact]
    public void A_folder_that_cannot_be_written_or_does_not_exist_is_said_so()
    {
        Assert.Contains("does not exist", DataFolder.Problem(Path.Combine(_folder, "missing", "shelf.db")));
        if (OperatingSystem.IsWindows() || Environment.UserName == "root")
        {
            return;
        }

        File.SetUnixFileMode(_folder, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        Assert.Contains("cannot create files", DataFolder.Problem(Path.Combine(_folder, "shelf.db")));
    }
}
