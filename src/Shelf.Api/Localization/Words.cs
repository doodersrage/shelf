using System.Collections.Frozen;
using System.Globalization;
using System.Text.Json;

namespace Shelf.Api.Localization;

// The words on Shelf's pages, in the reader's language. English is written in the code itself and is the key to
// every table, so a sentence missing from a table shows in English rather than not at all. A sentence with a value
// in it is a format, with {0} where the value goes. The tables are Localization/<language>.json, embedded in the app;
// a test checks that every sentence in the code is in every table, with the same placeholders.
public static class Words
{
    public const string English = "en";

    // The languages Shelf's pages can be in, with their own names for the language picker.
    public static readonly IReadOnlyList<(string Code, string Name)> Languages =
    [
        ("en", "English"),
        ("es", "Español"),
        ("fr", "Français"),
        ("de", "Deutsch"),
    ];

    private static readonly FrozenDictionary<string, FrozenDictionary<string, string>> Tables = Load();

    public static bool IsSupported(string? code) => Languages.Any(language => language.Code == code);

    public static string Current => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName is var code && IsSupported(code) ? code : English;

    public static string T(string text) =>
        Current != English && Tables.TryGetValue(Current, out var table) && table.TryGetValue(text, out var translated) ? translated : text;

    public static string T(string format, params object?[] values) =>
        string.Format(CultureInfo.CurrentCulture, T(format), values);

    // A sentence kept in English now (in the activity log, say) and translated with T when it is shown.
    public static string Say(string text) => text;

    // A whole table, for the scripts that run in the browser (see words.js).
    public static IReadOnlyDictionary<string, string> Table(string code) =>
        Tables.TryGetValue(code, out var table) ? table : FrozenDictionary<string, string>.Empty;

    private static FrozenDictionary<string, FrozenDictionary<string, string>> Load()
    {
        var assembly = typeof(Words).Assembly;
        var tables = new Dictionary<string, FrozenDictionary<string, string>>();
        foreach (var (code, _) in Languages.Where(language => language.Code != English))
        {
            using var stream = assembly.GetManifestResourceStream($"Shelf.Api.Localization.{code}.json");
            if (stream is null)
            {
                continue;
            }

            var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
            tables[code] = entries.Where(entry => !string.IsNullOrWhiteSpace(entry.Value)).ToFrozenDictionary(entry => entry.Key, entry => entry.Value);
        }

        return tables.ToFrozenDictionary();
    }
}
