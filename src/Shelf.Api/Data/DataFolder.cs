namespace Shelf.Api.Data;

// Before SQLite is opened: whether its file can be, and if not, why, in words a person moving a shelf can act on.
// SQLite's own answer to all of these is "unable to open database file".
public static class DataFolder
{
    public static string? Problem(string databasePath)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(databasePath)) ?? ".";
        var user = Environment.UserName;
        if (!Directory.Exists(folder))
        {
            return $"The folder for the database, {folder}, does not exist, or user {user} cannot look inside it. Create it, or check the path given for the database.";
        }

        if (!File.Exists(databasePath))
        {
            // A Docker volume copied whole keeps its files one folder down.
            var nested = Path.Combine(folder, "_data", Path.GetFileName(databasePath));
            if (File.Exists(nested))
            {
                return $"There is no {Path.GetFileName(databasePath)} in {folder}, but there is one in {Path.Combine(folder, "_data")}. A Docker volume keeps its files in _data: move what is in it up into {folder}, or mount the _data folder in its place.";
            }

            return CanWrite(folder) ? null
                : $"User {user} cannot create files in {folder}, so Shelf cannot start a new shelf there. Make the folder theirs (for Docker, chown -R 1654:1654 the folder on the host, or the PUID:PGID you set), and check it is not mounted read-only.";
        }

        try
        {
            using var file = new FileStream(databasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return $"User {user} cannot read and write {databasePath}. Make it and the files beside it theirs (for Docker, chown -R 1654:1654 the folder on the host, or the PUID:PGID you set).";
        }

        // SQLite writes its journal beside the database.
        return CanWrite(folder) ? null
            : $"User {user} can open {databasePath} but cannot write in {folder}, where SQLite keeps its journal. Make the folder theirs too.";
    }

    private static bool CanWrite(string folder)
    {
        var probe = Path.Combine(folder, $".shelf-write-check-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
