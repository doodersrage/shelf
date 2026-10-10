using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Localization;

// Counts in words. Each language forms plurals its own way, so one and many are separate sentences.
public static class Counts
{
    public static string Books(int count) => count == 1 ? T("1 book") : T("{0} books", count);

    public static string Files(int count) => count == 1 ? T("1 file") : T("{0} files", count);

    // A size, in the units the reader's language writes (Mo in French, for one).
    public static string Size(long bytes)
    {
        const double megabyte = 1024 * 1024;
        if (bytes >= megabyte * 1024)
        {
            return T("{0:0.##} GB", bytes / (megabyte * 1024));
        }

        return bytes >= megabyte ? T("{0:0.#} MB", bytes / megabyte) : T("{0} KB", Math.Max(1, bytes / 1024));
    }
}
