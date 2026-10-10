using static Shelf.Api.Localization.Words;

namespace Shelf.Api.Localization;

// Counts in words. Each language forms plurals its own way, so one and many are separate sentences.
public static class Counts
{
    public static string Books(int count) => count == 1 ? T("1 book") : T("{0} books", count);
}
